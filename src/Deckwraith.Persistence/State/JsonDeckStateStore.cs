using Deckwraith.Application.Abstractions;
using Deckwraith.Core.Naming;
using Deckwraith.Core.State;
using Deckwraith.Persistence.Json;

namespace Deckwraith.Persistence.State;

public sealed class JsonDeckStateStore : IDeckStateStore
{
    private readonly string _deckManifestPath;

    public JsonDeckStateStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = Path.GetFullPath(rootPath);
        _deckManifestPath = Path.Combine(RootPath, "deck.json");
    }

    public string RootPath { get; }

    public async Task InitializeAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(RootPath);
        SensitiveFilePermissions.RestrictDirectory(RootPath);
        if (File.Exists(_deckManifestPath))
        {
            throw new DeckStateException($"A deck is already initialized at '{RootPath}'.");
        }

        foreach (var directory in new[]
        {
            "agents",
            "haunts",
            "tools/powershell",
            "mcp",
            "recovery/renames",
            "recovery/incidents",
        })
        {
            var path = Path.Combine(RootPath, directory);
            Directory.CreateDirectory(path);
            SensitiveFilePermissions.RestrictDirectory(path);
        }

        await AtomicJsonFile.WriteAsync(
            _deckManifestPath, DeckManifest.Create(now), cancellationToken).ConfigureAwait(false);
        await AtomicJsonFile.WriteAsync(
            Path.Combine(RootPath, "policy.json"),
            DeckPolicy.CreateDefault(),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RenameIntent>> RecoverPendingRenamesAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        var transactionPath = Path.Combine(RootPath, "recovery", "renames");
        var recovered = new List<RenameIntent>();
        foreach (var path in Directory.EnumerateFiles(transactionPath, "*.json").Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var intent = await AtomicJsonFile.ReadAsync<RenameIntent>(path, cancellationToken)
                .ConfigureAwait(false);
            if (intent.Status is RenameStatus.Completed)
            {
                continue;
            }

            if (intent.Status is RenameStatus.Prepared)
            {
                intent = await ApplyRenameAsync(intent, path, now, cancellationToken)
                    .ConfigureAwait(false);
            }

            recovered.Add(intent);
        }

        return recovered;
    }

    public async Task<IdentityDocument> CreateWraithAsync(
        CanonicalName name,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        var manifest = await ReadManifestAsync(cancellationToken).ConfigureAwait(false);
        EnsureNameAvailable(name, "agents", manifest.WraithAliases);

        var wraithPath = EntityPath("agents", name);
        Directory.CreateDirectory(wraithPath);
        SensitiveFilePermissions.RestrictDirectory(wraithPath);
        foreach (var relativePath in new[]
        {
            "archive",
            "compactions",
            "deckbooks",
            "projections",
            "runs",
            "state/values",
            "tools",
        })
        {
            var path = Path.Combine(wraithPath, relativePath);
            Directory.CreateDirectory(path);
            SensitiveFilePermissions.RestrictDirectory(path);
        }

        var identity = IdentityDocument.CreateSparse(name, now);
        await AtomicJsonFile.WriteAsync(
            Path.Combine(wraithPath, "agent.json"),
            WraithDocument.Create(name, now),
            cancellationToken).ConfigureAwait(false);
        await AtomicJsonFile.WriteAsync(
            Path.Combine(wraithPath, "identity.json"),
            identity,
            cancellationToken).ConfigureAwait(false);
        return identity;
    }

    public async Task<HauntDocument> CreateHauntAsync(
        CanonicalName name,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        var manifest = await ReadManifestAsync(cancellationToken).ConfigureAwait(false);
        EnsureNameAvailable(name, "haunts", manifest.HauntAliases);

        var hauntPath = EntityPath("haunts", name);
        Directory.CreateDirectory(hauntPath);
        SensitiveFilePermissions.RestrictDirectory(hauntPath);
        foreach (var relativePath in new[] { "artifacts", "context", "state/values", "tasks" })
        {
            var path = Path.Combine(hauntPath, relativePath);
            Directory.CreateDirectory(path);
            SensitiveFilePermissions.RestrictDirectory(path);
        }

        var haunt = HauntDocument.Create(name, now);
        await AtomicJsonFile.WriteAsync(
            Path.Combine(hauntPath, "haunt.json"), haunt, cancellationToken).ConfigureAwait(false);
        return haunt;
    }

    public async Task<CanonicalName> ResolveWraithAsync(
        CanonicalName name,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        var manifest = await ReadManifestAsync(cancellationToken).ConfigureAwait(false);
        return Resolve(name, "agents", manifest.WraithAliases, "wraith");
    }

    public async Task<CanonicalName> ResolveHauntAsync(
        CanonicalName name,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        var manifest = await ReadManifestAsync(cancellationToken).ConfigureAwait(false);
        return Resolve(name, "haunts", manifest.HauntAliases, "haunt");
    }

    public async Task<IdentityDocument> ReadIdentityAsync(
        CanonicalName name,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveWraithAsync(name, cancellationToken).ConfigureAwait(false);
        return await AtomicJsonFile.ReadAsync<IdentityDocument>(
            Path.Combine(EntityPath("agents", resolved), "identity.json"), cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<RenameIntent> RenameWraithAsync(
        CanonicalName source,
        CanonicalName target,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        PrepareAndApplyRenameAsync(
            RenameSubject.Wraith, source, target, now, cancellationToken);

    public Task<RenameIntent> RenameHauntAsync(
        CanonicalName source,
        CanonicalName target,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        PrepareAndApplyRenameAsync(
            RenameSubject.Haunt, source, target, now, cancellationToken);

    public async Task CompleteRenameAsync(
        string operationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        if (!Guid.TryParseExact(operationId, "N", out _))
        {
            throw new ArgumentException("A rename operation ID must be a compact GUID.", nameof(operationId));
        }

        var path = Path.Combine(RootPath, "recovery", "renames", $"{operationId}.json");
        if (!File.Exists(path))
        {
            throw new DeckStateException($"Rename operation '{operationId}' does not exist.");
        }

        var intent = await AtomicJsonFile.ReadAsync<RenameIntent>(path, cancellationToken)
            .ConfigureAwait(false);
        if (intent.Status is RenameStatus.Prepared)
        {
            throw new DeckStateException($"Rename operation '{operationId}' has not been applied.");
        }

        if (intent.Status is not RenameStatus.Completed)
        {
            await AtomicJsonFile.WriteAsync(
                path,
                intent with { Status = RenameStatus.Completed, CompletedAt = now },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<RenameIntent> PrepareAndApplyRenameAsync(
        RenameSubject subject,
        CanonicalName source,
        CanonicalName target,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        if (source == target)
        {
            throw new DeckStateException($"'{source}' is already the requested canonical name.");
        }

        var manifest = await ReadManifestAsync(cancellationToken).ConfigureAwait(false);
        var collection = subject is RenameSubject.Wraith ? "agents" : "haunts";
        var aliases = subject is RenameSubject.Wraith
            ? manifest.WraithAliases
            : manifest.HauntAliases;
        EnsureNameAvailable(target, collection, aliases);
        if (!Directory.Exists(EntityPath(collection, source)))
        {
            throw new DeckStateException($"The {subject.ToString().ToLowerInvariant()} '{source}' does not exist.");
        }

        var operationId = Guid.CreateVersion7().ToString("N");
        var intent = new RenameIntent(
            RenameIntent.CurrentSchemaVersion,
            operationId,
            subject,
            source.Value,
            target.Value,
            RenameStatus.Prepared,
            now,
            null);
        var intentPath = Path.Combine(RootPath, "recovery", "renames", $"{operationId}.json");
        await AtomicJsonFile.WriteAsync(intentPath, intent, cancellationToken).ConfigureAwait(false);
        return await ApplyRenameAsync(intent, intentPath, now, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RenameIntent> ApplyRenameAsync(
        RenameIntent intent,
        string intentPath,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var source = CanonicalName.Parse(intent.Source);
        var target = CanonicalName.Parse(intent.Target);
        var collection = intent.Subject is RenameSubject.Wraith ? "agents" : "haunts";
        var sourcePath = EntityPath(collection, source);
        var targetPath = EntityPath(collection, target);
        var sourceExists = Directory.Exists(sourcePath);
        var targetExists = Directory.Exists(targetPath);

        if (sourceExists && targetExists)
        {
            throw new DeckStateException(
                $"Cannot recover rename '{intent.OperationId}': both source and target paths exist.");
        }

        if (!sourceExists && !targetExists)
        {
            throw new DeckStateException(
                $"Cannot recover rename '{intent.OperationId}': neither source nor target path exists.");
        }

        if (sourceExists)
        {
            Directory.Move(sourcePath, targetPath);
        }

        if (intent.Subject is RenameSubject.Wraith)
        {
            await UpdateWraithDocumentsAsync(targetPath, source, target, now, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await UpdateHauntDocumentAsync(targetPath, source, target, cancellationToken)
                .ConfigureAwait(false);
        }

        var manifest = await ReadManifestAsync(cancellationToken).ConfigureAwait(false);
        var wraithAliases = new Dictionary<string, string>(manifest.WraithAliases, StringComparer.Ordinal);
        var hauntAliases = new Dictionary<string, string>(manifest.HauntAliases, StringComparer.Ordinal);
        RetargetAliases(
            intent.Subject is RenameSubject.Wraith ? wraithAliases : hauntAliases,
            source,
            target);
        await AtomicJsonFile.WriteAsync(
            _deckManifestPath,
            manifest with
            {
                WraithAliases = wraithAliases,
                HauntAliases = hauntAliases,
            },
            cancellationToken).ConfigureAwait(false);

        var applied = intent with { Status = RenameStatus.Applied };
        await AtomicJsonFile.WriteAsync(
            intentPath,
            applied,
            cancellationToken).ConfigureAwait(false);
        return applied;
    }

    private static async Task UpdateWraithDocumentsAsync(
        string targetPath,
        CanonicalName source,
        CanonicalName target,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var documentPath = Path.Combine(targetPath, "agent.json");
        var document = await AtomicJsonFile.ReadAsync<WraithDocument>(documentPath, cancellationToken)
            .ConfigureAwait(false);
        var aliases = AddAlias(document.Aliases, source.Value);
        await AtomicJsonFile.WriteAsync(
            documentPath,
            document with { Name = target.Value, Aliases = aliases },
            cancellationToken).ConfigureAwait(false);

        var identityPath = Path.Combine(targetPath, "identity.json");
        var identity = await AtomicJsonFile.ReadAsync<IdentityDocument>(identityPath, cancellationToken)
            .ConfigureAwait(false);
        await AtomicJsonFile.WriteAsync(
            identityPath,
            identity with { Name = target.Value, UpdatedAt = now },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateHauntDocumentAsync(
        string targetPath,
        CanonicalName source,
        CanonicalName target,
        CancellationToken cancellationToken)
    {
        var documentPath = Path.Combine(targetPath, "haunt.json");
        var document = await AtomicJsonFile.ReadAsync<HauntDocument>(documentPath, cancellationToken)
            .ConfigureAwait(false);
        await AtomicJsonFile.WriteAsync(
            documentPath,
            document with
            {
                Name = target.Value,
                Aliases = AddAlias(document.Aliases, source.Value),
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<DeckManifest> ReadManifestAsync(CancellationToken cancellationToken) =>
        await AtomicJsonFile.ReadAsync<DeckManifest>(_deckManifestPath, cancellationToken)
            .ConfigureAwait(false);

    private CanonicalName Resolve(
        CanonicalName requested,
        string collection,
        IReadOnlyDictionary<string, string> aliases,
        string noun)
    {
        if (Directory.Exists(EntityPath(collection, requested)))
        {
            return requested;
        }

        if (aliases.TryGetValue(requested.Value, out var target))
        {
            var resolved = CanonicalName.Parse(target);
            if (Directory.Exists(EntityPath(collection, resolved)))
            {
                return resolved;
            }
        }

        throw new DeckStateException($"No {noun} resolves from '{requested}'.");
    }

    private void EnsureNameAvailable(
        CanonicalName name,
        string collection,
        IReadOnlyDictionary<string, string> aliases)
    {
        if (Directory.Exists(EntityPath(collection, name)) || aliases.ContainsKey(name.Value))
        {
            throw new DeckStateException(
                $"The name '{name}' is already a canonical name or reserved alias in {collection}.");
        }
    }

    private string EntityPath(string collection, CanonicalName name) =>
        Path.Combine(RootPath, collection, name.Value);

    private void EnsureInitialized()
    {
        if (!File.Exists(_deckManifestPath))
        {
            throw new DeckStateException($"No deck is initialized at '{RootPath}'.");
        }
    }

    private static IReadOnlyList<string> AddAlias(IReadOnlyList<string> aliases, string alias) =>
        aliases.Contains(alias, StringComparer.Ordinal)
            ? aliases
            : [.. aliases, alias];

    private static void RetargetAliases(
        Dictionary<string, string> aliases,
        CanonicalName source,
        CanonicalName target)
    {
        foreach (var alias in aliases
            .Where(pair => StringComparer.Ordinal.Equals(pair.Value, source.Value))
            .Select(pair => pair.Key)
            .ToArray())
        {
            aliases[alias] = target.Value;
        }

        aliases[source.Value] = target.Value;
    }
}
