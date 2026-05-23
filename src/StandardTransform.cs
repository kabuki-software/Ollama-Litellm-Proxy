using Microsoft.AspNetCore.Http.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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

            try
            {
                var localPath = response?.RequestMessage?.RequestUri?.LocalPath;
                if (response is not null
                    && string.Equals(localPath, "/models", StringComparison.Ordinal)
                    && response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync(cancellationToken);
                    SourceRoot? source;
                    try
                    {
                        source = JsonConvert.DeserializeObject<SourceRoot>(content);
                    }
                    catch (JsonException jex)
                    {
                        _logger.LogWarning(jex, "Proxy: failed to parse /models response. Raw content: {Content}", content);
                        return;
                    }

                    if (source?.data is null)
                    {
                        _logger.LogWarning("Proxy: /models response did not contain a 'data' array. Raw content: {Content}", content);
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
        try
        {
            string body;
            using (var reader = new System.IO.StreamReader(
                httpContext.Request.Body,
                System.Text.Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true))
            {
                body = await reader.ReadToEndAsync(cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(body))
                return;

            var json = JObject.Parse(body);
            var messages = json["messages"] as JArray;
            if (messages is null)
                return;

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

            var newBodyBytes = System.Text.Encoding.UTF8.GetBytes(
                modified ? json.ToString(Formatting.None) : body);

            // Always restore the body stream — even when unmodified, it was already
            // read to the end and must be rewound so YARP can forward it.
            httpContext.Request.Body = new System.IO.MemoryStream(newBodyBytes);
            httpContext.Request.ContentLength = newBodyBytes.Length;

            // YARP builds the outbound HttpRequestMessage (and its StreamCopyHttpContent)
            // BEFORE request transforms run, copying the original Content-Length onto
            // ProxyRequest.Content.Headers. That header — not HttpRequest.ContentLength —
            // is what gets written to the wire. If we don't sync it with the new payload
            // length, YARP will throw: "Sent N request content bytes, but Content-Length
            // promised M." Note this matters even when `modified` is false, because
            // JObject.ToString(Formatting.None) compacts whitespace and changes the size.
            var proxyContent = transformContext.ProxyRequest.Content;
            if (proxyContent is not null)
            {
                proxyContent.Headers.ContentLength = newBodyBytes.Length;
            }

            if (modified)
                _logger.LogDebug("Proxy: Injected reasoning_content into assistant messages for DeepSeek compatibility");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Proxy: Failed to inject reasoning_content; request will be forwarded unmodified");
        }
    }

    // Stable, deterministic digest so clients that cache by digest see a consistent identity per model id.
    private static string ComputeDigest(string id)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(id), hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
