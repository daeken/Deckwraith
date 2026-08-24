using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deckwraith.Application.Hosting;
using Deckwraith.Hosting;
using ElectronNET.API;
using ElectronNET.API.Entities;
using Microsoft.Extensions.FileProviders;

var deckPath = DesktopDeckPreferences.ResolveDeckPath(args);
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseElectron(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 4 * 1024 * 1024);
var rendererRoot = ResolveRendererRoot(builder.Environment.ContentRootPath);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

using var session = await DesktopDeckSession.OpenAsync(deckPath);
BrowserWindow? mainWindow = null;
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
    eventCursor = session.LatestEventCursor,
    deckPath = session.DeckPath,
}, ProtocolJson.Options));

app.MapPost("/api/v1/deck/pick", async (DeckPickerRequest request) =>
{
    if (!HybridSupport.IsElectronActive || mainWindow is null)
    {
        return Results.Json(
            new { code = "native-dialog-unavailable", message = "Enter the deck folder directly in this host." },
            ProtocolJson.Options,
            statusCode: StatusCodes.Status501NotImplemented);
    }

    var selected = await Electron.Dialog.ShowOpenDialogAsync(
        mainWindow,
        new OpenDialogOptions
        {
            Title = "Choose the Deckwraith deck folder",
            ButtonLabel = "Use this folder",
            DefaultPath = FindExistingDirectory(request.DefaultPath ?? session.DeckPath),
            Properties =
            [
                OpenDialogProperty.openDirectory,
                OpenDialogProperty.createDirectory,
                OpenDialogProperty.showHiddenFiles,
            ],
        });
    return Results.Json(new { path = selected.FirstOrDefault() }, ProtocolJson.Options);
});

app.MapPost("/api/v1/project/pick", async (DeckPickerRequest request) =>
{
    if (!HybridSupport.IsElectronActive || mainWindow is null)
    {
        return Results.Json(
            new { code = "native-dialog-unavailable", message = "Enter the project folder directly in this host." },
            ProtocolJson.Options,
            statusCode: StatusCodes.Status501NotImplemented);
    }

    var selected = await Electron.Dialog.ShowOpenDialogAsync(
        mainWindow,
        new OpenDialogOptions
        {
            Title = "Choose the haunt's project folder",
            ButtonLabel = "Use this project",
            DefaultPath = FindExistingDirectory(request.DefaultPath ?? session.DeckPath),
            Properties =
            [
                OpenDialogProperty.openDirectory,
                OpenDialogProperty.showHiddenFiles,
            ],
        });
    return Results.Json(new { path = selected.FirstOrDefault() }, ProtocolJson.Options);
});

app.MapPost("/api/v1/deck/select", async (
    DeckSelectionRequest request,
    CancellationToken cancellationToken) =>
{
    try
    {
        var selected = await session.SelectAsync(request.Path, cancellationToken)
            .ConfigureAwait(false);
        await DesktopDeckPreferences.SaveDeckPathAsync(
            selected.DeckPath, cancellationToken).ConfigureAwait(false);
        return Results.Json(selected, ProtocolJson.Options);
    }
    catch (DesktopDeckException exception)
    {
        return Results.Json(
            new { code = exception.Code, exception.Message },
            ProtocolJson.Options,
            statusCode: StatusCodes.Status400BadRequest);
    }
});

