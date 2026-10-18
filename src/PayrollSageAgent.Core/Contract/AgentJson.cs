using System.Text.Encodings.Web;
using System.Text.Json;

namespace PayrollSageAgent.Core.Contract;

/// <summary>
/// The one set of JSON options for the contract: property names come from the attributes, an unknown
/// key is ignored on the way in (tolerant reader), and accents are written as themselves.
/// </summary>
public static class AgentJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
