using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Deckwraith.Application.Abstractions;
using Deckwraith.Core.Archives;
using Deckwraith.Core.Naming;
using Deckwraith.Core.Serialization;
using Deckwraith.Core.State;
using Deckwraith.Kernels.Abstractions;
using Deckwraith.Notebooks.Model;
using Deckwraith.Notebooks.Persistence;

namespace Deckwraith.Notebooks;

public sealed record InsertDeckbookCell(
    string Name,
    DeckbookCellKind Kind,
    string Source,
    string? Kernel = null,
    CellContextPolicy ContextPolicy = CellContextPolicy.WhenRelevant,
    string? Synopsis = null,
    string? Before = null,
    string? After = null);

public sealed class DeckbookRuntime : IDisposable
{
    private const long PositionStep = 1_024;

    private readonly IDeckStateStore _deckState;
    private readonly DeckbookFileStore _store;
    private readonly ICellKernelRegistry _kernels;
    private readonly IAgentArchive _archive;
    private readonly ICheckpointStore _checkpoints;
    private readonly IDeckClock _clock;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
        new(StringComparer.Ordinal);

    public DeckbookRuntime(
        string rootPath,
        IDeckStateStore deckState,
        ICellKernelRegistry kernels,
        IAgentArchive archive,
        ICheckpointStore checkpoints,
        IDeckClock? clock = null)
    {
        _deckState = deckState;
        _store = new DeckbookFileStore(rootPath);
        _kernels = kernels;
        _archive = archive;
        _checkpoints = checkpoints;
        _clock = clock ?? SystemDeckClock.Instance;
    }

    public async Task<DeckbookSnapshot> GetAsync(
        string wraith,
        string haunt,
        CancellationToken cancellationToken = default)
    {
        var address = await ResolveAsync(wraith, haunt, cancellationToken).ConfigureAwait(false);
        var gate = Gate(address);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_store.Exists(address.Wraith, address.Haunt))
            {
                return new DeckbookSnapshot(
                    new DeckbookDocument(
                        DeckbookDocument.CurrentSchemaVersion,
                        address.Wraith.Value,
                        address.Haunt.Value,
                        0,
                        new Dictionary<string, string>(StringComparer.Ordinal),
                        [],
                        _clock.UtcNow),
                    []);
            }

