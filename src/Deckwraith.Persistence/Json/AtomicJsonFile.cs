using System.Text.Json;

namespace Deckwraith.Persistence.Json;

internal static class AtomicJsonFile
{
    public static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, DeckJson.Options, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new JsonException($"'{path}' contained JSON null instead of {typeof(T).Name}.");
    }

    public static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("The JSON path must have a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        SensitiveFilePermissions.RestrictDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                SensitiveFilePermissions.RestrictFile(temporaryPath);
                await JsonSerializer.SerializeAsync(stream, value, DeckJson.Options, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
            SensitiveFilePermissions.RestrictFile(path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
