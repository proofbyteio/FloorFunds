using System.Globalization;

namespace Catalog.Contracts;

/// <summary>
/// A target version is either ABSENT (no stored switch row) or an integer &gt;= 1.
/// Zero is not a stored version and is not a synonym for ABSENT.
/// </summary>
public readonly struct VersionToken : IEquatable<VersionToken>
{
    private readonly long _value;

    private VersionToken(long value) => _value = value;

    public static VersionToken Absent => default;

    public bool IsAbsent => _value == 0;

    /// <summary>Null when ABSENT. Never returns 0.</summary>
    public long? Number => _value == 0 ? null : _value;

    public static VersionToken AtLeastOne(long value)
    {
        if (value < 1)
            throw new ArgumentOutOfRangeException(nameof(value), "A stored version is 1 or greater.");
        return new VersionToken(value);
    }

    public static bool TryParseJson(System.Text.Json.JsonElement element, out VersionToken token, out string? error)
    {
        token = default;
        error = null;
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.String when element.GetString() == "ABSENT":
                token = Absent;
                return true;
            case System.Text.Json.JsonValueKind.Number when element.TryGetInt64(out var number) && number >= 1:
                token = new VersionToken(number);
                return true;
            default:
                error = "version_domain";
                return false;
        }
    }

    public override string ToString() => IsAbsent ? "ABSENT" : _value.ToString(CultureInfo.InvariantCulture);

    public bool Equals(VersionToken other) => _value == other._value;

    public override bool Equals(object? obj) => obj is VersionToken other && Equals(other);

    public override int GetHashCode() => _value.GetHashCode();

    public static bool operator ==(VersionToken left, VersionToken right) => left.Equals(right);

    public static bool operator !=(VersionToken left, VersionToken right) => !left.Equals(right);
}
