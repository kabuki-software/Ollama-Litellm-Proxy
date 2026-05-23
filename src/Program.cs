using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OllamaLiteLLMProxy;

var builder = WebApplication.CreateBuilder(args);

// Enables running as a Windows Service (SCM handshake, graceful stop).
// When run interactively (dotnet run / terminal) this call is a no-op.
builder.Host.UseWindowsService();

// Remove explicit logging configuration to allow appsettings.json to control logging
// builder.Logging.ClearProviders();
// builder.Logging.AddConsole();

builder.Services.AddSingleton<StandardTransform>();

// Add YARP reverse proxy
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms<StandardTransform>();

var app = builder.Build();

// Optionally, keep a root endpoint
app.MapGet("/", () => "Ollama is running");

// Ollama-compatible synthetic endpoints. These are registered BEFORE MapReverseProxy
// so YARP never forwards them and we cannot accidentally double-write the response.
app.MapGet("/api/version", () => Results.Json(new { version = "0.9.6" }));

app.MapPost("/api/show", async (HttpContext httpContext, ILoggerFactory loggerFactory) =>
{
    var logger = loggerFactory.CreateLogger("ApiShow");
    string? model = null;

    try
    {
        using var reader = new StreamReader(httpContext.Request.Body, System.Text.Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(httpContext.RequestAborted);

        if (!string.IsNullOrWhiteSpace(body))
        {
            var json = JsonConvert.DeserializeObject(body) as JObject;
            model = json?.Value<string>("model");
        }
    }
    catch (JsonException ex)
    {
        logger.LogWarning(ex, "/api/show: invalid JSON body");
        return Results.BadRequest(new { error = "Invalid JSON body" });
    }
    catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
    {
        return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
    }

    var answer = new GemmaModel
    {
        Capabilities = new List<string> { "chat" },
        ModelInfo = new ModelInfo { Architecture = model ?? string.Empty },
    };

    return Results.Json(answer);
});

// Log all proxy requests and their redirections
app.Use(async (context, next) =>
{
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ProxyLogger");
    var originalPath = context.Request.Path + context.Request.QueryString;
    logger.LogInformation("Incoming request: {method} {path}", context.Request.Method, originalPath);

    // Capture the response before and after proxying
    await next();

    // If the request was proxied, YARP sets this feature
    var proxyFeature = context.Features.Get<Yarp.ReverseProxy.Forwarder.IForwarderErrorFeature>();
    if (proxyFeature != null)
    {
        logger.LogWarning("Proxy error: {error}", proxyFeature.Error);
    }
    else if (context.Items.TryGetValue("YarpDestination", out var destination))
    {
        logger.LogInformation("Request {path} was proxied to {destination}", originalPath, destination);
    }
    else
    {
        // Try to log the destination from YARP's context
        var dest = context.Request.Headers["X-Forwarded-Host"].ToString();
        if (!string.IsNullOrEmpty(dest))
        {
            logger.LogInformation("Request {path} was proxied to {destination}", originalPath, dest);
        }
    }
});

// Map the proxy endpoints with a callback to log the destination
app.MapReverseProxy(proxyPipeline =>
{
    proxyPipeline.Use(async (context, next) =>
    {
        // YARP will set the destination info in the cluster/destination features
        var destination = context.Request.Headers["X-Forwarded-Host"].ToString();
        if (!string.IsNullOrEmpty(destination))
        {
            context.Items["YarpDestination"] = destination;
        }
        await next();
    });
});

app.Run();