            var deckbook = await _store.EnsureAsync(
                address.Wraith, address.Haunt, _clock.UtcNow, cancellationToken)
                .ConfigureAwait(false);
            return await ReadSnapshotAsync(address, deckbook, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DeckbookCellView> InsertAsync(
        string wraith,
        string haunt,
        InsertDeckbookCell request,
        CancellationToken cancellationToken = default)
    {
        ValidateInsert(request);
        var address = await ResolveAsync(wraith, haunt, cancellationToken).ConfigureAwait(false);
        var name = CanonicalName.Parse(request.Name).Value;
        var gate = Gate(address);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var deckbook = await _store.EnsureAsync(
                address.Wraith, address.Haunt, _clock.UtcNow, cancellationToken)
                .ConfigureAwait(false);
            var cells = (await _store.ReadCellsAsync(
                address.Wraith, address.Haunt, deckbook, cancellationToken)
                .ConfigureAwait(false)).ToList();
            EnsureNameAvailable(deckbook, cells, name);
            var position = AllocatePosition(cells, request.Before, request.After);
            var insertionIndex = cells.Count(cell => cell.Position < position);
            var cell = new DeckbookCellDocument(
                DeckbookCellDocument.CurrentSchemaVersion,
                name,
                position,
                request.Kind,
                NormalizeKernel(request.Kind, request.Kernel),
                DeckbookFileStore.SourceFileFor(request.Kind, request.Kernel),
                request.ContextPolicy,
                1,
                request.Kind is DeckbookCellKind.Code,
                request.Synopsis,
                null);
            cells.Add(cell);
            cells = Order(cells);
            InvalidateFrom(cells, insertionIndex);
            cell = cells.Single(candidate => candidate.Name == name);
            deckbook = Advance(deckbook, cells);
            await PersistCellsAsync(address, deckbook, cells, cancellationToken).ConfigureAwait(false);
            await _store.WriteSourceAsync(
                address.Wraith, address.Haunt, cell, request.Source, cancellationToken)
                .ConfigureAwait(false);
            await AppendAsync(address, "deckbook.cell-inserted", new
            {
                cell.Name,
                cell.Position,
                cell.Kind,
                cell.Kernel,
                cell.Revision,
            }, cancellationToken).ConfigureAwait(false);
            await CheckpointAsync(address, "deckbook-cell-inserted", cancellationToken)
                .ConfigureAwait(false);
            return new DeckbookCellView(cell, request.Source, null);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DeckbookCellView> EditAsync(
        string wraith,
        string haunt,
        string name,
        string source,
        DeckbookCellKind? kind = null,
        string? kernel = null,
        string? synopsis = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var address = await ResolveAsync(wraith, haunt, cancellationToken).ConfigureAwait(false);
        var gate = Gate(address);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadStateAsync(address, cancellationToken).ConfigureAwait(false);
            var canonical = ResolveCellName(state.Deckbook, state.Cells, name);
            var index = state.Cells.FindIndex(cell => cell.Name == canonical);
            var current = state.Cells[index];
            var nextKind = kind ?? current.Kind;
            var nextKernel = NormalizeKernel(nextKind, kernel ?? current.Kernel);
            var edited = current with
            {
                Kind = nextKind,
                Kernel = nextKernel,
                SourceFile = DeckbookFileStore.SourceFileFor(nextKind, nextKernel),
                Revision = checked(current.Revision + 1),
                Synopsis = synopsis ?? current.Synopsis,
            };
            state.Cells[index] = edited;
            InvalidateFrom(state.Cells, index);
            edited = state.Cells[index];
            var deckbook = Advance(state.Deckbook, state.Cells);
            await PersistCellsAsync(address, deckbook, state.Cells, cancellationToken)
                .ConfigureAwait(false);
            await _store.WriteSourceAsync(
                address.Wraith, address.Haunt, edited, source, cancellationToken)
                .ConfigureAwait(false);
            await AppendAsync(address, "deckbook.cell-edited", new
            {
                edited.Name,
                edited.Kind,
                edited.Kernel,
                edited.Revision,
                sourceHash = CanonicalJson.Hash(source),
            }, cancellationToken).ConfigureAwait(false);
            await CheckpointAsync(address, "deckbook-cell-edited", cancellationToken)
                .ConfigureAwait(false);
            return new DeckbookCellView(
                edited,
                source,
                await _store.ReadOutputAsync(
                    address.Wraith,
                    address.Haunt,
                    edited.LastExecution?.OutputHash,
                    cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DeckbookSnapshot> MoveAsync(
        string wraith,
        string haunt,
        string name,
        string? before = null,
        string? after = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSingleAnchor(before, after);
        var address = await ResolveAsync(wraith, haunt, cancellationToken).ConfigureAwait(false);
        var gate = Gate(address);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadStateAsync(address, cancellationToken).ConfigureAwait(false);
            var canonical = ResolveCellName(state.Deckbook, state.Cells, name);
            var oldIndex = state.Cells.FindIndex(cell => cell.Name == canonical);
            var moving = state.Cells[oldIndex];
            state.Cells.RemoveAt(oldIndex);
            var position = AllocatePosition(state.Cells, before, after);
            moving = moving with { Position = position };
            state.Cells.Add(moving);
            state.Cells = Order(state.Cells);
            var newIndex = state.Cells.FindIndex(cell => cell.Name == canonical);
            InvalidateFrom(state.Cells, Math.Min(oldIndex, newIndex));
            var deckbook = Advance(state.Deckbook, state.Cells);
            await PersistCellsAsync(address, deckbook, state.Cells, cancellationToken)
                .ConfigureAwait(false);
            await AppendAsync(address, "deckbook.cell-moved", new
            {
                name = canonical,
                oldIndex,
                newIndex,
                position,
            }, cancellationToken).ConfigureAwait(false);
            await CheckpointAsync(address, "deckbook-cell-moved", cancellationToken)
                .ConfigureAwait(false);
            return await ReadSnapshotAsync(address, deckbook, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DeckbookCellView> RenameAsync(
        string wraith,
        string haunt,
        string name,
        string target,
        CancellationToken cancellationToken = default)
    {
        var address = await ResolveAsync(wraith, haunt, cancellationToken).ConfigureAwait(false);
        var targetName = CanonicalName.Parse(target).Value;
        var gate = Gate(address);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadStateAsync(address, cancellationToken).ConfigureAwait(false);
            var sourceName = ResolveCellName(state.Deckbook, state.Cells, name);
            EnsureNameAvailable(state.Deckbook, state.Cells, targetName);
            var index = state.Cells.FindIndex(cell => cell.Name == sourceName);
            var renamed = state.Cells[index] with { Name = targetName };
            _store.RenameCell(address.Wraith, address.Haunt, sourceName, targetName);
            state.Cells[index] = renamed;
            var aliases = new Dictionary<string, string>(state.Deckbook.CellAliases, StringComparer.Ordinal)
            {
                [sourceName] = targetName,
            };
            foreach (var alias in aliases.Where(pair => pair.Value == sourceName).Select(pair => pair.Key)
                .ToArray())
            {
                aliases[alias] = targetName;
            }

            var deckbook = Advance(state.Deckbook with { CellAliases = aliases }, state.Cells);
            await PersistCellsAsync(address, deckbook, state.Cells, cancellationToken)
                .ConfigureAwait(false);
            await AppendAsync(address, "deckbook.cell-renamed", new
            {
                previousName = sourceName,
                name = targetName,
            }, cancellationToken).ConfigureAwait(false);
            await CheckpointAsync(address, "deckbook-cell-renamed", cancellationToken)
                .ConfigureAwait(false);
            return await ReadCellViewAsync(address, renamed, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DeckbookSnapshot> SetContextPolicyAsync(
        string wraith,
        string haunt,
        string name,
        CellContextPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var address = await ResolveAsync(wraith, haunt, cancellationToken).ConfigureAwait(false);
        var gate = Gate(address);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadStateAsync(address, cancellationToken).ConfigureAwait(false);
            var canonical = ResolveCellName(state.Deckbook, state.Cells, name);
            var index = state.Cells.FindIndex(cell => cell.Name == canonical);
            state.Cells[index] = state.Cells[index] with { ContextPolicy = policy };
            var deckbook = Advance(state.Deckbook, state.Cells);
            await PersistCellsAsync(address, deckbook, state.Cells, cancellationToken)
                .ConfigureAwait(false);
            await AppendAsync(address, "deckbook.cell-context-policy-changed", new
            {
                name = canonical,
                policy,
            }, cancellationToken).ConfigureAwait(false);
            await CheckpointAsync(address, "deckbook-cell-context-policy-changed", cancellationToken)
                .ConfigureAwait(false);
            return await ReadSnapshotAsync(address, deckbook, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DeckbookSnapshot> DeleteAsync(
        string wraith,
        string haunt,
        string name,
        CancellationToken cancellationToken = default)
    {
        var address = await ResolveAsync(wraith, haunt, cancellationToken).ConfigureAwait(false);
        var gate = Gate(address);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadStateAsync(address, cancellationToken).ConfigureAwait(false);
            var canonical = ResolveCellName(state.Deckbook, state.Cells, name);
            var index = state.Cells.FindIndex(cell => cell.Name == canonical);
            var removed = state.Cells[index];
            state.Cells.RemoveAt(index);
            InvalidateFrom(state.Cells, index);
            _store.DeleteCell(address.Wraith, address.Haunt, canonical);
            var deckbook = Advance(state.Deckbook, state.Cells);
            await PersistCellsAsync(address, deckbook, state.Cells, cancellationToken)
                .ConfigureAwait(false);
            await AppendAsync(address, "deckbook.cell-deleted", new
            {
                removed.Name,
                removed.Position,
                removed.Revision,
                outputHash = removed.LastExecution?.OutputHash,
            }, cancellationToken).ConfigureAwait(false);
            await CheckpointAsync(address, "deckbook-cell-deleted", cancellationToken)
                .ConfigureAwait(false);
            return await ReadSnapshotAsync(address, deckbook, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DeckbookExecutionResult> RunCellAsync(
        string wraith,
        string haunt,
        string name,
        string? runId = null,
        JsonElement input = default,
        CancellationToken cancellationToken = default)
    {
        var address = await ResolveAsync(wraith, haunt, cancellationToken).ConfigureAwait(false);
        var gate = Gate(address);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadStateAsync(address, cancellationToken).ConfigureAwait(false);
            var execution = await ExecuteCellAsync(
                address, state, name, runId, NormalizeInput(input), cancellationToken)
                .ConfigureAwait(false);
            await CheckpointAsync(address, "deckbook-cell-executed", cancellationToken)
                .ConfigureAwait(false);
            return execution.Result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DeckbookRunRemainingResult> RunRemainingAsync(
        string wraith,
        string haunt,
        string from,
        string? runId = null,
        JsonElement input = default,
        CancellationToken cancellationToken = default)
    {
        var address = await ResolveAsync(wraith, haunt, cancellationToken).ConfigureAwait(false);
        var gate = Gate(address);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadStateAsync(address, cancellationToken).ConfigureAwait(false);
            var canonical = ResolveCellName(state.Deckbook, state.Cells, from);
            var start = state.Cells.FindIndex(cell => cell.Name == canonical);
            var names = state.Cells.Skip(start).Where(cell => cell.IsExecutable)
                .Select(cell => cell.Name).ToArray();
            var executions = new List<DeckbookExecutionResult>();
            string? stoppedAt = null;
            foreach (var name in names)
            {
                var executed = await ExecuteCellAsync(
                    address, state, name, runId, NormalizeInput(input), cancellationToken)
                    .ConfigureAwait(false);
                state = executed.State;
                executions.Add(executed.Result);
                if (executed.Result.Output.Status is not CellKernelExecutionStatus.Succeeded)
                {
                    stoppedAt = name;
                    break;
                }
            }

            await AppendAsync(address, "deckbook.run-remaining-completed", new
            {
                from = canonical,
                completed = stoppedAt is null,
                stoppedAt,
                executions = executions.Select(result => new
                {
                    result.Cell.Cell.Name,
                    result.Output.ExecutionId,
                    result.Output.Status,
                    result.Output.Hash,
                }).ToArray(),
            }, cancellationToken).ConfigureAwait(false);
            await CheckpointAsync(address, "deckbook-run-remaining-completed", cancellationToken)
                .ConfigureAwait(false);
            return new DeckbookRunRemainingResult(executions, stoppedAt is null, stoppedAt);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DeckbookContextProjection> CompileContextAsync(
        string wraith,
        string haunt,
        string? activeCell,
        int precedingWindow = 2,
        int maximumCharacters = 32_000,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(precedingWindow);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        var address = await ResolveAsync(wraith, haunt, cancellationToken).ConfigureAwait(false);
        var gate = Gate(address);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadStateAsync(address, cancellationToken).ConfigureAwait(false);
            var canonicalActive = activeCell is null
                ? null
                : ResolveCellName(state.Deckbook, state.Cells, activeCell);
            var selected = new HashSet<string>(
                state.Cells.Where(cell => cell.ContextPolicy is CellContextPolicy.Pinned)
                    .Select(cell => cell.Name),
                StringComparer.Ordinal);
            if (canonicalActive is not null)
            {
                var activeIndex = state.Cells.FindIndex(cell => cell.Name == canonicalActive);
                for (var index = Math.Max(0, activeIndex - precedingWindow); index <= activeIndex; index++)
                {
                    selected.Add(state.Cells[index].Name);
                }
            }

            var indexEntries = state.Cells.Select(cell => new DeckbookContextIndexEntry(
                cell.Name,
                cell.Kind,
                cell.Kernel,
                Truncate(cell.Synopsis, 160),
                cell.IsStale,
                cell.LastExecution?.Status)).ToArray();
            var remaining = Math.Max(
                0,
                maximumCharacters - indexEntries.Sum(entry =>
                    entry.Name.Length + (entry.Synopsis?.Length ?? 0) + 32));
            var included = new List<DeckbookContextCell>();
            foreach (var cell in state.Cells.Where(cell => selected.Contains(cell.Name)))
            {
                var source = await _store.ReadSourceAsync(
                    address.Wraith, address.Haunt, cell, cancellationToken).ConfigureAwait(false);
                var boundedSource = Truncate(source, remaining) ?? string.Empty;
                remaining -= boundedSource.Length;
                var output = await _store.ReadOutputAsync(
                    address.Wraith,
                    address.Haunt,
                    cell.LastExecution?.OutputHash,
                    cancellationToken).ConfigureAwait(false);
                if (output is not null)
                {
                    var outputLength = CanonicalJson.Serialize(output).Length;
                    if (outputLength > remaining)
                    {
                        output = null;
                    }
                    else
                    {
                        remaining -= outputLength;
                    }
                }

                included.Add(new DeckbookContextCell(
                    cell.Name,
                    cell.Kind,
                    cell.Revision,
                    cell.IsStale,
                    boundedSource,
                    cell.LastExecution?.OutputHash,
                    output));
            }

            var unsigned = new
            {
                SchemaVersion = DeckbookContextProjection.CurrentSchemaVersion,
                Agent = address.Wraith.Value,
                Haunt = address.Haunt.Value,
                DeckbookRevision = state.Deckbook.Revision,
                ActiveCell = canonicalActive,
                IncludedCells = included,
                Index = indexEntries,
            };
            return new DeckbookContextProjection(
                unsigned.SchemaVersion,
                unsigned.Agent,
                unsigned.Haunt,
                unsigned.DeckbookRevision,
                unsigned.ActiveCell,
                included,
                indexEntries,
                CanonicalJson.Hash(unsigned));
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        foreach (var gate in _gates.Values)
        {
            gate.Dispose();
        }
    }

    private async Task<ExecutionUpdate> ExecuteCellAsync(
        Address address,
        LoadedState state,
        string name,
        string? runId,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        var canonical = ResolveCellName(state.Deckbook, state.Cells, name);
        var index = state.Cells.FindIndex(cell => cell.Name == canonical);
        var cell = state.Cells[index];
        if (!cell.IsExecutable || string.IsNullOrWhiteSpace(cell.Kernel))
        {
            throw new DeckStateException($"Cell '{canonical}' is not executable.");
        }

        var source = await _store.ReadSourceAsync(
            address.Wraith, address.Haunt, cell, cancellationToken).ConfigureAwait(false);
        var executionId = Guid.CreateVersion7(_clock.UtcNow).ToString("N");
        var startedAt = _clock.UtcNow;
        await AppendAsync(address, "deckbook.cell-execution-started", new
        {
            operationId = executionId,
            cell = cell.Name,
            cell.Revision,
            source,
            sourceHash = CanonicalJson.Hash(source),
            input,
            inputHash = CanonicalJson.Hash(input),
            kernel = cell.Kernel,
        }, cancellationToken, runId, executionId).ConfigureAwait(false);

        var kernel = _kernels.GetKernel(cell.Kernel);
        var values = new List<JsonElement>();
        var standardOutput = new List<string>();
        var standardError = new List<string>();
        var errors = new List<string>();
        var kernelVersion = "unknown";
        long kernelEpoch = 0;
        CellKernelExecutionStatus? status = null;
        await foreach (var kernelEvent in kernel.ExecuteAsync(
            new CellExecutionRequest(
                executionId,
                address.Wraith.Value,
                runId,
                address.Haunt.Value,
                cell.Name,
                source,
                input),
            cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            switch (kernelEvent)
            {
                case CellKernelStarted started:
                    kernelVersion = started.KernelVersion;
                    kernelEpoch = started.KernelEpoch;
                    break;
                case CellKernelValueProduced value:
                    values.Add(value.Value.Clone());
                    break;
                case CellKernelTextProduced { Stream: "stdout" } text:
                    standardOutput.Add(text.Text);
                    break;
                case CellKernelTextProduced text:
                    standardError.Add(text.Text);
                    break;
                case CellKernelErrorProduced error:
                    errors.Add($"{error.ErrorId}: {error.Message}");
                    break;
                case CellKernelCompleted completed:
                    status = completed.Status;
                    break;
            }
        }

        status ??= CellKernelExecutionStatus.OutcomeUnknown;
        var completedAt = _clock.UtcNow;
        var unsignedOutput = new
        {
            SchemaVersion = DeckbookOutputDocument.CurrentSchemaVersion,
            ExecutionId = executionId,
            Status = status.Value,
            Values = values,
            StandardOutput = standardOutput,
            StandardError = standardError,
            Errors = errors,
            CreatedAt = completedAt,
        };
        var output = new DeckbookOutputDocument(
            unsignedOutput.SchemaVersion,
            CanonicalJson.Hash(unsignedOutput),
            unsignedOutput.ExecutionId,
            unsignedOutput.Status,
            values,
            standardOutput,
            standardError,
            errors,
            completedAt);
        await _store.WriteOutputAsync(
            address.Wraith, address.Haunt, output, cancellationToken).ConfigureAwait(false);
        var provenance = new CellExecutionProvenance(
            executionId,
            CanonicalJson.Hash(source),
            CanonicalJson.Hash(input),
            output.Hash,
            status.Value,
            kernel.KernelId,
            kernelVersion,
            kernelEpoch,
            startedAt,
            completedAt);
        state.Cells[index] = cell with
        {
            IsStale = status is not CellKernelExecutionStatus.Succeeded,
            LastExecution = provenance,
        };
        if (status is CellKernelExecutionStatus.Succeeded)
        {
            InvalidateFrom(state.Cells, index + 1);
        }

        state.Deckbook = Advance(state.Deckbook, state.Cells);
        await PersistCellsAsync(
            address, state.Deckbook, state.Cells, cancellationToken).ConfigureAwait(false);
        await AppendAsync(address, "deckbook.cell-execution-completed", new
        {
            operationId = executionId,
            cell = cell.Name,
            status,
            output,
            kernel = kernel.KernelId,
            kernelVersion,
            kernelEpoch,
        }, cancellationToken, runId).ConfigureAwait(false);
        return new ExecutionUpdate(
            state,
            new DeckbookExecutionResult(
                new DeckbookCellView(state.Cells[index], source, output),
                output));
    }

    private async Task<LoadedState> LoadStateAsync(
        Address address,
        CancellationToken cancellationToken)
    {
        var deckbook = await _store.EnsureAsync(
            address.Wraith, address.Haunt, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
        var cells = (await _store.ReadCellsAsync(
            address.Wraith, address.Haunt, deckbook, cancellationToken).ConfigureAwait(false))
            .ToList();
        return new LoadedState(deckbook, cells);
    }

    private async Task<DeckbookSnapshot> ReadSnapshotAsync(
        Address address,
        DeckbookDocument deckbook,
        CancellationToken cancellationToken)
    {
        var cells = await _store.ReadCellsAsync(
            address.Wraith, address.Haunt, deckbook, cancellationToken).ConfigureAwait(false);
        var views = new List<DeckbookCellView>(cells.Count);
        foreach (var cell in cells)
        {
            views.Add(await ReadCellViewAsync(address, cell, cancellationToken).ConfigureAwait(false));
        }

        return new DeckbookSnapshot(deckbook, views);
    }

    private async Task<DeckbookCellView> ReadCellViewAsync(
        Address address,
        DeckbookCellDocument cell,
        CancellationToken cancellationToken) => new(
        cell,
        await _store.ReadSourceAsync(
            address.Wraith, address.Haunt, cell, cancellationToken).ConfigureAwait(false),
        await _store.ReadOutputAsync(
            address.Wraith,
            address.Haunt,
            cell.LastExecution?.OutputHash,
            cancellationToken).ConfigureAwait(false));

    private async Task PersistCellsAsync(
        Address address,
        DeckbookDocument deckbook,
        IReadOnlyList<DeckbookCellDocument> cells,
        CancellationToken cancellationToken)
    {
        foreach (var cell in cells)
        {
            await _store.WriteCellAsync(
                address.Wraith, address.Haunt, cell, cancellationToken).ConfigureAwait(false);
        }

        await _store.WriteDeckbookAsync(
            address.Wraith, address.Haunt, deckbook, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Address> ResolveAsync(
        string wraith,
        string haunt,
        CancellationToken cancellationToken) => new(
        await _deckState.ResolveWraithAsync(
            CanonicalName.Parse(wraith), cancellationToken).ConfigureAwait(false),
        await _deckState.ResolveHauntAsync(
            CanonicalName.Parse(haunt), cancellationToken).ConfigureAwait(false));

    private async Task AppendAsync(
        Address address,
        string kind,
        object payload,
        CancellationToken cancellationToken,
        string? runId = null,
        string? eventId = null) =>
        _ = await _archive.AppendAsync(
            new ArchiveEvent(
                address.Wraith.Value,
                kind,
                CanonicalJson.ToElement(payload),
                address.Haunt.Value,
                runId,
                EventId: eventId,
                Timestamp: _clock.UtcNow),
            cancellationToken).ConfigureAwait(false);

    private Task<string> CheckpointAsync(
        Address address,
        string reason,
        CancellationToken cancellationToken) =>
        _checkpoints.CheckpointAsync(
            reason, address.Wraith, address.Haunt, cancellationToken);

    private SemaphoreSlim Gate(Address address) => _gates.GetOrAdd(
        $"{address.Wraith.Value}/{address.Haunt.Value}",
        static _ => new SemaphoreSlim(1, 1));

    private DeckbookDocument Advance(
        DeckbookDocument deckbook,
        IReadOnlyList<DeckbookCellDocument> cells) => deckbook with
        {
            Revision = checked(deckbook.Revision + 1),
            Cells = Order(cells).Select(cell => cell.Name).ToArray(),
            UpdatedAt = _clock.UtcNow,
        };

    private static List<DeckbookCellDocument> Order(IEnumerable<DeckbookCellDocument> cells) =>
        cells.OrderBy(cell => cell.Position).ThenBy(cell => cell.Name, StringComparer.Ordinal).ToList();

    private static void InvalidateFrom(List<DeckbookCellDocument> cells, int index)
    {
        for (var current = Math.Max(0, index); current < cells.Count; current++)
        {
            if (cells[current].IsExecutable)
            {
                cells[current] = cells[current] with { IsStale = true };
            }
        }
    }

    private static long AllocatePosition(
        List<DeckbookCellDocument> cells,
        string? before,
        string? after)
    {
        EnsureSingleAnchor(before, after);
        cells.Sort(static (left, right) => left.Position.CompareTo(right.Position));
        if (cells.Count == 0)
        {
            return PositionStep;
        }

        var insertionIndex = cells.Count;
        if (before is not null)
        {
            var canonical = CanonicalName.Parse(before).Value;
            insertionIndex = cells.FindIndex(cell => cell.Name == canonical);
            if (insertionIndex < 0)
            {
                throw new DeckStateException($"Cell '{before}' does not exist.");
            }
        }
        else if (after is not null)
        {
            var canonical = CanonicalName.Parse(after).Value;
            var anchor = cells.FindIndex(cell => cell.Name == canonical);
            if (anchor < 0)
            {
                throw new DeckStateException($"Cell '{after}' does not exist.");
            }

            insertionIndex = anchor + 1;
        }

        var lower = insertionIndex == 0 ? 0 : cells[insertionIndex - 1].Position;
        var upper = insertionIndex == cells.Count
            ? checked(lower + PositionStep * 2)
            : cells[insertionIndex].Position;
        if (upper - lower <= 1)
        {
            for (var index = 0; index < cells.Count; index++)
            {
                cells[index] = cells[index] with { Position = checked((index + 1) * PositionStep) };
            }

            lower = insertionIndex == 0 ? 0 : cells[insertionIndex - 1].Position;
            upper = insertionIndex == cells.Count
                ? checked(lower + PositionStep * 2)
                : cells[insertionIndex].Position;
        }

        return lower + ((upper - lower) / 2);
    }

    private static string ResolveCellName(
        DeckbookDocument deckbook,
        IReadOnlyList<DeckbookCellDocument> cells,
        string name)
    {
        var canonical = CanonicalName.Parse(name).Value;
        if (cells.Any(cell => cell.Name == canonical))
        {
            return canonical;
        }

        if (deckbook.CellAliases.TryGetValue(canonical, out var target) &&
            cells.Any(cell => cell.Name == target))
        {
            return target;
        }

        throw new DeckStateException($"Cell '{name}' does not exist.");
    }

    private static void EnsureNameAvailable(
        DeckbookDocument deckbook,
        IReadOnlyList<DeckbookCellDocument> cells,
        string name)
    {
        if (cells.Any(cell => cell.Name == name) || deckbook.CellAliases.ContainsKey(name))
        {
            throw new DeckStateException($"Cell name '{name}' is already used or reserved as an alias.");
        }
    }

    private static string? NormalizeKernel(DeckbookCellKind kind, string? kernel)
    {
        if (kind is not DeckbookCellKind.Code)
        {
            return null;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(kernel);
        return CanonicalName.Parse(kernel).Value;
    }

    private static void ValidateInsert(InsertDeckbookCell request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentNullException.ThrowIfNull(request.Source);
        _ = NormalizeKernel(request.Kind, request.Kernel);
        EnsureSingleAnchor(request.Before, request.After);
    }

    private static void EnsureSingleAnchor(string? before, string? after)
    {
        if (before is not null && after is not null)
        {
            throw new ArgumentException("Specify either before or after, not both.");
        }
    }

    private static JsonElement NormalizeInput(JsonElement input) =>
        input.ValueKind is JsonValueKind.Undefined
            ? CanonicalJson.ToElement<object?>(null)
            : input.Clone();

    private static string? Truncate(string? value, int maximum)
    {
        if (value is null || value.Length <= maximum)
        {
            return value;
        }

        if (maximum <= 1)
        {
            return maximum == 0 ? string.Empty : "…";
        }

        return value[..(maximum - 1)] + "…";
    }

    private sealed record Address(CanonicalName Wraith, CanonicalName Haunt);

    private sealed class LoadedState
    {
        public LoadedState(DeckbookDocument deckbook, List<DeckbookCellDocument> cells)
        {
            Deckbook = deckbook;
            Cells = cells;
        }

        public DeckbookDocument Deckbook { get; set; }

        public List<DeckbookCellDocument> Cells { get; set; }
    }

    private sealed record ExecutionUpdate(LoadedState State, DeckbookExecutionResult Result);
}
