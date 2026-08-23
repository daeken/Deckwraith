using System.Diagnostics.CodeAnalysis;

namespace Deckwraith.Core.Naming;

/// <summary>A portable, case-insensitive identity used directly in paths and public APIs.</summary>
public readonly struct CanonicalName : IEquatable<CanonicalName>, IComparable<CanonicalName>
{
    public const int MaximumLength = 63;

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.Ordinal)
    {
        "con", "prn", "aux", "nul", "clock$",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    private CanonicalName(string value) => Value = value;

    public string Value { get; }

    public static CanonicalName Parse(string value)
    {
        if (!TryParse(value, out var name, out var error))
        {
            throw new ArgumentException(error, nameof(value));
        }

        return name;
    }

    public static bool TryParse(
        string? value,
        out CanonicalName name,
        [NotNullWhen(false)] out string? error)
    {
        name = default;
        if (string.IsNullOrEmpty(value))
        {
            error = "A canonical name cannot be empty.";
            return false;
        }

        var normalized = value.ToLowerInvariant();
        if (normalized.Length > MaximumLength)
        {
            error = $"A canonical name cannot exceed {MaximumLength} characters.";
            return false;
        }

        if (!IsAsciiLetterOrDigit(normalized[0]) || !IsAsciiLetterOrDigit(normalized[^1]))
        {
            error = "A canonical name must begin and end with an ASCII letter or digit.";
            return false;
        }

        foreach (var character in normalized)
        {
            if (!IsAsciiLetterOrDigit(character) && character != '-')
            {
                error = "A canonical name may contain only ASCII letters, digits, and interior hyphens.";
                return false;
            }
        }

        if (ReservedDeviceNames.Contains(normalized))
        {
            error = $"'{value}' is reserved on a supported platform.";
            return false;
        }

        name = new CanonicalName(normalized);
        error = null;
        return true;
    }

    public bool Equals(CanonicalName other) => StringComparer.Ordinal.Equals(Value, other.Value);

    public override bool Equals(object? obj) => obj is CanonicalName other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value ?? string.Empty);

    public int CompareTo(CanonicalName other) => StringComparer.Ordinal.Compare(Value, other.Value);

    public override string ToString() => Value ?? string.Empty;

    public static bool operator ==(CanonicalName left, CanonicalName right) => left.Equals(right);

    public static bool operator !=(CanonicalName left, CanonicalName right) => !left.Equals(right);

    public static bool operator <(CanonicalName left, CanonicalName right) => left.CompareTo(right) < 0;

    public static bool operator <=(CanonicalName left, CanonicalName right) => left.CompareTo(right) <= 0;

    public static bool operator >(CanonicalName left, CanonicalName right) => left.CompareTo(right) > 0;

    public static bool operator >=(CanonicalName left, CanonicalName right) => left.CompareTo(right) >= 0;

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9';
}
