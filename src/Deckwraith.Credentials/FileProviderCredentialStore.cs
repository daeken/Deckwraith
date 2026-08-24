using System.Security.Cryptography;
using System.Text;
using Deckwraith.Providers.Abstractions;

namespace Deckwraith.Credentials;

public sealed class FileProviderCredentialStore : IProviderCredentialStore, IDisposable
{
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public FileProviderCredentialStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public string StorageKind => "restricted-file";

    public async ValueTask<string?> ReadAsync(
        string credentialId,
        CancellationToken cancellationToken = default)
    {
        var path = CredentialPath(credentialId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return File.Exists(path)
                ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask WriteAsync(
        string credentialId,
        string payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var path = CredentialPath(credentialId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Directory.CreateDirectory(_directory);
            RestrictDirectory(_directory);
            var temporary = Path.Combine(
                _directory,
                $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllTextAsync(temporary, payload, cancellationToken)
                    .ConfigureAwait(false);
                RestrictFile(temporary);
                File.Move(temporary, path, true);
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
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DeleteAsync(
        string credentialId,
        CancellationToken cancellationToken = default)
    {
        var path = CredentialPath(credentialId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            File.Delete(path);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    private string CredentialPath(string credentialId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialId);
        if (credentialId.Length > 256 || credentialId.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("Credential IDs must be at most 256 printable characters.", nameof(credentialId));
        }

        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(credentialId)));
        return Path.Combine(_directory, digest + ".secret");
    }

    private static void RestrictDirectory(string path)
    {
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
}
