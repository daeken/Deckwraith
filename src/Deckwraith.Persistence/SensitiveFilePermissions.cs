namespace Deckwraith.Persistence;

internal static class SensitiveFilePermissions
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead |
        UnixFileMode.UserWrite |
        UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void RestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, DirectoryMode);
        }
    }

    public static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, FileMode);
        }
    }

    public static void RestrictTree(string rootPath)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(rootPath))
        {
            return;
        }

        RestrictDirectory(rootPath);
        foreach (var directory in Directory.EnumerateDirectories(rootPath, "*", SearchOption.AllDirectories))
        {
            RestrictDirectory(directory);
        }

        foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
        {
            RestrictFile(file);
        }
    }
}
