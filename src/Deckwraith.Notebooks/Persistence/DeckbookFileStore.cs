using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deckwraith.Core.Naming;
using Deckwraith.Core.State;
using Deckwraith.Notebooks.Model;

namespace Deckwraith.Notebooks.Persistence;

internal sealed class DeckbookFileStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string _rootPath;

    public DeckbookFileStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
    }

    public async Task<DeckbookDocument> EnsureAsync(
        CanonicalName wraith,
        CanonicalName haunt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        EnsureOwners(wraith, haunt);
        var path = ManifestPath(wraith, haunt);
        if (File.Exists(path))
        {
            return await ReadJsonAsync<DeckbookDocument>(path, cancellationToken)
                .ConfigureAwait(false);
        }

        var root = DeckbookPath(wraith, haunt);
        CreateRestrictedDirectory(root);
        CreateRestrictedDirectory(Path.Combine(root, "cells"));
        CreateRestrictedDirectory(Path.Combine(root, "outputs"));
        var deckbook = new DeckbookDocument(
            DeckbookDocument.CurrentSchemaVersion,
            wraith.Value,
            haunt.Value,
            0,
            new Dictionary<string, string>(StringComparer.Ordinal),
            [],
            now);
        await WriteJsonAsync(path, deckbook, cancellationToken).ConfigureAwait(false);
        return deckbook;
    }

    public Task<DeckbookDocument> ReadDeckbookAsync(
        CanonicalName wraith,
        CanonicalName haunt,
        CancellationToken cancellationToken) =>
        ReadJsonAsync<DeckbookDocument>(ManifestPath(wraith, haunt), cancellationToken);

    public Task WriteDeckbookAsync(
        CanonicalName wraith,
        CanonicalName haunt,
        DeckbookDocument deckbook,
        CancellationToken cancellationToken) =>
        WriteJsonAsync(ManifestPath(wraith, haunt), deckbook, cancellationToken);

    public Task<DeckbookCellDocument> ReadCellAsync(
        CanonicalName wraith,
        CanonicalName haunt,
        string name,
        CancellationToken cancellationToken) =>
        ReadJsonAsync<DeckbookCellDocument>(
            CellMetadataPath(wraith, haunt, name), cancellationToken);

    public async Task<IReadOnlyList<DeckbookCellDocument>> ReadCellsAsync(
        CanonicalName wraith,
        CanonicalName haunt,
        DeckbookDocument deckbook,
        CancellationToken cancellationToken)
    {
        var cells = new List<DeckbookCellDocument>(deckbook.Cells.Count);
        foreach (var name in deckbook.Cells)
        {
            cells.Add(await ReadCellAsync(wraith, haunt, name, cancellationToken)
                .ConfigureAwait(false));
        }

        return cells.OrderBy(cell => cell.Position).ThenBy(cell => cell.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public Task WriteCellAsync(
        CanonicalName wraith,
        CanonicalName haunt,
        DeckbookCellDocument cell,
        CancellationToken cancellationToken)
    {
        var path = CellPath(wraith, haunt, cell.Name);
        CreateRestrictedDirectory(path);
        return WriteJsonAsync(Path.Combine(path, "cell.json"), cell, cancellationToken);
    }

    public Task<string> ReadSourceAsync(
        CanonicalName wraith,
        CanonicalName haunt,
        DeckbookCellDocument cell,
        CancellationToken cancellationToken) =>
        File.ReadAllTextAsync(
            SourcePath(wraith, haunt, cell), Encoding.UTF8, cancellationToken);

    public Task WriteSourceAsync(
        CanonicalName wraith,
        CanonicalName haunt,
        DeckbookCellDocument cell,
        string source,
        CancellationToken cancellationToken) =>
        WriteTextAsync(SourcePath(wraith, haunt, cell), source, cancellationToken);

    public void RenameCell(
        CanonicalName wraith,
        CanonicalName haunt,
        string source,
        string target)
    {
        var sourcePath = CellPath(wraith, haunt, source);
        var targetPath = CellPath(wraith, haunt, target);
        if (Directory.Exists(targetPath))
        {
            throw new DeckStateException($"Cell '{target}' already exists.");
        }

        Directory.Move(sourcePath, targetPath);
    }

    public void DeleteCell(CanonicalName wraith, CanonicalName haunt, string name) =>
        Directory.Delete(CellPath(wraith, haunt, name), recursive: true);

    public Task WriteOutputAsync(
        CanonicalName wraith,
        CanonicalName haunt,
        DeckbookOutputDocument output,
        CancellationToken cancellationToken) =>
        WriteJsonAsync(OutputPath(wraith, haunt, output.Hash), output, cancellationToken);

    public async Task<DeckbookOutputDocument?> ReadOutputAsync(
        CanonicalName wraith,
        CanonicalName haunt,
        string? hash,
        CancellationToken cancellationToken)
    {
        if (hash is null)
        {
            return null;
        }

        var path = OutputPath(wraith, haunt, hash);
        return File.Exists(path)
            ? await ReadJsonAsync<DeckbookOutputDocument>(path, cancellationToken)
                .ConfigureAwait(false)
            : throw new DeckStateException($"Deckbook output '{hash}' is missing.");
    }

    public static string SourceFileFor(DeckbookCellKind kind, string? kernel) => kind switch
    {
        DeckbookCellKind.Code when StringComparer.OrdinalIgnoreCase.Equals(kernel, "powershell") =>
            "source.ps1",
        DeckbookCellKind.Code when StringComparer.OrdinalIgnoreCase.Equals(kernel, "csharp") =>
            "source.csx",
        DeckbookCellKind.Markdown or DeckbookCellKind.Prompt => "source.md",
        _ => "source.json",
    };

    private string ManifestPath(CanonicalName wraith, CanonicalName haunt) =>
        Path.Combine(DeckbookPath(wraith, haunt), "deckbook.json");

    private string DeckbookPath(CanonicalName wraith, CanonicalName haunt) =>
        Path.Combine(_rootPath, "agents", wraith.Value, "deckbooks", haunt.Value);

    private string CellPath(CanonicalName wraith, CanonicalName haunt, string name) =>
        Path.Combine(DeckbookPath(wraith, haunt), "cells", CanonicalName.Parse(name).Value);

    private string CellMetadataPath(CanonicalName wraith, CanonicalName haunt, string name) =>
        Path.Combine(CellPath(wraith, haunt, name), "cell.json");

    private string SourcePath(
        CanonicalName wraith,
        CanonicalName haunt,
        DeckbookCellDocument cell)
    {
        if (!StringComparer.Ordinal.Equals(cell.SourceFile, Path.GetFileName(cell.SourceFile)))
        {
            throw new DeckStateException($"Cell '{cell.Name}' has an invalid source path.");
        }

        return Path.Combine(CellPath(wraith, haunt, cell.Name), cell.SourceFile);
    }

    private string OutputPath(CanonicalName wraith, CanonicalName haunt, string hash)
    {
        if (!hash.StartsWith("sha256:", StringComparison.Ordinal) || hash.Length != 71)
        {
            throw new DeckStateException($"Invalid deckbook output hash '{hash}'.");
        }

        return Path.Combine(DeckbookPath(wraith, haunt), "outputs", hash[7..] + ".json");
    }

    private void EnsureOwners(CanonicalName wraith, CanonicalName haunt)
    {
        if (!File.Exists(Path.Combine(_rootPath, "agents", wraith.Value, "agent.json")))
        {
            throw new DeckStateException($"Wraith '{wraith}' does not exist.");
        }

        if (!File.Exists(Path.Combine(_rootPath, "haunts", haunt.Value, "haunt.json")))
        {
            throw new DeckStateException($"Haunt '{haunt}' does not exist.");
        }
    }

    private static async Task<T> ReadJsonAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new JsonException($"'{path}' contained JSON null.");
    }

    private static async Task WriteJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("A parent directory is required.", nameof(path));
        CreateRestrictedDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                RestrictFile(temporary);
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
            RestrictFile(path);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task WriteTextAsync(
        string path,
        string text,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("A parent directory is required.", nameof(path));
        CreateRestrictedDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            RestrictFile(temporary);
            File.Move(temporary, path, overwrite: true);
            RestrictFile(path);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void CreateRestrictedDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
