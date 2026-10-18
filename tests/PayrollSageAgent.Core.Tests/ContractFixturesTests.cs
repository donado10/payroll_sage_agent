using System.Text.Json;
using System.Text.Json.Nodes;
using PayrollSageAgent.Core.Contract;

namespace PayrollSageAgent.Core.Tests;

/// <summary>
/// The contract's golden exchanges (platform/fixtures, written in igf_payroll) through this agent's
/// types: each fixture deserialises into its message and serialises back to the SAME JSON. A field
/// renamed, dropped or mistyped here fails one of these before it fails at a customer's.
/// </summary>
public sealed class ContractFixturesTests
{
    /// <summary>Every message of the contract, by its <c>$defs</c> name — the fixture files' prefix.</summary>
    internal static readonly Dictionary<string, Type> Messages = new(StringComparer.Ordinal)
    {
        ["PairRequest"] = typeof(PairRequest),
        ["PairResponse"] = typeof(PairResponse),
        ["UnpairRequest"] = typeof(UnpairRequest),
        ["OfflineRequest"] = typeof(OfflineRequest),
        ["ClaimRequest"] = typeof(ClaimRequest),
        ["ClaimResponse"] = typeof(ClaimResponse),
        ["CompleteRequest"] = typeof(CompleteRequest),
        ["CompleteResponse"] = typeof(CompleteResponse),
        ["FailRequest"] = typeof(FailRequest),
        ["FailResponse"] = typeof(FailResponse),
        ["UpdateRequest"] = typeof(UpdateRequest),
        ["UpdateResponse"] = typeof(UpdateResponse),
        ["UpdateDownloadRequest"] = typeof(UpdateDownloadRequest),
        ["ErrorResponse"] = typeof(ErrorResponse),
    };

    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(RepoPaths.Platform("fixtures"), "*.json").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(file));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void A_fixture_survives_a_round_trip_through_its_message(string file)
    {
        var name = file.Split('.')[0];
        Assert.True(Messages.TryGetValue(name, out var type), $"{file}: no message « {name} » in this agent — add its type.");

        var original = JsonNode.Parse(File.ReadAllText(RepoPaths.Platform(Path.Combine("fixtures", file))));
        var message = JsonSerializer.Deserialize(original!.ToJsonString(), type!, AgentJson.Options);
        var back = JsonNode.Parse(JsonSerializer.Serialize(message, type!, AgentJson.Options));

        Assert.True(JsonNode.DeepEquals(Normalise(original), Normalise(back)),
            $"{file}: {Normalise(original)?.ToJsonString()} became {Normalise(back)?.ToJsonString()}");
    }

    [Fact]
    public void Every_message_of_the_schema_has_a_type_in_this_agent()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(RepoPaths.Platform(Path.Combine("schema", "agent-protocol.schema.json"))));
        var defs = schema.RootElement.GetProperty("$defs").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal);
        Assert.Equal(defs, Messages.Keys.Order(StringComparer.Ordinal));
    }

    /// <summary>Timestamps compared as instants: « +00:00 » and « Z » are the same moment.</summary>
    private static JsonNode? Normalise(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, Normalise(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Normalise).ToArray()),
        JsonValue v when v.TryGetValue<string>(out var s) && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t) && s.Contains('T', StringComparison.Ordinal)
            => JsonValue.Create(t.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
        _ => node?.DeepClone(),
    };
}
