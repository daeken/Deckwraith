using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Deckwraith.Application.Files;

public enum FileEditKind
{
    Write,
    Prepend,
    Append,
    Replace,
    JsonSet,
    JsonRemove,
    JsonInsert,
    JsonAppend,
    JsonTest,
}

public sealed record AtomicFileEdit(
    string Path,
    FileEditKind Kind,
    string? Text = null,
    string? Match = null,
    string? Replacement = null,
    int? ExpectedCount = null,
    string? JsonPointer = null,
    int? JsonIndex = null,
    JsonElement? Value = null,
    string? ExpectedHash = null);

public sealed record AtomicFileEditBatch(
    IReadOnlyList<AtomicFileEdit> Operations,
    string? RootPath = null,
    string? CommitSubject = null,
    string? CommitBody = null);

public sealed record FileEditReceipt(
    string Path,
    bool Created,
    string? BeforeHash,
    string AfterHash,
    long BeforeLength,
    long AfterLength,
    IReadOnlyList<FileEditKind> Operations);

public sealed record AtomicFileEditResult(
    IReadOnlyList<FileEditReceipt> Files,
    string? CommitSubject,
    string? CommitBody,
    ProjectCommitReceipt? Commit = null,
    string? Warning = null);

public sealed class AtomicFileEditException : Exception
{
    public AtomicFileEditException(string message)
        : base(message)
    {
    }

    public AtomicFileEditException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class AtomicFileEditor
{
    private static readonly SemaphoreSlim PublicationGate = new(1, 1);
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
    };

