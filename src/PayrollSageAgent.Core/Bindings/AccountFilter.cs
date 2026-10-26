using System.Text.Json;
using System.Text.RegularExpressions;

namespace PayrollSageAgent.Core.Bindings;

/// <summary>
/// <c>accountFilter</c> — which comptes généraux a binding syncs (contract step 12, platform/protocol.md ›
/// « sync_gl_accounts ») — and <c>partnerFilter</c>, which comptes tiers (step 13, « sync_partner_accounts »):
/// the same grammar for both. Patterns of SQL's <c>LIKE</c>: <c>4%</c> starts with 4, <c>%62</c> ends with 62,
/// <c>%62%</c> contains 62, <c>421100</c> is that one account. Empty: everything, as before.
///
/// [HARD] Letters, digits, <c>%</c> and <c>_</c>, nothing else: <c>[ ] ^ \</c> mean something to SQL
/// Server's <c>LIKE</c> and nothing to Postgres', and the platform deletes what lies OUTSIDE the same
/// patterns — they must read the same on both sides. The platform checks the same shape and refuses
/// anything else.
/// </summary>
public static partial class AccountFilter
{
    public const int MaxPatterns = 50;

    public const string Rule =
        "une liste de motifs du LIKE de SQL — lettres, chiffres, % (n’importe quelle suite) et _ (un caractère), 40 caractères au plus, 50 motifs au plus : [\"4%\", \"50%\", \"%62\", \"421100\"]";

    [GeneratedRegex("^[0-9A-Za-z%_]{1,40}$")]
    private static partial Regex PatternShape();

    public static bool IsValid(string pattern) => PatternShape().IsMatch(pattern);

    /// <summary>The patterns of a JSON value — a list of strings — or why they cannot be used, in French, naming <paramref name="key"/>.</summary>
    public static (IReadOnlyList<string>? Patterns, string? Error) Read(JsonElement value, string key = "accountFilter")
    {
        if (value.ValueKind != JsonValueKind.Array) return (null, $"« {key} » : {Rule}.");
        if (value.GetArrayLength() > MaxPatterns) return (null, $"« {key} » : {MaxPatterns} motifs au plus, pas {value.GetArrayLength()}.");
        var patterns = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            var text = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (text is null || !IsValid(text))
            {
                var shown = item.ValueKind == JsonValueKind.String ? $"« {text} »" : item.GetRawText();
                return (null, $"« {key} » : {shown} n’est pas un motif — {Rule}.");
            }
            patterns.Add(text);
        }
        return (patterns, null);
    }

    /// <summary>« 4%, 50% » — or what an empty filter means: every account.</summary>
    public static string Describe(IReadOnlyList<string> patterns) =>
        patterns.Count == 0 ? "tous (aucun filtre)" : string.Join(", ", patterns);

    public static bool Same(IReadOnlyList<string> a, IReadOnlyList<string> b) => a.SequenceEqual(b, StringComparer.Ordinal);
}
