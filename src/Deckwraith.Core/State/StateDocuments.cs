using Deckwraith.Core.Naming;

namespace Deckwraith.Core.State;

public sealed record DeckManifest(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    IReadOnlyDictionary<string, string> WraithAliases,
    IReadOnlyDictionary<string, string> HauntAliases)
{
    public const int CurrentSchemaVersion = 1;

    public static DeckManifest Create(DateTimeOffset now) => new(
        CurrentSchemaVersion,
        now,
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal));
}

public sealed record DeckPolicy(
    int SchemaVersion,
    bool SensitiveRepository,
    bool AllowAutomaticRemotes)
{
    public const int CurrentSchemaVersion = 1;

    public static DeckPolicy CreateDefault() => new(CurrentSchemaVersion, true, false);
}

public sealed record WraithDocument(
    int SchemaVersion,
    string Name,
    string? DisplayLabel,
    IReadOnlyList<string> Aliases,
    DateTimeOffset CreatedAt)
{
    public const int CurrentSchemaVersion = 1;

    public static WraithDocument Create(CanonicalName name, DateTimeOffset now) =>
        new(CurrentSchemaVersion, name.Value, null, [], now);
}

public sealed record IdentityDocument
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Personality { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> Calibration { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["register"] = string.Empty,
        };

    public IReadOnlyList<string> Pronouns { get; init; } = [];

    public string SelfDescription { get; init; } = string.Empty;

    public IReadOnlyList<string> KnownTendencies { get; init; } = [];

    public IReadOnlyList<string> OpenQuestions { get; init; } = [];

    public DateTimeOffset UpdatedAt { get; init; }

    public static IdentityDocument CreateSparse(CanonicalName name, DateTimeOffset now) =>
        new()
        {
            SchemaVersion = CurrentSchemaVersion,
            Name = name.Value,
            UpdatedAt = now,
        };
}

public sealed record HauntDocument(
    int SchemaVersion,
    string Name,
    string? DisplayLabel,
    IReadOnlyList<string> Aliases,
    DateTimeOffset CreatedAt)
{
    public const int CurrentSchemaVersion = 1;

    public static HauntDocument Create(CanonicalName name, DateTimeOffset now) =>
        new(CurrentSchemaVersion, name.Value, null, [], now);
}

public enum RenameSubject
{
    Wraith,
    Haunt,
}

public enum RenameStatus
{
    Prepared,
    Applied,
    Completed,
}

public sealed record RenameIntent(
    int SchemaVersion,
    string OperationId,
    RenameSubject Subject,
    string Source,
    string Target,
    RenameStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record ArtifactReference(
    string Hash,
    long Length,
    string RelativePath,
    string? MediaType);
