using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deckwraith.Application.Hosting;
using Deckwraith.Hosting;
using ElectronNET.API;
using ElectronNET.API.Entities;
using Microsoft.Extensions.FileProviders;

var deckPath = ResolveDeckPath(args);
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseElectron(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 4 * 1024 * 1024);
var rendererRoot = ResolveRendererRoot(builder.Environment.ContentRootPath);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

using var runtime = await DeckwraithHost.OpenAsync(deckPath);
var app = builder.Build();
app.Use(async (context, next) =>
{
    if (context.Connection.RemoteIpAddress is not null &&
        !IPAddress.IsLoopback(context.Connection.RemoteIpAddress))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    context.Response.Headers.ContentSecurityPolicy =
        "default-src 'self'; connect-src 'self'; img-src 'self' data:; " +
        "style-src 'self' 'unsafe-inline'; script-src 'self'; frame-ancestors 'none'";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next().ConfigureAwait(false);
});
if (rendererRoot is not null)
{
    var rendererFiles = new PhysicalFileProvider(rendererRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = rendererFiles });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = rendererFiles });
}
else
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

app.MapGet("/api/v1/status", () => Results.Json(new
{
    protocolVersion = HostProtocol.CurrentVersion,
    eventCursor = runtime.LatestEventCursor,
}, ProtocolJson.Options));

app.MapPost("/api/v1/request", async (
    HostRequest request,
    CancellationToken cancellationToken) =>
{
    try
    {
        var response = await runtime.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        return Results.Json(response, ProtocolJson.Options);
    }
    catch (HostProtocolException exception)
    {
        return Results.Json(
            new HostProtocolError(exception.Code, exception.Message, false),
            ProtocolJson.Options,
            statusCode: StatusCodes.Status400BadRequest);
    }
});

app.MapGet("/api/v1/events", async (
    long? after,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-cache, no-store";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.ContentType = "text/event-stream";
    try
    {
        await foreach (var hostEvent in runtime.ReadEventsAsync(
            after ?? 0, cancellationToken).ConfigureAwait(false))
        {
            await context.Response.WriteAsync(
                $"id: {hostEvent.Cursor}\nevent: host\ndata: " +
                JsonSerializer.Serialize(hostEvent, ProtocolJson.Options) +
                "\n\n",
                cancellationToken).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
    catch (HostEventGapException exception)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(
            new
            {
                code = exception.Code,
                exception.Message,
                exception.RequestedCursor,
                exception.OldestCursor,
            },
            ProtocolJson.Options,
            cancellationToken).ConfigureAwait(false);
    }
});

if (rendererRoot is not null)
{
    var rendererIndex = Path.Combine(rendererRoot, "index.html");
    app.MapFallback(() => Results.File(rendererIndex, "text/html"));
}
else
{
    app.MapFallbackToFile("index.html");
}
await app.StartAsync();
if (HybridSupport.IsElectronActive)
{
    var window = await Electron.WindowManager.CreateWindowAsync(new BrowserWindowOptions
    {
        Width = 1440,
        Height = 960,
        MinWidth = 1040,
        MinHeight = 720,
        Show = false,
        BackgroundColor = "#090b10",
        WebPreferences = new WebPreferences
        {
            ContextIsolation = true,
            NodeIntegration = false,
            Sandbox = true,
        },
    });
    window.SetTitle("Deckwraith");
    window.OnReadyToShow += window.Show;
    window.OnClosed += app.Lifetime.StopApplication;
}

await app.WaitForShutdownAsync();

static string ResolveDeckPath(string[] arguments)
{
    var index = Array.FindIndex(
        arguments, argument => StringComparer.Ordinal.Equals(argument, "--deck-path"));
    if (index >= 0 && index + 1 < arguments.Length &&
        !string.IsNullOrWhiteSpace(arguments[index + 1]))
    {
        return Path.GetFullPath(arguments[index + 1]);
    }

    var configured = Environment.GetEnvironmentVariable("DECKWRAITH_DECK_PATH");
    if (!string.IsNullOrWhiteSpace(configured))
    {
        return Path.GetFullPath(configured);
    }

    return Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Deckwraith",
        "deck-state");
}

static string? ResolveRendererRoot(string contentRoot)
{
    var candidates = new[]
    {
        Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        Path.Combine(contentRoot, "wwwroot"),
        Path.GetFullPath(Path.Combine(contentRoot, "ui", "dist")),
        Path.GetFullPath(Path.Combine(contentRoot, "..", "..", "ui", "dist")),
    };

    return candidates.FirstOrDefault(candidate =>
        File.Exists(Path.Combine(candidate, "index.html")));
}

internal static class ProtocolJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