    public static async Task<AtomicFileEditResult> ApplyAsync(
        AtomicFileEditBatch batch,
        CancellationToken cancellationToken = default)
    {
        return await ApplyAsync(batch, commitAsync: null, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<AtomicFileEditResult> ApplyAsync(
        AtomicFileEditBatch batch,
        Func<IReadOnlyList<FileEditReceipt>, CancellationToken, Task<ProjectCommitReceipt?>>? commitAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Operations.Count == 0)
        {
            throw new AtomicFileEditException("An atomic file edit batch must contain an operation.");
        }

        var candidates = BuildCandidates(batch, cancellationToken);
        await PublicationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var published = false;
        try
        {
            await PrepareTemporaryFilesAsync(candidates, cancellationToken).ConfigureAwait(false);
            Publish(candidates);
            published = true;

            var result = new AtomicFileEditResult(
                candidates.Select(candidate => new FileEditReceipt(
                    candidate.Path,
                    !candidate.OriginalExists,
                    candidate.OriginalHash,
                    candidate.FinalHash,
                    candidate.OriginalBytes.LongLength,
                    candidate.FinalBytes.LongLength,
                    candidate.Operations.Select(operation => operation.Kind).ToArray())).ToArray(),
                batch.CommitSubject,
                batch.CommitBody);
            if (commitAsync is not null)
            {
                var commit = await commitAsync(result.Files, cancellationToken).ConfigureAwait(false);
                result = result with { Commit = commit };
            }

            published = false;
            return result with { Warning = CompletePublication(candidates) };
        }
        catch (Exception exception) when (published)
        {
            RollBackPublished(candidates, exception);
            throw;
        }
        finally
        {
            Cleanup(candidates);
            PublicationGate.Release();
        }
    }

    public static IReadOnlyList<string> ResolvePaths(AtomicFileEditBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Operations.Count == 0)
        {
            throw new AtomicFileEditException("An atomic file edit batch must contain an operation.");
        }

        var root = string.IsNullOrWhiteSpace(batch.RootPath)
            ? null
            : ResolveNativePath(Path.GetFullPath(batch.RootPath));
        return batch.Operations
            .Select(operation =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(operation.Path);
                return ResolvePath(operation.Path, root);
            })
            .Distinct(PathComparer)
            .Order(PathComparer)
            .ToArray();
    }

    private static List<FileCandidate> BuildCandidates(
        AtomicFileEditBatch batch,
        CancellationToken cancellationToken)
    {
        var root = string.IsNullOrWhiteSpace(batch.RootPath)
            ? null
            : ResolveNativePath(Path.GetFullPath(batch.RootPath));
        var grouped = new Dictionary<string, List<AtomicFileEdit>>(PathComparer);
        foreach (var operation in batch.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(operation.Path);
            var path = ResolvePath(operation.Path, root);
            if (!grouped.TryGetValue(path, out var operations))
            {
                operations = [];
                grouped.Add(path, operations);
            }

            operations.Add(operation);
        }

        var candidates = new List<FileCandidate>(grouped.Count);
        foreach (var (path, operations) in grouped.OrderBy(pair => pair.Key, PathComparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exists = File.Exists(path);
            var original = exists ? File.ReadAllBytes(path) : [];
            var originalHash = exists ? Hash(original) : null;
            ValidateExpectedHashes(path, exists, originalHash, operations);
            var document = Decode(path, original, exists);
            foreach (var operation in operations)
            {
                document = Apply(path, document, exists, operation);
                exists = true;
            }

            var final = Encode(document);
            candidates.Add(new FileCandidate(
                path,
                File.Exists(path),
                original,
                originalHash,
                final,
                Hash(final),
                operations,
                root));
        }

        return candidates;
    }

    private static TextDocument Apply(
        string path,
        TextDocument document,
        bool exists,
        AtomicFileEdit operation)
    {
        switch (operation.Kind)
        {
            case FileEditKind.Write:
                Require(operation.Text, operation.Kind, nameof(operation.Text));
                return document with { Text = operation.Text! };
            case FileEditKind.Prepend:
                RequireExisting(path, exists, operation.Kind);
                Require(operation.Text, operation.Kind, nameof(operation.Text));
                return document with { Text = operation.Text + document.Text };
            case FileEditKind.Append:
                RequireExisting(path, exists, operation.Kind);
                Require(operation.Text, operation.Kind, nameof(operation.Text));
                return document with { Text = document.Text + operation.Text };
            case FileEditKind.Replace:
                RequireExisting(path, exists, operation.Kind);
                Require(operation.Match, operation.Kind, nameof(operation.Match));
                if (operation.Replacement is null)
                {
                    throw new AtomicFileEditException(
                        $"{operation.Kind} requires {nameof(operation.Replacement)} for '{path}'.");
                }

                var expected = operation.ExpectedCount ?? 1;
                if (expected < 1)
                {
                    throw new AtomicFileEditException(
                        $"{operation.Kind} ExpectedCount must be positive for '{path}'.");
                }

                var actual = CountOccurrences(document.Text, operation.Match!);
                if (actual != expected)
                {
                    throw new AtomicFileEditException(
                        $"{operation.Kind} expected {expected} occurrence(s) of its anchor in '{path}', but found {actual}.");
                }

                return document with
                {
                    Text = document.Text.Replace(
                        operation.Match!, operation.Replacement, StringComparison.Ordinal),
                };
            case FileEditKind.JsonSet:
            case FileEditKind.JsonRemove:
            case FileEditKind.JsonInsert:
            case FileEditKind.JsonAppend:
            case FileEditKind.JsonTest:
                if (!exists && !(operation.Kind is FileEditKind.JsonSet && operation.JsonPointer == string.Empty))
                {
                    RequireExisting(path, exists, operation.Kind);
                }

                return ApplyJson(path, document, operation);
            default:
                throw new AtomicFileEditException($"Unsupported file edit kind '{operation.Kind}'.");
        }
    }

    private static TextDocument ApplyJson(
        string path,
        TextDocument document,
        AtomicFileEdit operation)
    {
        var pointer = operation.JsonPointer ?? string.Empty;
        JsonNode? root;
        try
        {
            root = document.Text.Length == 0
                ? null
                : JsonNode.Parse(document.Text, nodeOptions: null, documentOptions: default);
        }
        catch (JsonException exception)
        {
            throw new AtomicFileEditException($"'{path}' is not valid JSON: {exception.Message}", exception);
        }

        root = operation.Kind switch
        {
            FileEditKind.JsonSet => JsonSet(path, root, pointer, RequireValue(path, operation)),
            FileEditKind.JsonRemove => JsonRemove(path, root, pointer),
            FileEditKind.JsonInsert => JsonInsert(
                path,
                root,
                pointer,
                operation.JsonIndex ?? throw new AtomicFileEditException(
                    $"JsonInsert requires JsonIndex for '{path}'."),
                RequireValue(path, operation)),
            FileEditKind.JsonAppend => JsonAppend(path, root, pointer, RequireValue(path, operation)),
            FileEditKind.JsonTest => JsonTest(path, root, pointer, RequireValue(path, operation)),
            _ => throw new AtomicFileEditException($"Unsupported JSON edit kind '{operation.Kind}'."),
        };

        var trailingNewline = document.Text.EndsWith('\n');
        var newline = document.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var serialized = JsonSerializer.Serialize(root, IndentedJson);
        if (newline == "\r\n")
        {
            serialized = serialized.Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        if (trailingNewline || document.Text.Length == 0)
        {
            serialized += newline;
        }

        return document with { Text = serialized };
    }

    private static JsonNode? JsonSet(string path, JsonNode? root, string pointer, JsonNode? value)
    {
        if (pointer.Length == 0)
        {
            return value?.DeepClone();
        }

        var (parent, token) = ResolveParent(path, root, pointer);
        switch (parent)
        {
            case JsonObject obj:
                obj[token] = value?.DeepClone();
                return root;
            case JsonArray array:
                var index = ParseArrayIndex(path, token, array.Count, allowEnd: false);
                array[index] = value?.DeepClone();
                return root;
            default:
                throw InvalidJsonParent(path, pointer);
        }
    }

    private static JsonNode? JsonRemove(string path, JsonNode? root, string pointer)
    {
        if (pointer.Length == 0)
        {
            throw new AtomicFileEditException($"JsonRemove cannot remove the document root in '{path}'.");
        }

        var (parent, token) = ResolveParent(path, root, pointer);
        switch (parent)
        {
            case JsonObject obj when obj.Remove(token):
                return root;
            case JsonObject:
                throw new AtomicFileEditException(
                    $"JSON pointer '{pointer}' does not exist in '{path}'.");
            case JsonArray array:
                array.RemoveAt(ParseArrayIndex(path, token, array.Count, allowEnd: false));
                return root;
            default:
                throw InvalidJsonParent(path, pointer);
        }
    }

    private static JsonNode? JsonInsert(
        string path,
        JsonNode? root,
        string pointer,
        int index,
        JsonNode? value)
    {
        var target = ResolveNode(path, root, pointer);
        if (target is not JsonArray array)
        {
            throw new AtomicFileEditException(
                $"JSON pointer '{pointer}' is not an array in '{path}'.");
        }

        if (index < 0 || index > array.Count)
        {
            throw new AtomicFileEditException(
                $"JsonInsert index {index} is outside array '{pointer}' in '{path}'.");
        }

        array.Insert(index, value?.DeepClone());
        return root;
    }

    private static JsonNode? JsonAppend(string path, JsonNode? root, string pointer, JsonNode? value)
    {
        var target = ResolveNode(path, root, pointer);
        if (target is not JsonArray array)
        {
            throw new AtomicFileEditException(
                $"JSON pointer '{pointer}' is not an array in '{path}'.");
        }

        array.Add(value?.DeepClone());
        return root;
    }

    private static JsonNode? JsonTest(string path, JsonNode? root, string pointer, JsonNode? value)
    {
        var target = ResolveNode(path, root, pointer, requireExistingNull: true);
        if (!JsonNode.DeepEquals(target, value))
        {
            throw new AtomicFileEditException(
                $"JsonTest failed at '{pointer}' in '{path}'.");
        }

        return root;
    }

    private static (JsonNode Parent, string Token) ResolveParent(
        string path,
        JsonNode? root,
        string pointer)
    {
        var tokens = ParsePointer(path, pointer);
        if (tokens.Count == 0)
        {
            throw new AtomicFileEditException($"JSON pointer '{pointer}' has no parent in '{path}'.");
        }

        var parentPointer = tokens.Count == 1
            ? string.Empty
            : "/" + string.Join('/', tokens.Take(tokens.Count - 1).Select(EscapePointerToken));
        var parent = ResolveNode(path, root, parentPointer);
        return parent is null
            ? throw InvalidJsonParent(path, pointer)
            : (parent, tokens[^1]);
    }

    private static JsonNode? ResolveNode(
        string path,
        JsonNode? root,
        string pointer,
        bool requireExistingNull = false)
    {
        var current = root;
        if (pointer.Length == 0)
        {
            if (root is null && !requireExistingNull)
            {
                throw new AtomicFileEditException($"JSON document root is null in '{path}'.");
            }

            return root;
        }

        foreach (var token in ParsePointer(path, pointer))
        {
            switch (current)
            {
                case JsonObject obj when obj.TryGetPropertyValue(token, out var next):
                    current = next;
                    break;
                case JsonObject:
                    throw new AtomicFileEditException(
                        $"JSON pointer '{pointer}' does not exist in '{path}'.");
                case JsonArray array:
                    current = array[ParseArrayIndex(path, token, array.Count, allowEnd: false)];
                    break;
                default:
                    throw new AtomicFileEditException(
                        $"JSON pointer '{pointer}' crosses a scalar or null value in '{path}'.");
            }
        }

        if (current is null && !requireExistingNull)
        {
            throw new AtomicFileEditException($"JSON pointer '{pointer}' is null in '{path}'.");
        }

        return current;
    }

    private static List<string> ParsePointer(string path, string pointer)
    {
        if (pointer.Length == 0)
        {
            return [];
        }

        if (pointer[0] != '/')
        {
            throw new AtomicFileEditException(
                $"JSON pointer '{pointer}' in '{path}' must be empty or begin with '/'.");
        }

        return pointer[1..].Split('/').Select(token =>
        {
            var builder = new StringBuilder(token.Length);
            for (var index = 0; index < token.Length; index++)
            {
                if (token[index] != '~')
                {
                    builder.Append(token[index]);
                    continue;
                }

                if (++index >= token.Length || token[index] is not ('0' or '1'))
                {
                    throw new AtomicFileEditException(
                        $"JSON pointer '{pointer}' in '{path}' contains an invalid escape.");
                }

                builder.Append(token[index] == '0' ? '~' : '/');
            }

            return builder.ToString();
        }).ToList();
    }

    private static string EscapePointerToken(string token) =>
        token.Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);

    private static int ParseArrayIndex(
        string path,
        string token,
        int count,
        bool allowEnd)
    {
        if (!int.TryParse(token, out var index) || index < 0 || index >= count + (allowEnd ? 1 : 0))
        {
            throw new AtomicFileEditException(
                $"JSON array index '{token}' is outside its array in '{path}'.");
        }

        return index;
    }

    private static JsonNode? RequireValue(string path, AtomicFileEdit operation)
    {
        if (operation.Value is not { } value)
        {
            throw new AtomicFileEditException(
                $"{operation.Kind} requires Value for '{path}'. Use an explicit JSON null when null is intended.");
        }

        return JsonNode.Parse(value.GetRawText());
    }

    private static AtomicFileEditException InvalidJsonParent(string path, string pointer) =>
        new($"JSON pointer '{pointer}' does not have an object or array parent in '{path}'.");

    private static void ValidateExpectedHashes(
        string path,
        bool exists,
        string? actualHash,
        IReadOnlyList<AtomicFileEdit> operations)
    {
        foreach (var expected in operations
            .Select(operation => operation.ExpectedHash)
            .Where(expected => !string.IsNullOrWhiteSpace(expected))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(expected, "missing"))
            {
                if (exists)
                {
                    throw new AtomicFileEditException(
                        $"Expected '{path}' to be missing, but it exists with hash {actualHash}.");
                }

                continue;
            }

            if (!exists || !StringComparer.OrdinalIgnoreCase.Equals(expected, actualHash))
            {
                throw new AtomicFileEditException(
                    $"Expected hash {expected} for '{path}', but found {actualHash ?? "missing"}.");
            }
        }
    }

    private static async Task PrepareTemporaryFilesAsync(
        IReadOnlyList<FileCandidate> candidates,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(candidate.Path)
                ?? throw new AtomicFileEditException($"'{candidate.Path}' has no parent directory.");
            if (!Directory.Exists(directory))
            {
                throw new AtomicFileEditException(
                    $"Parent directory '{directory}' does not exist for '{candidate.Path}'.");
            }

            candidate.TemporaryPath = Path.Combine(
                directory, $".deckwraith-edit-{Guid.NewGuid():N}.tmp");
            candidate.BackupPath = Path.Combine(
                directory, $".deckwraith-edit-{Guid.NewGuid():N}.bak");
            await using (var stream = new FileStream(
                candidate.TemporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(candidate.FinalBytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (candidate.OriginalExists && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(candidate.TemporaryPath, File.GetUnixFileMode(candidate.Path));
            }
        }
    }

    private static void Publish(IReadOnlyList<FileCandidate> candidates)
    {
        var published = new List<FileCandidate>();
        try
        {
            foreach (var candidate in candidates)
            {
                VerifyUnchanged(candidate);
                if (candidate.OriginalExists)
                {
                    File.Move(candidate.Path, candidate.BackupPath!);
                }

                try
                {
                    File.Move(candidate.TemporaryPath!, candidate.Path);
                    candidate.TemporaryPath = null;
                    published.Add(candidate);
                }
                catch
                {
                    try
                    {
                        if (candidate.OriginalExists && File.Exists(candidate.BackupPath))
                        {
                            File.Move(candidate.BackupPath!, candidate.Path);
                            candidate.BackupPath = null;
                        }
                    }
                    catch (Exception restoreException)
                    {
                        throw new PublicationRollbackException(
                            $"Publication failed for '{candidate.Path}' and its original could not be restored. " +
                            $"The recovery backup remains at '{candidate.BackupPath}'.",
                            restoreException);
                    }

                    throw;
                }
            }

        }
        catch (Exception exception)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var candidate in published.AsEnumerable().Reverse())
            {
                try
                {
                    File.Delete(candidate.Path);
                    if (candidate.OriginalExists && File.Exists(candidate.BackupPath))
                    {
                        File.Move(candidate.BackupPath!, candidate.Path);
                        candidate.BackupPath = null;
                    }
                }
                catch (Exception rollbackException)
                {
                    rollbackErrors.Add(rollbackException);
                }
            }

            if (rollbackErrors.Count > 0 || exception is PublicationRollbackException)
            {
                throw new AggregateException(
                    "Atomic file edit publication failed and rollback was incomplete; recovery backups were retained.",
                    [exception, .. rollbackErrors]);
            }

            throw new AtomicFileEditException(
                "Atomic file edit publication failed; all published files were restored.", exception);
        }
    }

    private static string? CompletePublication(IEnumerable<FileCandidate> candidates)
    {
        var retainedBackups = new List<string>();
        foreach (var candidate in candidates.Where(candidate => candidate.OriginalExists))
        {
            try
            {
                File.Delete(candidate.BackupPath!);
                candidate.BackupPath = null;
            }
            catch (IOException)
            {
                retainedBackups.Add(candidate.BackupPath!);
            }
            catch (UnauthorizedAccessException)
            {
                retainedBackups.Add(candidate.BackupPath!);
            }
        }

        return retainedBackups.Count == 0
            ? null
            : "The edit succeeded, but Deckwraith could not remove recovery backup(s): " +
                string.Join(", ", retainedBackups);
    }

    private static void RollBackPublished(
        IReadOnlyList<FileCandidate> candidates,
        Exception cause)
    {
        var rollbackErrors = new List<Exception>();
        foreach (var candidate in candidates.Reverse())
        {
            try
            {
                if (!File.Exists(candidate.Path) ||
                    !StringComparer.Ordinal.Equals(
                        Hash(File.ReadAllBytes(candidate.Path)), candidate.FinalHash))
                {
                    throw new AtomicFileEditException(
                        $"'{candidate.Path}' changed after publication; its recovery backup was retained.");
                }

                if (candidate.OriginalExists && !File.Exists(candidate.BackupPath))
                {
                    throw new AtomicFileEditException(
                        $"The recovery backup for '{candidate.Path}' is missing.");
                }

                File.Delete(candidate.Path);
                if (candidate.OriginalExists)
                {
                    File.Move(candidate.BackupPath!, candidate.Path);
                    candidate.BackupPath = null;
                }
            }
            catch (Exception rollbackException)
            {
                rollbackErrors.Add(rollbackException);
            }
        }

        if (rollbackErrors.Count > 0)
        {
            throw new AggregateException(
                "Atomic file edit follow-up failed and rollback was incomplete; recovery backups were retained.",
                [cause, .. rollbackErrors]);
        }

        throw new AtomicFileEditException(
            "Atomic file edit follow-up failed; all published files were restored.", cause);
    }

    private static void VerifyUnchanged(FileCandidate candidate)
    {
        if (candidate.RootPath is not null)
        {
            ValidateNoLinksBelowRoot(candidate.RootPath, candidate.Path);
        }

        var exists = File.Exists(candidate.Path);
        if (exists != candidate.OriginalExists)
        {
            throw new AtomicFileEditException(
                $"'{candidate.Path}' changed existence while the edit batch was being prepared.");
        }

        if (exists)
        {
            var hash = Hash(File.ReadAllBytes(candidate.Path));
            if (!StringComparer.Ordinal.Equals(hash, candidate.OriginalHash))
            {
                throw new AtomicFileEditException(
                    $"'{candidate.Path}' changed while the edit batch was being prepared.");
            }
        }
    }

    private static void Cleanup(IEnumerable<FileCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            DeleteIfPresent(candidate.TemporaryPath);
        }
    }

    private static void DeleteIfPresent(string? path)
    {
        if (path is not null && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string ResolvePath(string path, string? root)
    {
        var resolved = ResolveNativePath(Path.GetFullPath(
            Path.IsPathRooted(path) ? path : Path.Combine(root ?? Environment.CurrentDirectory, path)));
        if (root is null)
        {
            return resolved;
        }

        var relative = Path.GetRelativePath(root, resolved);
        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new AtomicFileEditException(
                $"'{path}' resolves outside the edit root '{root}'.");
        }

        ValidateNoLinksBelowRoot(root, resolved);
        return resolved;
    }

    private static string ResolveNativePath(string path)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            return path;
        }

        var pathRoot = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(pathRoot))
        {
            return path;
        }

        var components = path[pathRoot.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = pathRoot;
        for (var index = 0; index < components.Length; index++)
        {
            var requested = Path.Combine(current, components[index]);
            if (!File.Exists(requested) && !Directory.Exists(requested))
            {
                return components[index..].Aggregate(current, Path.Combine);
            }

            string? native = null;
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var name = Path.GetFileName(entry);
                if (StringComparer.Ordinal.Equals(name, components[index]))
                {
                    native = entry;
                    break;
                }

                if (native is null && NativeNameComparer.Equals(name, components[index]))
                {
                    native = entry;
                }
            }

            current = native ?? requested;
        }

        return current;
    }

    private static void ValidateNoLinksBelowRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".")
        {
            return;
        }

        var current = root;
        foreach (var component in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new AtomicFileEditException(
                        $"'{path}' crosses symbolic link or reparse point '{current}' beneath edit root '{root}'.");
                }
            }
            catch (FileNotFoundException)
            {
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
        }
    }

    private static TextDocument Decode(string path, byte[] bytes, bool exists)
    {
        if (!exists)
        {
            return new TextDocument(string.Empty, HasUtf8Bom: false);
        }

        var hasBom = bytes.AsSpan().StartsWith(Utf8Bom);
        try
        {
            return new TextDocument(
                StrictUtf8.GetString(hasBom ? bytes.AsSpan(3) : bytes),
                hasBom);
        }
        catch (DecoderFallbackException exception)
        {
            throw new AtomicFileEditException(
                $"'{path}' is not valid UTF-8 text and cannot be edited structurally.", exception);
        }
    }

    private static byte[] Encode(TextDocument document)
    {
        var content = StrictUtf8.GetBytes(document.Text);
        return document.HasUtf8Bom ? [.. Utf8Bom, .. content] : content;
    }

    private static int CountOccurrences(string text, string match)
    {
        if (match.Length == 0)
        {
            throw new AtomicFileEditException("A replacement anchor cannot be empty.");
        }

        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(match, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += match.Length;
        }

        return count;
    }

    private static void Require(string? value, FileEditKind kind, string property)
    {
        if (value is null)
        {
            throw new AtomicFileEditException($"{kind} requires {property}.");
        }
    }

    private static void RequireExisting(string path, bool exists, FileEditKind kind)
    {
        if (!exists)
        {
            throw new AtomicFileEditException($"{kind} requires existing file '{path}'.");
        }
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}";

    private static StringComparer NativeNameComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    // ResolveNativePath canonicalizes the casing of existing entries on
    // case-insensitive volumes. Remaining distinct strings must stay distinct:
    // macOS and Windows can both host case-sensitive directories.
    private static StringComparer PathComparer => StringComparer.Ordinal;

    private sealed record TextDocument(string Text, bool HasUtf8Bom);

    private sealed class FileCandidate
    {
        public FileCandidate(
            string path,
            bool originalExists,
            byte[] originalBytes,
            string? originalHash,
            byte[] finalBytes,
            string finalHash,
            IReadOnlyList<AtomicFileEdit> operations,
            string? rootPath = null)
        {
            Path = path;
            OriginalExists = originalExists;
            OriginalBytes = originalBytes;
            OriginalHash = originalHash;
            FinalBytes = finalBytes;
            FinalHash = finalHash;
            Operations = operations;
            RootPath = rootPath;
        }

        public string Path { get; }

        public bool OriginalExists { get; }

        public byte[] OriginalBytes { get; }

        public string? OriginalHash { get; }

        public byte[] FinalBytes { get; }

        public string FinalHash { get; }

        public IReadOnlyList<AtomicFileEdit> Operations { get; }

        public string? RootPath { get; }

        public string? TemporaryPath { get; set; }

        public string? BackupPath { get; set; }
    }

    private sealed class PublicationRollbackException : Exception
    {
        public PublicationRollbackException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
