using Microsoft.AspNetCore.Http.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace OllamaLiteLLMProxy;

public class StandardTransform : ITransformProvider
{
    // Stable timestamp used for synthetic Ollama model metadata.
    private const string SyntheticModifiedAt = "2024-02-24T18:29:19.5508829+01:00";
    private const long SyntheticSize = 1966917458L;

    private readonly ILogger<StandardTransform> _logger;

    public StandardTransform(ILogger<StandardTransform> logger)
    {
        _logger = logger;
    }

    public void Apply(TransformBuilderContext context)
    {
        context.UseDefaultForwarders = true;

        context.AddRequestTransform(async transformContext =>
        {
            var httpContext = transformContext.HttpContext;
            var path = httpContext.Request.Path;

            try
            {
                if (path == "/api/tags")
                {
                    transformContext.Path = "/models";
                    _logger.LogInformation("Proxy: Request path rewritten from /api/tags to /models");
                }
                else if (path == "/v1/chat/completions")
                {
                    transformContext.Path = "/chat/completions";
                    _logger.LogInformation("Proxy: Request path rewritten from /v1/chat/completions to /chat/completions");

                    // DeepSeek thinking-mode models require `reasoning_content` to be present on
                    // every assistant message in the conversation history. Clients such as GitHub
                    // Copilot are unaware of this field and omit it, causing HTTP 400 errors.
                    // We fix this transparently by injecting an empty `reasoning_content` on any
                    // assistant message that is missing it before the request is forwarded.
                    await InjectReasoningContentAsync(transformContext, cancellationToken: httpContext.RequestAborted);
                }

                _logger.LogDebug(
                    "Proxy: Request {Url} method {Method} proxied to {Path}",
                    httpContext.Request.GetDisplayUrl(),
                    httpContext.Request.Method,
                    transformContext.Path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Proxy: Error in request transform for {Url}", httpContext.Request.GetDisplayUrl());
            }
        });

        context.CopyResponseHeaders = true;

        context.AddResponseTransform(async transformContext =>
        {
            var httpContext = transformContext.HttpContext;
            var response = transformContext.ProxyResponse;
            var cancellationToken = httpContext.RequestAborted;

            byte[]? originalBytes = null;
            try
            {
                var localPath = response?.RequestMessage?.RequestUri?.LocalPath;
                if (response is not null
                    && string.Equals(localPath, "/models", StringComparison.Ordinal)
                    && response.IsSuccessStatusCode)
                {
                    // YARP disables handler auto-decompression, so the content stream is the
                    // original encoded payload and the copied Content-Encoding still says gzip.
                    // Parse a decoded copy, but keep the encoded bytes for a failed rewrite.
                    string content;
                    (originalBytes, content) = await ReadResponseBodyAsync(response, cancellationToken);
                    SourceRoot? source;
                    try
                    {
                        source = JsonConvert.DeserializeObject<SourceRoot>(content);
                    }
                    catch (JsonException jex)
                    {
                        _logger.LogWarning(jex, "Proxy: failed to parse /models response. Raw content: {Content}", content);
                        await WriteOriginalResponseAsync(transformContext, originalBytes, cancellationToken);
                        return;
                    }

                    if (source?.data is null)
                    {
                        _logger.LogWarning("Proxy: /models response did not contain a 'data' array. Raw content: {Content}", content);
                        await WriteOriginalResponseAsync(transformContext, originalBytes, cancellationToken);
                        return;
                    }

                    var ollamaModels = new OllamaRoot
                    {
                        models = source.data
                            .Where(m => m is not null && !string.IsNullOrEmpty(m.id))
                            .Select(m => new OllamaModel
                            {
                                name = m.id,
                                model = m.id,
                                modified_at = SyntheticModifiedAt,
                                size = SyntheticSize,
                                digest = ComputeDigest(m.id),
                            })
                            .ToList()
                    };

                    var ollamaJson = JsonConvert.SerializeObject(ollamaModels, Formatting.Indented);

                    transformContext.SuppressResponseBody = true;

                    var modifiedBytes = Encoding.UTF8.GetBytes(ollamaJson);
                    // The copied upstream length/encoding describe the original payload, not this JSON.
                    httpContext.Response.Headers.Remove("Content-Encoding");
                    httpContext.Response.ContentLength = modifiedBytes.Length;
                    httpContext.Response.ContentType = "application/json";

                    await httpContext.Response.Body.WriteAsync(modifiedBytes, cancellationToken);
                }

                _logger.LogDebug("Proxy: Request {Url} proxied", httpContext.Request.GetDisplayUrl());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Client disconnected; nothing to do.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Proxy: Error in response transform for {Url}", httpContext.Request.GetDisplayUrl());
                if (originalBytes is not null)
                    await WriteOriginalResponseAsync(transformContext, originalBytes, cancellationToken);
            }
        });
    }

    public void ValidateCluster(TransformClusterValidationContext context)
    {
        _logger.LogInformation("StandardTransform.ValidateCluster called");
    }

    public void ValidateRoute(TransformRouteValidationContext context)
    {
        _logger.LogInformation("StandardTransform.ValidateRoute called");
    }

    /// <summary>
    /// Reads the request body, finds any assistant messages that are missing
    /// <c>reasoning_content</c>, injects an empty string value, and rewrites
    /// the body so DeepSeek thinking-mode models accept the request.
    /// </summary>
    private async Task InjectReasoningContentAsync(RequestTransformContext transformContext, CancellationToken cancellationToken)
    {
        var httpContext = transformContext.HttpContext;
        byte[]? originalBytes = null;
        try
        {
            // Buffer first. Any early return or parse failure happens after the request
            // stream is already consumed, and YARP still has the original Content-Length
            // on ProxyRequest.Content. Leaving that combination in place drops the body.
            using (var buffer = new System.IO.MemoryStream())
            {
                await httpContext.Request.Body.CopyToAsync(buffer, cancellationToken);
                originalBytes = buffer.ToArray();
            }

            if (originalBytes.Length == 0)
            {
                ReplaceRequestBody(transformContext, originalBytes);
                return;
            }

            var body = Encoding.UTF8.GetString(originalBytes);
            if (string.IsNullOrWhiteSpace(body))
            {
                ReplaceRequestBody(transformContext, originalBytes);
                return;
            }

            var json = JObject.Parse(body);
            var messages = json["messages"] as JArray;
            if (messages is null)
            {
                ReplaceRequestBody(transformContext, originalBytes);
                return;
            }

            bool modified = false;
            foreach (var message in messages)
            {
                if (message is not JObject msg) continue;
                var role = msg.Value<string>("role");
                if (!string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)) continue;

                if (msg["reasoning_content"] is null)
                {
                    msg["reasoning_content"] = string.Empty;
                    modified = true;
                }
            }

            var newBodyBytes = modified
                ? Encoding.UTF8.GetBytes(json.ToString(Formatting.None))
                : originalBytes;

            ReplaceRequestBody(transformContext, newBodyBytes);

            if (modified)
                _logger.LogDebug("Proxy: Injected reasoning_content into assistant messages for DeepSeek compatibility");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Proxy: Failed to inject reasoning_content; request will be forwarded unmodified");
            if (originalBytes is not null)
            {
                ReplaceRequestBody(transformContext, originalBytes);
            }
            else
            {
                // The stream was consumed before we could buffer it. Drop the stale length
                // so YARP does not promise bytes it can no longer read.
                httpContext.Request.Body = Stream.Null;
                httpContext.Request.ContentLength = 0;
                if (transformContext.ProxyRequest.Content is not null)
                    transformContext.ProxyRequest.Content.Headers.ContentLength = 0;
            }
        }
    }

