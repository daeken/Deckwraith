using Deckwraith.Application.Abstractions;
using Deckwraith.Core.Context;
using Deckwraith.Core.Naming;
using Deckwraith.Core.Serialization;
using Deckwraith.Core.State;
using Deckwraith.Persistence.Json;

namespace Deckwraith.Persistence.State;

public sealed class JsonCompactionStore : ICompactionStore
{
    private readonly string _rootPath;

    public JsonCompactionStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
    }

    public async Task<IReadOnlyList<CompactionDocument>> ReadAllAsync(
        CanonicalName wraith,
        CancellationToken cancellationToken)
    {
        var directory = DirectoryPath(wraith);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var result = new List<CompactionDocument>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            var document = await AtomicJsonFile.ReadAsync<CompactionDocument>(
                path, cancellationToken).ConfigureAwait(false);
            Validate(wraith, document);
            result.Add(document);
        }

        return result.OrderBy(document => document.FirstSequence).ToArray();
    }

    public async Task WriteAsync(
        CanonicalName wraith,
        CompactionDocument compaction,
        CancellationToken cancellationToken)
    {
        Validate(wraith, compaction);
        var directory = DirectoryPath(wraith);
        Directory.CreateDirectory(directory);
        SensitiveFilePermissions.RestrictDirectory(directory);
        var path = Path.Combine(directory, compaction.CompactionId + ".json");
        if (File.Exists(path))
        {
            var existing = await AtomicJsonFile.ReadAsync<CompactionDocument>(
                path, cancellationToken).ConfigureAwait(false);
            if (!StringComparer.Ordinal.Equals(
                CanonicalJson.Hash(existing with { CheckpointCommit = null }),
                CanonicalJson.Hash(compaction with { CheckpointCommit = null })))
            {
                throw new DeckStateException(
                    $"Compaction '{compaction.CompactionId}' already exists with different content.");
            }
        }

        await AtomicJsonFile.WriteAsync(path, compaction, cancellationToken).ConfigureAwait(false);
    }

    private string DirectoryPath(CanonicalName wraith) =>
        Path.Combine(_rootPath, "agents", wraith.Value, "compactions");

    private static void Validate(CanonicalName wraith, CompactionDocument document)
    {
        if (document.SchemaVersion != CompactionDocument.CurrentSchemaVersion ||
            !StringComparer.Ordinal.Equals(document.Agent, wraith.Value) ||
            !Guid.TryParseExact(document.CompactionId, "N", out _) ||
            document.FirstSequence < 1 ||
            document.LastSequence < document.FirstSequence ||
            document.SourceContentHashes.Count !=
                document.LastSequence - document.FirstSequence + 1 ||
            document.SourceContentHashes.Any(hash =>
                !hash.StartsWith("sha256:", StringComparison.Ordinal)))
        {
            throw new DeckStateException(
                $"Compaction '{document.CompactionId}' failed structural validation.");
        }

        if (document.Parameters.ValueKind is System.Text.Json.JsonValueKind.Undefined ||
            string.IsNullOrWhiteSpace(document.Provider) ||
            string.IsNullOrWhiteSpace(document.Model) ||
            string.IsNullOrWhiteSpace(document.PromptVersion) ||
            string.IsNullOrWhiteSpace(document.Summary))
        {
            throw new DeckStateException(
                $"Compaction '{document.CompactionId}' has incomplete provenance.");
        }

        _ = CanonicalJson.Hash(document);
    }
}
