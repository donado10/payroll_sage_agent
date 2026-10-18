using System.Text.Json;
using PayrollSageAgent.Core.Bindings;

namespace PayrollSageAgent.Service.Bindings;

/// <summary>
/// The bindings of this agent — one JSON file each, in <see cref="AgentHome.Bindings"/>. A file is the
/// whole binding: adding one is writing it (<c>add-binding</c>), removing one is deleting it
/// (<c>remove-binding</c>), and the service notices within seconds without a restart. Writes are atomic
/// (a temporary file, then a move), so the service never reads half a binding.
/// </summary>
public sealed partial class BindingStore(AgentHome home)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static bool IsValidName(string name) => BindingRules.IsValidName(name);

    private string PathOf(string name) => Path.Combine(home.Bindings, name + ".json");

    public string Root => home.Root;

    /// <summary>
    /// The folders exist and this process may write in them — asked BEFORE pairing, so a missing right
    /// (a console not run as administrator) costs no pairing code. Throws <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    public void Prepare()
    {
        home.Ensure();
        var probe = Path.Combine(home.Bindings, $".probe-{Environment.ProcessId}");
        File.WriteAllText(probe, "");
        File.Delete(probe);
    }

    public IReadOnlyList<BindingConfig> List()
    {
        if (!Directory.Exists(home.Bindings)) return [];
        var bindings = new List<BindingConfig>();
        foreach (var file in Directory.EnumerateFiles(home.Bindings, "*.json").Order(StringComparer.Ordinal))
        {
            var binding = Read(file);
            if (binding is not null) bindings.Add(binding);
        }
        return bindings;
    }

    public BindingConfig? Get(string name) => IsValidName(name) && File.Exists(PathOf(name)) ? Read(PathOf(name)) : null;

    public void Save(BindingConfig binding)
    {
        if (!IsValidName(binding.Name)) throw new ArgumentException($"Nom de liaison invalide : « {binding.Name} ».");
        home.Ensure();
        var target = PathOf(binding.Name);
        var temporary = target + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(binding, Json));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, target, overwrite: true);
    }

    public bool Delete(string name)
    {
        if (!IsValidName(name) || !File.Exists(PathOf(name))) return false;
        File.Delete(PathOf(name));
        return true;
    }

    /// <summary>The platform no longer knows this binding: kept, marked, never polled again.</summary>
    public void MarkRevoked(string name, string reason)
    {
        var binding = Get(name);
        if (binding is null) return;
        Save(binding with { State = BindingStates.Revoked, StateReason = reason });
    }

    private static BindingConfig? Read(string file)
    {
        try
        {
            return JsonSerializer.Deserialize<BindingConfig>(File.ReadAllText(file), Json);
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            return null;
        }
    }
}
