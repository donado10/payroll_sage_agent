using System.Text.RegularExpressions;

namespace PayrollSageAgent.Core.Bindings;

/// <summary>What a binding's name, pairing code and platform address must look like — one place, read by
/// the verbs and by the requests the service consumes from <c>pending\</c>.</summary>
public static partial class BindingRules
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex NameShape();

    [GeneratedRegex("^[A-HJKMNP-Z2-9]{4}-[A-HJKMNP-Z2-9]{4}-[A-HJKMNP-Z2-9]{4}$")]
    private static partial Regex CodeShape();

    public const string NameRule = "des minuscules, des chiffres et des tirets, 40 au plus (ex. « prestige-textile »)";
    public const string CodeRule = "le code d’appairage affiché par la plateforme, de la forme XXXX-XXXX-XXXX";
    public const string UrlRule = "une adresse complète en http:// ou https://, ex. http://172.16.2.4:3000/api/agent";

    public static bool IsValidName(string name) => NameShape().IsMatch(name);

    /// <summary>The code as typed — case and spaces forgiven — or null when it is not a code.</summary>
    public static string? Code(string value)
    {
        var code = value.Trim().ToUpperInvariant();
        return CodeShape().IsMatch(code) ? code : null;
    }

    /// <summary>
    /// HTTP or HTTPS, to any host — HTTP for now, in development and in production (the user's decision of
    /// 2026-10-17, platform/protocol.md › « Transport »). When HTTPS comes, HTTP is refused here again.
    /// </summary>
    public static Uri? Address(string value) =>
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : null;
}