    /// <summary>
    /// Puts a fully-buffered body back on the request. YARP's StreamCopyHttpContent
    /// reads HttpContext.Request.Body lazily, but the outbound Content-Length was
    /// copied onto ProxyRequest.Content before transforms ran. Both must match.
    /// </summary>
    private static void ReplaceRequestBody(RequestTransformContext transformContext, byte[] body)
    {
        var httpContext = transformContext.HttpContext;
        httpContext.Request.Body = new System.IO.MemoryStream(body);
        httpContext.Request.ContentLength = body.Length;

        var proxyContent = transformContext.ProxyRequest.Content;
        if (proxyContent is not null)
            proxyContent.Headers.ContentLength = body.Length;
    }

    /// <summary>
    /// Returns the original encoded bytes plus the text to parse. YARP's handler has
    /// automatic decompression disabled, so ReadAsStringAsync would return gzip bytes
    /// as a string and a failed rewrite would then write the decompressed JSON under
    /// the original Content-Encoding and Content-Length.
    /// </summary>
    private static async Task<(byte[] Raw, string Text)> ReadResponseBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = response.Content;
        await content.LoadIntoBufferAsync(cancellationToken);

        var raw = ReadBufferedContent(content);
        var encoding = content.Headers.ContentEncoding.ToString() ?? string.Empty;
        var text = Decode(raw, encoding);
        return (raw, text);
    }

    private static string Decode(byte[] raw, string encoding)
    {
        Stream stream = new MemoryStream(raw);
        if (encoding.Contains("gzip", StringComparison.OrdinalIgnoreCase))
            stream = new GZipStream(stream, CompressionMode.Decompress);
        else if (encoding.Contains("br", StringComparison.OrdinalIgnoreCase))
            stream = new BrotliStream(stream, CompressionMode.Decompress);
        else if (encoding.Contains("deflate", StringComparison.OrdinalIgnoreCase))
            stream = new DeflateStream(stream, CompressionMode.Decompress);

        using (stream)
        using (var reader = new StreamReader(stream, Encoding.UTF8))
            return reader.ReadToEnd();
    }

    private static byte[] ReadBufferedContent(HttpContent content)
    {
        var buffered = typeof(HttpContent)
            .GetField("_bufferedContent", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(content) as MemoryStream
            ?? throw new InvalidOperationException("Response content was not buffered.");

        var previous = buffered.Position;
        buffered.Position = 0;
        var raw = buffered.ToArray();
        buffered.Position = previous;
        return raw;
    }

    /// <summary>
    /// Writes the original upstream bytes after a failed rewrite. Headers (including
    /// Content-Encoding and the original Content-Length) were already copied, so the
    /// body must be the original encoded payload. Suppress the YARP copy or the client
    /// gets this body plus an empty remainder against the stale length.
    /// </summary>
    private static async Task WriteOriginalResponseAsync(ResponseTransformContext transformContext, byte[] originalBytes, CancellationToken cancellationToken)
    {
        transformContext.SuppressResponseBody = true;

        var httpContext = transformContext.HttpContext;
        httpContext.Response.ContentLength = originalBytes.Length;
        await httpContext.Response.Body.WriteAsync(originalBytes, cancellationToken);
    }

    // Stable, deterministic digest so clients that cache by digest see a consistent identity per model id.
    private static string ComputeDigest(string id)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(id), hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
