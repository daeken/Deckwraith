using System.Text.Json;
using Deckwraith.Persistence;

return await DeckwraithCli.RunAsync(args).ConfigureAwait(false);

internal static class DeckwraithCli
{
    private static readonly JsonSerializerOptions OutputOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments.Length < 2)
        {
            WriteUsage();
            return 2;
        }

        try
        {
            var command = arguments[0];
            var rootPath = arguments[1];
            using var state = DeckwraithPersistence.CreateStateSpine(rootPath);
            object result = command switch
            {
                "init" when arguments.Length == 2 => new
                {
                    rootPath = Path.GetFullPath(rootPath),
                    commitId = await state.InitializeAsync().ConfigureAwait(false),
                },
                "create-wraith" when arguments.Length == 3 =>
                    await state.CreateWraithAsync(arguments[2]).ConfigureAwait(false),
                "create-haunt" when arguments.Length == 3 =>
                    await state.CreateHauntAsync(arguments[2]).ConfigureAwait(false),
                "rename-wraith" when arguments.Length == 4 =>
                    await state.RenameWraithAsync(arguments[2], arguments[3]).ConfigureAwait(false),
                "rename-haunt" when arguments.Length == 4 =>
                    await state.RenameHauntAsync(arguments[2], arguments[3]).ConfigureAwait(false),
                "resolve-wraith" when arguments.Length == 3 => new
                {
                    name = (await state.ResolveWraithAsync(arguments[2]).ConfigureAwait(false)).Value,
                },
                "resolve-haunt" when arguments.Length == 3 => new
                {
                    name = (await state.ResolveHauntAsync(arguments[2]).ConfigureAwait(false)).Value,
                },
                "identity" when arguments.Length == 3 =>
                    await state.ReadIdentityAsync(arguments[2]).ConfigureAwait(false),
                "archive" when arguments.Length == 3 =>
                    await state.ReadArchiveAsync(arguments[2]).ConfigureAwait(false),
                "store-artifact" when arguments.Length is 5 or 6 =>
                    await StoreArtifactAsync(state, arguments).ConfigureAwait(false),
                _ => throw new ArgumentException($"Unknown command or invalid arguments: '{command}'."),
            };

            Console.WriteLine(JsonSerializer.Serialize(result, OutputOptions));
            return 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static async Task<object> StoreArtifactAsync(
        Deckwraith.Application.State.StateSpine state,
        string[] arguments)
    {
        await using var stream = new FileStream(
            arguments[4],
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await state.StoreArtifactAsync(
            arguments[2],
            arguments[3],
            stream,
            arguments.Length == 6 ? arguments[5] : null).ConfigureAwait(false);
    }

    private static void WriteUsage()
    {
        Console.Error.WriteLine(
            "Usage: deckwraith <init|create-wraith|create-haunt|rename-wraith|rename-haunt|" +
            "resolve-wraith|resolve-haunt|identity|archive|store-artifact> <deck-path> [arguments]");
    }
}
