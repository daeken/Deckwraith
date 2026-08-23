using System.Text.Json;
using Deckwraith.Application.Inference;
using Deckwraith.Application.State;
using Deckwraith.Kernels.Abstractions;
using Deckwraith.Kernels.PowerShell;
using Deckwraith.Notebooks;
using Deckwraith.Notebooks.Model;
using Deckwraith.Persistence;
using Deckwraith.Persistence.Archives;
using Deckwraith.Persistence.Git;
using Deckwraith.Persistence.State;
using Deckwraith.PowerShell.Hosting;
using Deckwraith.PowerShell.Serialization;
using Deckwraith.Providers.OpenAI;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};
return await DeckwraithCli.RunAsync(args, shutdown.Token).ConfigureAwait(false);

internal static class DeckwraithCli
{
    private static readonly JsonSerializerOptions OutputOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(
        string[] arguments,
        CancellationToken cancellationToken = default)
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
                    commitId = await state.InitializeAsync(cancellationToken).ConfigureAwait(false),
                },
                "create-wraith" when arguments.Length == 3 =>
                    await state.CreateWraithAsync(arguments[2], cancellationToken).ConfigureAwait(false),
                "create-haunt" when arguments.Length == 3 =>
                    await state.CreateHauntAsync(arguments[2], cancellationToken).ConfigureAwait(false),
                "rename-wraith" when arguments.Length == 4 =>
                    await state.RenameWraithAsync(
                        arguments[2], arguments[3], cancellationToken).ConfigureAwait(false),
                "rename-haunt" when arguments.Length == 4 =>
                    await state.RenameHauntAsync(
                        arguments[2], arguments[3], cancellationToken).ConfigureAwait(false),
                "resolve-wraith" when arguments.Length == 3 => new
                {
                    name = (await state.ResolveWraithAsync(
                        arguments[2], cancellationToken).ConfigureAwait(false)).Value,
                },
                "resolve-haunt" when arguments.Length == 3 => new
                {
                    name = (await state.ResolveHauntAsync(
                        arguments[2], cancellationToken).ConfigureAwait(false)).Value,
                },
                "identity" when arguments.Length == 3 =>
                    await state.ReadIdentityAsync(arguments[2], cancellationToken).ConfigureAwait(false),
                "archive" when arguments.Length == 3 =>
                    await state.ReadArchiveAsync(arguments[2], cancellationToken).ConfigureAwait(false),
                "store-artifact" when arguments.Length is 5 or 6 =>
                    await StoreArtifactAsync(state, arguments, cancellationToken).ConfigureAwait(false),
                "start-run" when arguments.Length == 6 =>
                    await StartRunAsync(rootPath, arguments, cancellationToken).ConfigureAwait(false),
                "turn" when arguments.Length == 5 =>
                    await ExecuteTurnAsync(rootPath, arguments, cancellationToken).ConfigureAwait(false),
                "run-openai" when arguments.Length == 7 =>
                    await RunOpenAiAsync(rootPath, arguments, cancellationToken).ConfigureAwait(false),
                "powershell" when arguments.Length == 6 =>
                    await RunPowerShellAsync(rootPath, arguments, cancellationToken).ConfigureAwait(false),
                "deckbook" when arguments.Length == 4 =>
                    await ReadDeckbookAsync(rootPath, arguments, cancellationToken).ConfigureAwait(false),
                "add-cell" when arguments.Length == 8 =>
                    await AddCellAsync(rootPath, arguments, cancellationToken).ConfigureAwait(false),
                "run-cell" when arguments.Length is 5 or 6 =>
                    await RunCellAsync(rootPath, arguments, cancellationToken).ConfigureAwait(false),
                "run-remaining" when arguments.Length is 5 or 6 =>
                    await RunRemainingAsync(rootPath, arguments, cancellationToken).ConfigureAwait(false),
                "deckbook-context" when arguments.Length == 5 =>
                    await CompileDeckbookContextAsync(rootPath, arguments, cancellationToken)
                        .ConfigureAwait(false),
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
        string[] arguments,
        CancellationToken cancellationToken)
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
            arguments.Length == 6 ? arguments[5] : null,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object> StartRunAsync(
        string rootPath,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        using var runtime = CreateInferenceRuntime(rootPath);
        return await runtime.StartRunAsync(
            arguments[2],
            ParseOptionalHaunt(arguments[3]),
            arguments[5],
            "openai-codex-subscription",
            arguments[4],
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object> ExecuteTurnAsync(
        string rootPath,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        using var runtime = CreateInferenceRuntime(rootPath);
        return await runtime.ExecuteTurnAsync(
            arguments[2], arguments[3], arguments[4], cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object> RunOpenAiAsync(
        string rootPath,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        using var runtime = CreateInferenceRuntime(rootPath);
        var started = await runtime.StartRunAsync(
            arguments[2],
            ParseOptionalHaunt(arguments[3]),
            arguments[5],
            "openai-codex-subscription",
            arguments[4],
            cancellationToken).ConfigureAwait(false);
        var turn = await runtime.ExecuteTurnAsync(
            arguments[2], started.Run.RunId, arguments[6], cancellationToken).ConfigureAwait(false);
        return new { started, turn };
    }

    private static InferenceRuntime CreateInferenceRuntime(string rootPath)
    {
        var provider = new CodexAppServerProvider(new CodexAppServerProviderOptions(
            ResolveCodexExecutable(),
            Path.GetTempPath()));
        return new InferenceRuntime(
            new JsonDeckStateStore(rootPath),
            new JsonInferenceStateStore(rootPath),
            new JsonlAgentArchive(rootPath),
            new GitCheckpointStore(rootPath),
            new ModelProviderRegistry([provider]));
    }

    private static async Task<object> RunPowerShellAsync(
        string rootPath,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        var deckState = new JsonDeckStateStore(rootPath);
        var archive = new JsonlAgentArchive(rootPath);
        var checkpoints = new GitCheckpointStore(rootPath);
        var durableState = new DurableStateRuntime(
            deckState,
            new JsonDurableValueStore(rootPath),
            archive,
            checkpoints);
        using var manager = new PowerShellRuntimeManager(
            rootPath, durableState, archive, checkpoints);
        var result = await manager.ExecuteAsync(
            new PowerShellInvocationContext(
                arguments[2],
                arguments[3] == "-" ? null : arguments[3],
                ParseOptionalHaunt(arguments[4])),
            arguments[5],
            cancellationToken).ConfigureAwait(false);
        return new
        {
            output = result.Output.Select(PortablePowerShellValue.ToJsonElement).ToArray(),
            errors = result.Errors.Select(error => new
            {
                error.FullyQualifiedErrorId,
                message = error.ToString(),
                category = error.CategoryInfo.Category.ToString(),
            }).ToArray(),
            result.Runtime,
            result.ToolsReloaded,
        };
    }

    private static async Task<object> ReadDeckbookAsync(
        string rootPath,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        using var composition = new NotebookComposition(rootPath);
        return await composition.Notebooks.GetAsync(
            arguments[2], arguments[3], cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object> AddCellAsync(
        string rootPath,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        using var composition = new NotebookComposition(rootPath);
        var kind = Enum.Parse<DeckbookCellKind>(arguments[5], ignoreCase: true);
        return await composition.Notebooks.InsertAsync(
            arguments[2],
            arguments[3],
            new InsertDeckbookCell(
                arguments[4],
                kind,
                arguments[7],
                arguments[6] == "-" ? null : arguments[6]),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object> RunCellAsync(
        string rootPath,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        using var composition = new NotebookComposition(rootPath);
        return await composition.Notebooks.RunCellAsync(
            arguments[2],
            arguments[3],
            arguments[4],
            arguments.Length == 6 && arguments[5] != "-" ? arguments[5] : null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object> RunRemainingAsync(
        string rootPath,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        using var composition = new NotebookComposition(rootPath);
        return await composition.Notebooks.RunRemainingAsync(
            arguments[2],
            arguments[3],
            arguments[4],
            arguments.Length == 6 && arguments[5] != "-" ? arguments[5] : null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object> CompileDeckbookContextAsync(
        string rootPath,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        using var composition = new NotebookComposition(rootPath);
        return await composition.Notebooks.CompileContextAsync(
            arguments[2],
            arguments[3],
            arguments[4] == "-" ? null : arguments[4],
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string? ParseOptionalHaunt(string value) => value == "-" ? null : value;

    private static string ResolveCodexExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("DECKWRAITH_CODEX_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        const string desktopPath = "/Applications/ChatGPT.app/Contents/Resources/codex";
        return File.Exists(desktopPath) ? desktopPath : "codex";
    }

    private static void WriteUsage()
    {
        Console.Error.WriteLine(
            "Usage: deckwraith <init|create-wraith|create-haunt|rename-wraith|rename-haunt|" +
            "resolve-wraith|resolve-haunt|identity|archive|store-artifact|start-run|turn|run-openai|" +
            "powershell|deckbook|add-cell|run-cell|run-remaining|deckbook-context> " +
            "<deck-path> [arguments]\n" +
            "  start-run <deck> <wraith> <haunt|-> <model> <objective>\n" +
            "  turn <deck> <wraith> <run-id> <message>\n" +
            "  run-openai <deck> <wraith> <haunt|-> <model> <objective> <message>\n" +
            "  powershell <deck> <wraith> <run|-> <haunt|-> <script>\n" +
            "  deckbook <deck> <wraith> <haunt>\n" +
            "  add-cell <deck> <wraith> <haunt> <name> <kind> <kernel|-> <source>\n" +
            "  run-cell <deck> <wraith> <haunt> <name> [run|-]\n" +
            "  run-remaining <deck> <wraith> <haunt> <from> [run|-]\n" +
            "  deckbook-context <deck> <wraith> <haunt> <active|->");
    }

    private sealed class NotebookComposition : IDisposable
    {
        private readonly PowerShellRuntimeManager _runspaces;
        private readonly PowerShellCellKernel _kernel;

        public NotebookComposition(string rootPath)
        {
            var deckState = new JsonDeckStateStore(rootPath);
            var archive = new JsonlAgentArchive(rootPath);
            var checkpoints = new GitCheckpointStore(rootPath);
            var durableState = new DurableStateRuntime(
                deckState,
                new JsonDurableValueStore(rootPath),
                archive,
                checkpoints);
            _runspaces = new PowerShellRuntimeManager(
                rootPath, durableState, archive, checkpoints);
            _kernel = new PowerShellCellKernel(_runspaces);
            Notebooks = new DeckbookRuntime(
                rootPath,
                deckState,
                new CellKernelRegistry([_kernel]),
                archive,
                checkpoints);
        }

        public DeckbookRuntime Notebooks { get; }

        public void Dispose()
        {
            Notebooks.Dispose();
            _kernel.Dispose();
            _runspaces.Dispose();
        }
    }
}
