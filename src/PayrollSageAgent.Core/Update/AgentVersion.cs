using System.Globalization;
using System.Text.RegularExpressions;

namespace PayrollSageAgent.Core.Update;

/// <summary>
/// A version of the agent, in the order the platform keeps (<c>agent_version_key</c>): « 0.10.0 » comes after
/// « 0.9.3 » — numbers, not text. A suffix (« 0.5.0-dev ») is ignored; what is not X.Y.Z has no rank.
/// </summary>
public readonly partial record struct AgentVersion(int Major, int Minor, int Patch) : IComparable<AgentVersion>
{
    public static AgentVersion? Parse(string? text)
    {
        if (text is null) return null;
        var match = Shape().Match(text.Trim());
        if (!match.Success) return null;
        int Part(int i) => int.Parse(match.Groups[i].Value, CultureInfo.InvariantCulture);
        return new AgentVersion(Part(1), Part(2), Part(3));
    }

    /// <summary><paramref name="current"/> is older than <paramref name="other"/> — false when either has no rank.</summary>
    public static bool IsOlder(string? current, string? other) => Parse(current) is { } a && Parse(other) is { } b && a < b;

    /// <summary>The same version, suffixes aside.</summary>
    public static bool Same(string? a, string? b) => Parse(a) is { } x && Parse(b) is { } y && x == y;

    public int CompareTo(AgentVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major) : Minor != other.Minor ? Minor.CompareTo(other.Minor) : Patch.CompareTo(other.Patch);

    public static bool operator <(AgentVersion left, AgentVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(AgentVersion left, AgentVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(AgentVersion left, AgentVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(AgentVersion left, AgentVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    [GeneratedRegex(@"^([0-9]{1,5})\.([0-9]{1,5})\.([0-9]{1,5})(?:[-+].*)?$")]
    private static partial Regex Shape();
}
