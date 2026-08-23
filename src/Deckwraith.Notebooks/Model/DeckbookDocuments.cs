using System.Text.Json;
using Deckwraith.Kernels.Abstractions;

namespace Deckwraith.Notebooks.Model;

public enum DeckbookCellKind
{
    Markdown,
    Code,
    Prompt,
    Query,
    Artifact,
    Value,
}

public enum CellContextPolicy
{
    Never,
    WhenRelevant,
    Pinned,
}

public sealed record CellExecutionProvenance(
    string ExecutionId,
    string SourceHash,
    string InputHash,
    string OutputHash,
    CellKernelExecutionStatus Status,
    string Kernel,
    string KernelVersion,
    long KernelEpoch,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

public sealed record DeckbookCellDocument(
    int SchemaVersion,
    string Name,
    long Position,
    DeckbookCellKind Kind,
    string? Kernel,
    string SourceFile,
    CellContextPolicy ContextPolicy,
    long Revision,
    bool IsStale,
    string? Synopsis,
    CellExecutionProvenance? LastExecution)
{
    public const int CurrentSchemaVersion = 1;

    public bool IsExecutable => Kind is DeckbookCellKind.Code;
}

public sealed record DeckbookDocument(
    int SchemaVersion,
    string Agent,
    string Haunt,
    long Revision,
    IReadOnlyDictionary<string, string> CellAliases,
    IReadOnlyList<string> Cells,
    DateTimeOffset UpdatedAt)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record DeckbookOutputDocument(
    int SchemaVersion,
    string Hash,
    string ExecutionId,
    CellKernelExecutionStatus Status,
    IReadOnlyList<JsonElement> Values,
    IReadOnlyList<string> StandardOutput,
    IReadOnlyList<string> StandardError,
    IReadOnlyList<string> Errors,
    DateTimeOffset CreatedAt)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record DeckbookCellView(
    DeckbookCellDocument Cell,
    string Source,
    DeckbookOutputDocument? Output);

public sealed record DeckbookSnapshot(
    DeckbookDocument Deckbook,
    IReadOnlyList<DeckbookCellView> Cells);

public sealed record DeckbookExecutionResult(
    DeckbookCellView Cell,
    DeckbookOutputDocument Output);

public sealed record DeckbookRunRemainingResult(
    IReadOnlyList<DeckbookExecutionResult> Executions,
    bool Completed,
    string? StoppedAt);

public sealed record DeckbookContextCell(
    string Name,
    DeckbookCellKind Kind,
    long Revision,
    bool IsStale,
    string Source,
    string? OutputHash,
    DeckbookOutputDocument? Output);

public sealed record DeckbookContextIndexEntry(
    string Name,
    DeckbookCellKind Kind,
    string? Kernel,
    string? Synopsis,
    bool IsStale,
    CellKernelExecutionStatus? Status);

public sealed record DeckbookContextProjection(
    int SchemaVersion,
    string Agent,
    string Haunt,
    long DeckbookRevision,
    string? ActiveCell,
    IReadOnlyList<DeckbookContextCell> IncludedCells,
    IReadOnlyList<DeckbookContextIndexEntry> Index,
    string ProjectionHash)
{
    public const int CurrentSchemaVersion = 1;
}