app.MapPost("/api/v1/request", async (
    HostRequest request,
    CancellationToken cancellationToken) =>
{
    try
    {
        var response = await session.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
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
        await foreach (var hostEvent in session.ReadEventsAsync(
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
    mainWindow = await Electron.WindowManager.CreateWindowAsync(new BrowserWindowOptions
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
    mainWindow.SetTitle("Deckwraith");
    mainWindow.OnReadyToShow += mainWindow.Show;
    mainWindow.OnClosed += app.Lifetime.StopApplication;
}

await app.WaitForShutdownAsync();

static string FindExistingDirectory(string path)
{
    var candidate = new DirectoryInfo(DesktopDeckPreferences.NormalizePath(path));
    while (candidate is not null && !candidate.Exists)
    {
        candidate = candidate.Parent;
    }

    return candidate?.FullName ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
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

internal sealed record DeckPickerRequest(string? DefaultPath);

internal sealed record DeckSelectionRequest(string Path);

internal sealed record DeckSelectionResult(string DeckPath, bool Initialized);

internal sealed class DesktopDeckException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal sealed class DesktopDeckSession : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DeckwraithHost _runtime;
    private bool _disposed;

    private DesktopDeckSession(string deckPath, DeckwraithHost runtime)
    {
        DeckPath = deckPath;
        _runtime = runtime;
    }

    public string DeckPath { get; private set; }

    public long LatestEventCursor => _runtime.LatestEventCursor;

    public static async Task<DesktopDeckSession> OpenAsync(
        string deckPath,
        CancellationToken cancellationToken = default)
    {
        var normalized = DesktopDeckPreferences.NormalizePath(deckPath);
        return new DesktopDeckSession(
            normalized,
            await DeckwraithHost.OpenAsync(normalized, cancellationToken: cancellationToken)
                .ConfigureAwait(false));
    }

    public async Task<DeckSelectionResult> SelectAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var normalized = DesktopDeckPreferences.NormalizePath(path);
        if (File.Exists(normalized))
        {
            throw new DesktopDeckException(
                "deck-path-is-file", $"Deck folder '{normalized}' is an existing file.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (File.Exists(Path.Combine(DeckPath, "deck.json")) &&
                !DesktopDeckPreferences.PathsEqual(DeckPath, normalized))
            {
                throw new DesktopDeckException(
                    "deck-already-open", "Choose a different deck before initializing the current one.");
            }

            if (!DesktopDeckPreferences.PathsEqual(DeckPath, normalized))
            {
                var next = await DeckwraithHost.OpenAsync(
                    normalized, cancellationToken: cancellationToken).ConfigureAwait(false);
                var previous = _runtime;
                _runtime = next;
                DeckPath = normalized;
                previous.Dispose();
            }

            return new DeckSelectionResult(
                DeckPath,
                File.Exists(Path.Combine(DeckPath, "deck.json")));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HostResponse> ExecuteAsync(
        HostRequest request,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await _runtime.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public IAsyncEnumerable<HostEvent> ReadEventsAsync(
        long afterCursor,
        CancellationToken cancellationToken = default) =>
        _runtime.ReadEventsAsync(afterCursor, cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _runtime.Dispose();
        _gate.Dispose();
    }
}

internal static class DesktopDeckPreferences
{
    private const string DeckPathEnvironmentVariable = "DECKWRAITH_DECK_PATH";
    private static readonly JsonSerializerOptions PreferenceJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string ResolveDeckPath(string[] arguments)
    {
        var index = Array.FindIndex(
            arguments, argument => StringComparer.Ordinal.Equals(argument, "--deck-path"));
        if (index >= 0 && index + 1 < arguments.Length &&
            !string.IsNullOrWhiteSpace(arguments[index + 1]))
        {
            return NormalizePath(arguments[index + 1]);
        }

        var configured = Environment.GetEnvironmentVariable(DeckPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return NormalizePath(configured);
        }

        var preference = ReadDeckPath();
        if (!string.IsNullOrWhiteSpace(preference))
        {
            return NormalizePath(preference);
        }

        var legacy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Deckwraith",
            "deck-state");
        if (File.Exists(Path.Combine(legacy, "deck.json")))
        {
            return Path.GetFullPath(legacy);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".deckwraith");
    }

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new DesktopDeckException("deck-path-required", "Choose a folder for the deck.");
        }

        var trimmed = path.Trim();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (StringComparer.Ordinal.Equals(trimmed, "~"))
        {
            trimmed = home;
        }
        else if (trimmed.StartsWith($"~{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                 trimmed.StartsWith($"~{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            trimmed = Path.Combine(home, trimmed[2..]);
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new DesktopDeckException("invalid-deck-path", $"Deck folder '{path}' is not valid.");
        }
    }

    public static bool PathsEqual(string left, string right) =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase.Equals(NormalizePath(left), NormalizePath(right))
            : StringComparer.Ordinal.Equals(NormalizePath(left), NormalizePath(right));

    public static async Task SaveDeckPathAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var preferencePath = PreferencePath();
        Directory.CreateDirectory(Path.GetDirectoryName(preferencePath)!);
        var temporary = $"{preferencePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(new DesktopPreferences(NormalizePath(path)), PreferenceJson) + "\n",
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, preferencePath, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string? ReadDeckPath()
    {
        var path = PreferencePath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DesktopPreferences>(
                File.ReadAllText(path), PreferenceJson)?.DeckPath;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string PreferencePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Deckwraith",
        "desktop.json");

    private sealed record DesktopPreferences(string DeckPath);
}
