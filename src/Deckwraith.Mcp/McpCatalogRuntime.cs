using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deckwraith.Application.Abstractions;
using Deckwraith.Core.Archives;
using Deckwraith.Core.Naming;
using Deckwraith.Core.Serialization;
using Deckwraith.Core.State;

namespace Deckwraith.Mcp;

public sealed partial class McpCatalogRuntime : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _rootPath;
    private readonly IDeckStateStore _deckState;
    private readonly IAgentArchive _archive;
    private readonly ICheckpointStore _checkpoints;
    private readonly IDeckClock _clock;
    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private readonly SemaphoreSlim _clientGate = new(1, 1);
    private readonly ConcurrentDictionary<string, ClientLease> _clients =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CatalogLease> _catalogs =
        new(StringComparer.Ordinal);
    private bool _disposed;

    public McpCatalogRuntime(
        string rootPath,
        IDeckStateStore deckState,
        IAgentArchive archive,
        ICheckpointStore checkpoints,
        IDeckClock? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _deckState = deckState;
        _archive = archive;
        _checkpoints = checkpoints;
        _clock = clock ?? SystemDeckClock.Instance;
    }

    public async Task<string> ConfigureServersAsync(
        IReadOnlyList<McpServerDefinition> servers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ValidateServers(servers);
        var registry = new McpServerRegistry(
            McpServerRegistry.CurrentSchemaVersion,
            servers.OrderBy(server => server.Id, StringComparer.Ordinal).ToArray(),
            _clock.UtcNow);
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAsync(ServerRegistryPath, registry, cancellationToken).ConfigureAwait(false);
            Invalidate();
        }
        finally
        {
            _configurationGate.Release();
        }

        return await _checkpoints.CheckpointAsync(
            "mcp-servers-configured", null, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> WriteGlobalAssignmentAsync(
        McpAssignmentDocument assignment,
        CancellationToken cancellationToken = default)
    {
        ValidateAssignment(assignment);
        assignment = Normalize(assignment with { UpdatedAt = _clock.UtcNow });
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAsync(GlobalAssignmentPath, assignment, cancellationToken).ConfigureAwait(false);
            Invalidate();
        }
        finally
        {
            _configurationGate.Release();
        }

        return await _checkpoints.CheckpointAsync(
            "mcp-global-assignment-updated", null, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> WriteWraithAssignmentAsync(
        string wraith,
        McpAssignmentDocument assignment,
        CancellationToken cancellationToken = default)
    {
        ValidateAssignment(assignment);
        var resolved = await _deckState.ResolveWraithAsync(
            CanonicalName.Parse(wraith), cancellationToken).ConfigureAwait(false);
        assignment = Normalize(assignment with { UpdatedAt = _clock.UtcNow });
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAsync(
                WraithAssignmentPath(resolved), assignment, cancellationToken).ConfigureAwait(false);
            Invalidate(resolved.Value);
        }
        finally
        {
            _configurationGate.Release();
        }

        await _archive.AppendAsync(
            new ArchiveEvent(
                resolved.Value,
                "mcp.assignment-changed",
                CanonicalJson.ToElement(new { assignment }),
                Timestamp: _clock.UtcNow),
            cancellationToken).ConfigureAwait(false);
        return await _checkpoints.CheckpointAsync(
            "mcp-wraith-assignment-updated", resolved, null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<McpEffectiveCatalog> GetEffectiveCatalogAsync(
        string wraith,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var resolved = await _deckState.ResolveWraithAsync(
            CanonicalName.Parse(wraith), cancellationToken).ConfigureAwait(false);
        var registry = await ReadOrDefaultAsync(
            ServerRegistryPath,
            McpServerRegistry.Empty(_clock.UtcNow),
            cancellationToken).ConfigureAwait(false);
        if (registry.SchemaVersion != McpServerRegistry.CurrentSchemaVersion)
        {
            throw new DeckStateException(
                $"Unsupported MCP server registry schema {registry.SchemaVersion}.");
        }

        ValidateServers(registry.Servers);
        var global = Normalize(await ReadOrDefaultAsync(
            GlobalAssignmentPath,
            McpAssignmentDocument.Empty(_clock.UtcNow),
            cancellationToken).ConfigureAwait(false));
        var local = Normalize(await ReadOrDefaultAsync(
            WraithAssignmentPath(resolved),
            McpAssignmentDocument.Empty(_clock.UtcNow),
            cancellationToken).ConfigureAwait(false));
        ValidateAssignment(global);
        ValidateAssignment(local);
        var configurationHash = CanonicalJson.Hash(new { registry, global, local });
        if (!forceRefresh &&
            _catalogs.TryGetValue(resolved.Value, out var cached) &&
            StringComparer.Ordinal.Equals(cached.ConfigurationHash, configurationHash))
        {
            return cached.Catalog;
        }

        var includedServers = global.IncludeServers.Concat(local.IncludeServers)
            .ToHashSet(StringComparer.Ordinal);
        var includedTools = global.IncludeTools.Concat(local.IncludeTools)
            .ToHashSet(StringComparer.Ordinal);
        var excludedServers = global.ExcludeServers.Concat(local.ExcludeServers)
            .ToHashSet(StringComparer.Ordinal);
        var excludedTools = global.ExcludeTools.Concat(local.ExcludeTools)
            .ToHashSet(StringComparer.Ordinal);
        var entries = new List<McpCatalogEntry>();
        foreach (var server in registry.Servers.OrderBy(server => server.Id, StringComparer.Ordinal))
        {
            if (excludedServers.Contains(server.Id))
            {
                continue;
            }

            var selectedTools = includedTools
                .Where(selector => selector.StartsWith(server.Id + "/", StringComparison.Ordinal))
                .ToHashSet(StringComparer.Ordinal);
            if (!includedServers.Contains(server.Id) && selectedTools.Count == 0)
            {
                continue;
            }

            var client = await GetClientAsync(server, cancellationToken).ConfigureAwait(false);
            foreach (var tool in await client.ListToolsAsync(cancellationToken).ConfigureAwait(false))
            {
                var qualified = $"{server.Id}/{tool.Name}";
                if ((!includedServers.Contains(server.Id) && !selectedTools.Contains(qualified)) ||
                    excludedTools.Contains(qualified))
                {
                    continue;
                }

                entries.Add(new McpCatalogEntry(
                    qualified,
                    server.Id,
                    tool.Name,
                    $"Deckwraith.Mcp.{NormalizeIdentifier(server.Id)}",
                    BuildCommandName(server.Id, tool.Name),
                    tool.Description,
                    tool.InputSchema.Clone(),
                    tool.OutputSchema?.Clone()));
            }
        }

        entries = ResolveCommandCollisions(entries);
        var ordered = entries
            .OrderBy(entry => entry.PowerShellCommand, StringComparer.Ordinal)
            .ThenBy(entry => entry.QualifiedName, StringComparer.Ordinal)
            .ToArray();
        var contentHash = CanonicalJson.Hash(ordered);
        var catalog = new McpEffectiveCatalog(
            McpEffectiveCatalog.CurrentSchemaVersion,
            resolved.Value,
            contentHash,
            ordered,
            _clock.UtcNow);
        _catalogs[resolved.Value] = new CatalogLease(configurationHash, catalog);
        return catalog;
    }

    public async Task<McpToolCallResult> CallToolAsync(
        string qualifiedTool,
        JsonElement arguments,
        McpInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context.OperationId);
        var resolved = await _deckState.ResolveWraithAsync(
            CanonicalName.Parse(context.Wraith), cancellationToken).ConfigureAwait(false);
        var resolvedHaunt = context.Haunt is null
            ? (CanonicalName?)null
            : await _deckState.ResolveHauntAsync(
                CanonicalName.Parse(context.Haunt), cancellationToken).ConfigureAwait(false);
        context = context with
        {
            Wraith = resolved.Value,
            Haunt = resolvedHaunt?.Value,
        };
        var catalog = await GetEffectiveCatalogAsync(
            resolved.Value, cancellationToken: cancellationToken).ConfigureAwait(false);
        var entry = catalog.Tools.SingleOrDefault(tool =>
            StringComparer.Ordinal.Equals(tool.QualifiedName, qualifiedTool)) ??
            throw new DeckStateException(
                $"MCP tool '{qualifiedTool}' is not assigned to '{resolved.Value}'.");
        var started = await _archive.AppendAsync(
            new ArchiveEvent(
                resolved.Value,
                "mcp.started",
                CanonicalJson.ToElement(new
                {
                    context.OperationId,
                    entry.ServerId,
                    entry.ToolName,
                    arguments,
                }),
                context.Haunt,
                context.RunId,
                context.ShellId,
                context.OperationId,
                _clock.UtcNow),
            cancellationToken).ConfigureAwait(false);
        try
        {
            var server = await ReadServerAsync(entry.ServerId, cancellationToken).ConfigureAwait(false);
            var client = await GetClientAsync(server, cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(server.RequestTimeoutSeconds));
            var result = await client.CallToolAsync(
                entry.ToolName, arguments, timeout.Token).ConfigureAwait(false);
            await _archive.AppendAsync(
                TerminalEvent(
                    resolved,
                    result.IsError ? "mcp.failed" : "mcp.completed",
                    context,
                    started.Sequence,
                    new { result.IsError, result.StructuredContent, result.Content }),
                cancellationToken).ConfigureAwait(false);
            await _checkpoints.CheckpointAsync(
                result.IsError ? "mcp-tool-failed" : "mcp-tool-completed",
                resolved,
                resolvedHaunt,
                cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException)
        {
            if (_clients.TryRemove(entry.ServerId, out var failedClient))
            {
                failedClient.Client.Dispose();
            }

            await _archive.AppendAsync(
                TerminalEvent(
                    resolved,
                    "mcp.outcome-unknown",
                    context,
                    started.Sequence,
                    new { reason = "cancelled-or-timeout", sideEffectMayHaveOccurred = true }),
                CancellationToken.None).ConfigureAwait(false);
            await _checkpoints.CheckpointAsync(
                "mcp-tool-outcome-unknown",
                resolved,
                resolvedHaunt,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            if (exception is McpProtocolException &&
                _clients.TryRemove(entry.ServerId, out var failedClient))
            {
                failedClient.Client.Dispose();
            }

            await _archive.AppendAsync(
                TerminalEvent(
                    resolved,
                    "mcp.failed",
                    context,
                    started.Sequence,
                    new { error = exception.Message, errorType = exception.GetType().FullName }),
                cancellationToken).ConfigureAwait(false);
            await _checkpoints.CheckpointAsync(
                "mcp-tool-failed", resolved, resolvedHaunt, cancellationToken)
                .ConfigureAwait(false);
            throw;
        }
    }

    public void Invalidate(string? wraith = null)
    {
        if (wraith is null)
        {
            _catalogs.Clear();
            return;
        }

        _catalogs.TryRemove(wraith, out _);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var lease in _clients.Values)
        {
            lease.Client.Dispose();
        }

        _clients.Clear();
        _configurationGate.Dispose();
        _clientGate.Dispose();
    }

    private string ServerRegistryPath => Path.Combine(_rootPath, "tools", "mcp-servers.json");

    private string GlobalAssignmentPath => Path.Combine(_rootPath, "tools", "mcp.json");

    private string WraithAssignmentPath(CanonicalName wraith) =>
        Path.Combine(_rootPath, "agents", wraith.Value, "tools", "mcp.json");

    private async Task<McpServerDefinition> ReadServerAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        var registry = await ReadOrDefaultAsync(
            ServerRegistryPath,
            McpServerRegistry.Empty(_clock.UtcNow),
            cancellationToken).ConfigureAwait(false);
        return registry.Servers.SingleOrDefault(server =>
            StringComparer.Ordinal.Equals(server.Id, serverId)) ??
            throw new DeckStateException($"MCP server '{serverId}' is not configured.");
    }

    private async Task<StdioMcpClient> GetClientAsync(
        McpServerDefinition definition,
        CancellationToken cancellationToken)
    {
        await _clientGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var definitionHash = CanonicalJson.Hash(definition);
            if (_clients.TryGetValue(definition.Id, out var existing) &&
                StringComparer.Ordinal.Equals(existing.DefinitionHash, definitionHash))
            {
                return existing.Client;
            }

            var connected = await StdioMcpClient.ConnectAsync(
                definition, _rootPath, cancellationToken).ConfigureAwait(false);
            if (_clients.TryRemove(definition.Id, out var previous))
            {
                previous.Client.Dispose();
            }

            _clients[definition.Id] = new ClientLease(definitionHash, connected);
            return connected;
        }
        finally
        {
            _clientGate.Release();
        }
    }

    private ArchiveEvent TerminalEvent(
        CanonicalName wraith,
        string kind,
        McpInvocationContext context,
        long startedSequence,
        object terminal) =>
        new(
            wraith.Value,
            kind,
            CanonicalJson.ToElement(new
            {
                context.OperationId,
                startedSequence,
                terminal,
            }),
            context.Haunt,
            context.RunId,
            context.ShellId,
            Timestamp: _clock.UtcNow);

    private static List<McpCatalogEntry> ResolveCommandCollisions(
        IReadOnlyList<McpCatalogEntry> entries)
    {
        var collisions = entries
            .GroupBy(entry => entry.PowerShellCommand, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .Select(entry => entry.QualifiedName)
            .ToHashSet(StringComparer.Ordinal);
        return entries.Select(entry => collisions.Contains(entry.QualifiedName)
            ? entry with
            {
                PowerShellCommand = entry.PowerShellCommand + "_" +
                    Convert.ToHexStringLower(SHA256.HashData(
                        Encoding.UTF8.GetBytes(entry.QualifiedName)))[..8],
            }
            : entry).ToList();
    }

    private static string BuildCommandName(string server, string tool)
    {
        var tokens = TokenPattern().Matches(tool)
            .Select(match => match.Value.ToLowerInvariant())
            .ToArray();
        var verb = tokens.FirstOrDefault() switch
        {
            "get" or "list" or "read" or "find" or "search" => "Get",
            "create" or "add" or "new" => "New",
            "set" or "update" or "edit" => "Set",
            "remove" or "delete" => "Remove",
            "test" or "check" => "Test",
            _ => "Invoke",
        };
        var nounTokens = verb == "Invoke" ? tokens : tokens.Skip(1).ToArray();
        var noun = NormalizeIdentifier(server) + string.Concat(
            nounTokens.Select(PascalCase));
        return $"{verb}-Dw{noun}";
    }

    private static string NormalizeIdentifier(string value) =>
        string.Concat(TokenPattern().Matches(value).Select(match => PascalCase(match.Value)));

    private static string PascalCase(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();

    private static McpAssignmentDocument Normalize(McpAssignmentDocument assignment) =>
        assignment with
        {
            IncludeServers = assignment.IncludeServers.Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray(),
            IncludeTools = assignment.IncludeTools.Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray(),
            ExcludeServers = assignment.ExcludeServers.Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray(),
            ExcludeTools = assignment.ExcludeTools.Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray(),
        };

    private static void ValidateServers(IReadOnlyList<McpServerDefinition> servers)
    {
        if (servers.Select(server => server.Id).Distinct(StringComparer.Ordinal).Count() != servers.Count)
        {
            throw new DeckStateException("MCP server IDs must be unique.");
        }

        foreach (var server in servers)
        {
            _ = CanonicalName.Parse(server.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(server.Command);
            if (server.RequestTimeoutSeconds is < 1 or > 3600)
            {
                throw new DeckStateException("MCP request timeouts must be from 1 through 3600 seconds.");
            }

            if (server.EnvironmentReferences.Any(reference =>
                string.IsNullOrWhiteSpace(reference.Key) || string.IsNullOrWhiteSpace(reference.Value)))
            {
                throw new DeckStateException("MCP environment references require non-empty names.");
            }
        }
    }

    private static void ValidateAssignment(McpAssignmentDocument assignment)
    {
        if (assignment.SchemaVersion != McpAssignmentDocument.CurrentSchemaVersion)
        {
            throw new DeckStateException(
                $"Unsupported MCP assignment schema {assignment.SchemaVersion}.");
        }

        foreach (var server in assignment.IncludeServers.Concat(assignment.ExcludeServers))
        {
            _ = CanonicalName.Parse(server);
        }

        foreach (var selector in assignment.IncludeTools.Concat(assignment.ExcludeTools))
        {
            var separator = selector.IndexOf('/');
            if (separator < 1 || separator == selector.Length - 1 ||
                selector.IndexOf('/', separator + 1) >= 0)
            {
                throw new DeckStateException(
                    $"MCP tool selector '{selector}' must have the form server/tool.");
            }

            _ = CanonicalName.Parse(selector[..separator]);
        }
    }

    private static async Task<T> ReadOrDefaultAsync<T>(
        string path,
        T defaultValue,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return defaultValue;
        }

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false) ?? throw new DeckStateException($"State file '{path}' is empty.");
    }

    private static async Task WriteAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(
                temporary, CanonicalJson.Serialize(value), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    [GeneratedRegex("[A-Za-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    private sealed record ClientLease(string DefinitionHash, StdioMcpClient Client);

    private sealed record CatalogLease(string ConfigurationHash, McpEffectiveCatalog Catalog);
}
