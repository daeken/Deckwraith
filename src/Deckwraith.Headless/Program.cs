using System.Text.Json;
using System.Text.Json.Serialization;
using Deckwraith.Application.Inference;
using Deckwraith.Application.Hosting;
using Deckwraith.Application.State;
using Deckwraith.Continuity;
using Deckwraith.Hosting;
using Deckwraith.Kernels.Abstractions;
using Deckwraith.Kernels.CSharp;
using Deckwraith.Kernels.PowerShell;
using Deckwraith.Mcp;
using Deckwraith.Notebooks;
using Deckwraith.Notebooks.Model;
using Deckwraith.Persistence;
using Deckwraith.Persistence.Archives;
using Deckwraith.Persistence.Artifacts;
using Deckwraith.Persistence.Git;
using Deckwraith.Persistence.State;
using Deckwraith.PowerShell.Hosting;
using Deckwraith.PowerShell.Serialization;
using Deckwraith.Providers.Abstractions;

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
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
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
                "init" when arguments.Length == 2 => await InitializeDeckAsync(
                    state, rootPath, cancellationToken).ConfigureAwait(false),
                "create-wraith" when arguments.Length == 3 =>
                    await state.CreateWraithAsync(arguments[2], cancellationToken).ConfigureAwait(false),
                "archive-wraith" when arguments.Length == 3 =>
                    await state.ArchiveWraithAsync(arguments[2], cancellationToken).ConfigureAwait(false),
                "restore-wraith" when arguments.Length == 3 =>
                    await state.RestoreWraithAsync(arguments[2], cancellationToken).ConfigureAwait(false),
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
                "start-run" when arguments.Length is 6 or 7 =>
                    await StartRunAsync(rootPath, arguments, cancellationToken).ConfigureAwait(false),
                "turn" when arguments.Length == 5 =>
                    await ExecuteTurnAsync(rootPath, arguments, cancellationToken).ConfigureAwait(false),
                "run-openai" when arguments.Length == 7 =>
                    await RunOpenAiAsync(rootPath, arguments, cancellationToken).ConfigureAwait(false),
                "run-provider" when arguments.Length == 8 =>
                    await RunProviderAsync(rootPath, arguments, cancellationToken).ConfigureAwait(false),
                "replace-shell" when arguments.Length is 6 or 7 =>
                    await ReplaceShellAsync(rootPath, arguments, cancellationToken).ConfigureAwait(false),
                "complete-run" when arguments.Length == 5 =>
                    await EndRunAsync(rootPath, arguments, complete: true, cancellationToken)
                        .ConfigureAwait(false),
                "cancel-run" when arguments.Length == 5 =>
                    await EndRunAsync(rootPath, arguments, complete: false, cancellationToken)
                        .ConfigureAwait(false),
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
                "mcp-servers" when arguments.Length == 3 =>
                    await ConfigureMcpServersAsync(rootPath, arguments[2], cancellationToken)
                        .ConfigureAwait(false),
                "mcp-assign-global" when arguments.Length == 3 =>
                    await AssignGlobalMcpAsync(rootPath, arguments[2], cancellationToken)
                        .ConfigureAwait(false),
                "mcp-assign-wraith" when arguments.Length == 4 =>
                    await AssignWraithMcpAsync(
                        rootPath, arguments[2], arguments[3], cancellationToken)
                        .ConfigureAwait(false),
                "mcp-catalog" when arguments.Length == 3 =>
                    await ReadMcpCatalogAsync(rootPath, arguments[2], cancellationToken)
                        .ConfigureAwait(false),
                "compact" when arguments.Length is >= 5 and <= 7 =>
                    await CompactAsync(rootPath, arguments, cancellationToken)
                        .ConfigureAwait(false),
                "recover" when arguments.Length == 3 =>
                    await RecoverAsync(rootPath, arguments[2], cancellationToken)
                        .ConfigureAwait(false),
                "reverse" when arguments.Length == 3 =>
                    await ReverseAsync(rootPath, arguments[2], cancellationToken)
                        .ConfigureAwait(false),
                "bridge" when arguments.Length == 3 =>
                    await ExecuteBridgeRequestAsync(rootPath, arguments[2], cancellationToken)
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

    private static async Task<object> InitializeDeckAsync(
        StateSpine state,
        string rootPath,
        CancellationToken cancellationToken)
    {
        var initialized = await state.InitializeWithSetupAsync(cancellationToken)
            .ConfigureAwait(false);
        return new
        {
            rootPath = Path.GetFullPath(rootPath),
            commitId = initialized.CommitId,
            setupWraith = initialized.SetupWraith,
            setupHaunt = initialized.SetupHaunt,
        };
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
        var provider = arguments.Length == 7
            ? arguments[4]
            : "openai-codex-subscription";
        var model = arguments.Length == 7 ? arguments[5] : arguments[4];
        var objective = arguments.Length == 7 ? arguments[6] : arguments[5];
        return await runtime.StartRunAsync(
            arguments[2],
            ParseOptionalHaunt(arguments[3]),
            objective,
            provider,
            model,
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

    private static async Task<object> RunProviderAsync(
        string rootPath,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        using var runtime = CreateInferenceRuntime(rootPath);
        var started = await runtime.StartRunAsync(
            arguments[2],
            ParseOptionalHaunt(arguments[3]),
            arguments[6],
            arguments[4],
            arguments[5],
            cancellationToken).ConfigureAwait(false);
        var turn = await runtime.ExecuteTurnAsync(
            arguments[2], started.Run.RunId, arguments[7], cancellationToken).ConfigureAwait(false);
        return new { started, turn };
    }

    private static InferenceRuntime CreateInferenceRuntime(string rootPath)
    {
        var deckState = new JsonDeckStateStore(rootPath);
        var archive = new JsonlAgentArchive(rootPath);
        var checkpoints = new GitCheckpointStore(rootPath);
        var artifacts = new ArtifactRuntime(
            deckState,
            new ContentAddressedArtifactStore(rootPath),
            archive,
            checkpoints);
        var durableState = new DurableStateRuntime(
            deckState,
            new JsonDurableValueStore(rootPath),
            archive,
            checkpoints);
        var tools = new PowerShellToolBroker(new PowerShellRuntimeManager(
            rootPath,
            durableState,
            artifacts,
            archive,
            checkpoints,
            mcp: new McpCatalogRuntime(
                rootPath, deckState, archive, checkpoints),
            ownsMcp: true));
        return new InferenceRuntime(
            deckState,
            new JsonInferenceStateStore(rootPath),
            archive,
            checkpoints,
            CreateProviderRegistry(),
            tools);
    }

    private static async Task<object> ReplaceShellAsync(
        string rootPath,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        using var runtime = CreateInferenceRuntime(rootPath);
        var provider = arguments.Length == 7
            ? arguments[4]
            : "openai-codex-subscription";
        var model = arguments.Length == 7 ? arguments[5] : arguments[4];
        var reason = arguments.Length == 7 ? arguments[6] : arguments[5];
        return await runtime.ReplaceShellAsync(
            arguments[2],
            arguments[3],
            provider,
            model,
            reason,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object> EndRunAsync(
        string rootPath,
        string[] arguments,
        bool complete,
        CancellationToken cancellationToken)
    {
        using var runtime = CreateInferenceRuntime(rootPath);
        return complete
            ? await runtime.CompleteRunAsync(
                arguments[2], arguments[3], arguments[4], cancellationToken).ConfigureAwait(false)
            : await runtime.CancelRunAsync(
                arguments[2], arguments[3], arguments[4], cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object> RunPowerShellAsync(
        string rootPath,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        var deckState = new JsonDeckStateStore(rootPath);
        var archive = new JsonlAgentArchive(rootPath);
        var checkpoints = new GitCheckpointStore(rootPath);
        var artifactRuntime = new ArtifactRuntime(
            deckState,
            new ContentAddressedArtifactStore(rootPath),
            archive,
            checkpoints);
        var durableState = new DurableStateRuntime(
            deckState,
            new JsonDurableValueStore(rootPath),
            archive,
            checkpoints);
        using var manager = new PowerShellRuntimeManager(
            rootPath,
            durableState,
            artifactRuntime,
            archive,
            checkpoints,
            mcp: new McpCatalogRuntime(
                rootPath, deckState, archive, checkpoints),
            ownsMcp: true);
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

    private static async Task<object> ConfigureMcpServersAsync(
        string rootPath,
        string registryPath,
        CancellationToken cancellationToken)
    {
        var registry = await ReadJsonAsync<McpServerRegistry>(
            registryPath, cancellationToken).ConfigureAwait(false);
        using var runtime = CreateMcpRuntime(rootPath);
        var commitId = await runtime.ConfigureServersAsync(
            registry.Servers, cancellationToken).ConfigureAwait(false);
        return new { registry, commitId };
    }

    private static async Task<object> AssignGlobalMcpAsync(
        string rootPath,
        string assignmentPath,
        CancellationToken cancellationToken)
    {
        var assignment = await ReadJsonAsync<McpAssignmentDocument>(
            assignmentPath, cancellationToken).ConfigureAwait(false);
        using var runtime = CreateMcpRuntime(rootPath);
        var commitId = await runtime.WriteGlobalAssignmentAsync(
            assignment, cancellationToken).ConfigureAwait(false);
        return new { assignment, commitId };
    }

    private static async Task<object> AssignWraithMcpAsync(
        string rootPath,
        string wraith,
        string assignmentPath,
        CancellationToken cancellationToken)
    {
        var assignment = await ReadJsonAsync<McpAssignmentDocument>(
            assignmentPath, cancellationToken).ConfigureAwait(false);
        using var runtime = CreateMcpRuntime(rootPath);
        var commitId = await runtime.WriteWraithAssignmentAsync(
            wraith, assignment, cancellationToken).ConfigureAwait(false);
        return new { wraith, assignment, commitId };
    }

    private static async Task<object> ReadMcpCatalogAsync(
        string rootPath,
        string wraith,
        CancellationToken cancellationToken)
    {
        using var runtime = CreateMcpRuntime(rootPath);
        return await runtime.GetEffectiveCatalogAsync(
            wraith, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static McpCatalogRuntime CreateMcpRuntime(string rootPath) =>
        new(
            rootPath,
            new JsonDeckStateStore(rootPath),
            new JsonlAgentArchive(rootPath),
            new GitCheckpointStore(rootPath));

    private static async Task<object> CompactAsync(
        string rootPath,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        var runtime = new CompactionRuntime(
            new JsonDeckStateStore(rootPath),
            new JsonInferenceStateStore(rootPath),
            new JsonlAgentArchive(rootPath),
            new JsonCompactionStore(rootPath),
            new GitCheckpointStore(rootPath),
            CreateProviderRegistry());
        var fraction = arguments.Length >= 6
            ? double.Parse(arguments[5], System.Globalization.CultureInfo.InvariantCulture)
            : 0.25;
        var minimumRecords = arguments.Length == 7
            ? int.Parse(arguments[6], System.Globalization.CultureInfo.InvariantCulture)
            : 8;
        var result = await runtime.CompactAsync(
            arguments[2],
            arguments[3],
            arguments[4],
            fraction,
            minimumRecords,
            cancellationToken).ConfigureAwait(false);
        return new
        {
            compacted = result is not null,
            result,
        };
    }

    private static Task<RecoveryResult> RecoverAsync(
        string rootPath,
        string wraith,
        CancellationToken cancellationToken) =>
        new RecoveryRuntime(
            rootPath,
            new JsonDeckStateStore(rootPath),
            new JsonInferenceStateStore(rootPath),
            new JsonlAgentArchive(rootPath),
            new JsonCompactionStore(rootPath),
            new GitCheckpointStore(rootPath))
        .RecoverAsync(wraith, cancellationToken);

    private static Task<GitReversalResult> ReverseAsync(
        string rootPath,
        string commit,
        CancellationToken cancellationToken) =>
        new GitReversalRuntime(
            rootPath,
            new GitCheckpointStore(rootPath))
        .ReverseCommitAsync(commit, cancellationToken);

    private static async Task<HostResponse> ExecuteBridgeRequestAsync(
        string rootPath,
        string requestPath,
        CancellationToken cancellationToken)
    {
        var request = await ReadJsonAsync<HostRequest>(
            requestPath, cancellationToken).ConfigureAwait(false);
        using var host = await DeckwraithHost.OpenAsync(
            rootPath, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await host.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadJsonAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            Path.GetFullPath(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(
            stream, OutputOptions, cancellationToken).ConfigureAwait(false) ??
            throw new JsonException($"'{path}' contains no JSON value.");
    }

    private static string? ParseOptionalHaunt(string value) => value == "-" ? null : value;

    private static ModelProviderRegistry CreateProviderRegistry() =>
        DeckwraithHostOptions.CreateDefault().CreateProviderRegistry();

    private static void WriteUsage()
    {
        Console.Error.WriteLine(
            "Usage: deckwraith <init|create-wraith|create-haunt|rename-wraith|rename-haunt|" +
            "archive-wraith|restore-wraith|" +
            "resolve-wraith|resolve-haunt|identity|archive|store-artifact|start-run|turn|run-openai|run-provider|" +
            "replace-shell|complete-run|cancel-run|" +
            "powershell|deckbook|add-cell|run-cell|run-remaining|deckbook-context|" +
            "mcp-servers|mcp-assign-global|mcp-assign-wraith|mcp-catalog|" +
            "compact|recover|reverse|bridge> " +
            "<deck-path> [arguments]\n" +
            "  start-run <deck> <wraith> <haunt|-> [provider] <model> <objective>\n" +
            "  turn <deck> <wraith> <run-id> <message>\n" +
            "  run-openai <deck> <wraith> <haunt|-> <model> <objective> <message>\n" +
            "  run-provider <deck> <wraith> <haunt|-> <provider> <model> <objective> <message>\n" +
            "  replace-shell <deck> <wraith> <run-id> [provider] <model> <reason>\n" +
            "  complete-run <deck> <wraith> <run-id> <reason>\n" +
            "  cancel-run <deck> <wraith> <run-id> <reason>\n" +
            "  powershell <deck> <wraith> <run|-> <haunt|-> <script>\n" +
            "  deckbook <deck> <wraith> <haunt>\n" +
            "  add-cell <deck> <wraith> <haunt> <name> <kind> <kernel|-> <source>\n" +
            "  run-cell <deck> <wraith> <haunt> <name> [run|-]\n" +
            "  run-remaining <deck> <wraith> <haunt> <from> [run|-]\n" +
            "  deckbook-context <deck> <wraith> <haunt> <active|->\n" +
            "  mcp-servers <deck> <registry-json>\n" +
            "  mcp-assign-global <deck> <assignment-json>\n" +
            "  mcp-assign-wraith <deck> <wraith> <assignment-json>\n" +
            "  mcp-catalog <deck> <wraith>\n" +
            "  compact <deck> <wraith> <provider> <model> [fraction] [minimum-records]\n" +
            "  recover <deck> <wraith>\n" +
            "  reverse <deck> <commit>\n" +
            "  bridge <deck> <host-request-json>");
    }

    private sealed class NotebookComposition : IDisposable
    {
        private readonly PowerShellRuntimeManager _runspaces;
        private readonly PowerShellCellKernel _powerShellKernel;
        private readonly CSharpCellKernel _csharpKernel;

        public NotebookComposition(string rootPath)
        {
            var deckState = new JsonDeckStateStore(rootPath);
            var archive = new JsonlAgentArchive(rootPath);
            var checkpoints = new GitCheckpointStore(rootPath);
            var artifactRuntime = new ArtifactRuntime(
                deckState,
                new ContentAddressedArtifactStore(rootPath),
                archive,
                checkpoints);
            var durableState = new DurableStateRuntime(
                deckState,
                new JsonDurableValueStore(rootPath),
                archive,
                checkpoints);
            _runspaces = new PowerShellRuntimeManager(
                rootPath,
                durableState,
                artifactRuntime,
                archive,
                checkpoints,
                mcp: new McpCatalogRuntime(
                    rootPath, deckState, archive, checkpoints),
                ownsMcp: true);
            _powerShellKernel = new PowerShellCellKernel(_runspaces);
            _csharpKernel = new CSharpCellKernel(
                durableState, artifactRuntime, archive, checkpoints);
            Notebooks = new DeckbookRuntime(
                rootPath,
                deckState,
                new CellKernelRegistry([_powerShellKernel, _csharpKernel]),
                archive,
                checkpoints);
        }

        public DeckbookRuntime Notebooks { get; }

        public void Dispose()
        {
            Notebooks.Dispose();
            _csharpKernel.Dispose();
            _powerShellKernel.Dispose();
            _runspaces.Dispose();
        }
    }
}
