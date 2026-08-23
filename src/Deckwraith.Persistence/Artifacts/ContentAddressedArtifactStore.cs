using System.Security.Cryptography;
using Deckwraith.Application.Abstractions;
using Deckwraith.Core.Naming;
using Deckwraith.Core.State;

namespace Deckwraith.Persistence.Artifacts;

public sealed class ContentAddressedArtifactStore : IArtifactStore
{
    private readonly string _rootPath;

    public ContentAddressedArtifactStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
    }

    public async Task<ArtifactReference> PutAsync(
        CanonicalName haunt,
        Stream content,
        string? mediaType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var artifactRoot = ArtifactRoot(haunt);
        if (!Directory.Exists(Path.Combine(_rootPath, "haunts", haunt.Value)))
        {
            throw new DeckStateException($"The haunt '{haunt}' does not exist.");
        }

        var incoming = Path.Combine(artifactRoot, ".incoming");
        Directory.CreateDirectory(incoming);
        SensitiveFilePermissions.RestrictDirectory(incoming);
        var temporaryPath = Path.Combine(incoming, Guid.NewGuid().ToString("N"));
        long length;
        byte[] digest;
        try
        {
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                SensitiveFilePermissions.RestrictFile(temporaryPath);
                var buffer = new byte[64 * 1024];
                length = 0;
                while (true)
                {
                    var read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    hasher.AppendData(buffer, 0, read);
                    length = checked(length + read);
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
                digest = hasher.GetHashAndReset();
            }

            var hex = Convert.ToHexStringLower(digest);
            var directory = Path.Combine(artifactRoot, "sha256", hex[..2]);
            Directory.CreateDirectory(directory);
            SensitiveFilePermissions.RestrictDirectory(directory);
            var finalPath = Path.Combine(directory, hex[2..]);
            if (File.Exists(finalPath))
            {
                File.Delete(temporaryPath);
            }
            else
            {
                File.Move(temporaryPath, finalPath);
                SensitiveFilePermissions.RestrictFile(finalPath);
            }

            var relativePath = Path.GetRelativePath(_rootPath, finalPath).Replace('\\', '/');
            return new ArtifactReference($"sha256:{hex}", length, relativePath, mediaType);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public Task<Stream> OpenReadAsync(
        CanonicalName haunt,
        string hash,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(haunt, hash);
        Stream stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    private string ResolvePath(CanonicalName haunt, string hash)
    {
        const string prefix = "sha256:";
        if (!hash.StartsWith(prefix, StringComparison.Ordinal) || hash.Length != prefix.Length + 64)
        {
            throw new ArgumentException("An artifact hash must be a sha256: digest.", nameof(hash));
        }

        var hex = hash[prefix.Length..];
        if (hex.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("An artifact hash must use lowercase hexadecimal.", nameof(hash));
        }

        return Path.Combine(ArtifactRoot(haunt), "sha256", hex[..2], hex[2..]);
    }

    private string ArtifactRoot(CanonicalName haunt) =>
        Path.Combine(_rootPath, "haunts", haunt.Value, "artifacts");
}
