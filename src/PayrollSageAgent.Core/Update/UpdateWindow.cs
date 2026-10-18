using System.Globalization;
using System.Text.RegularExpressions;

namespace PayrollSageAgent.Core.Update;

/// <summary>
/// The hours in which the agent installs an update by itself — the SERVER's clock, « 03:00-05:00 », possibly
/// across midnight (« 22:00-02:00 »). [HARD] One hour at least: the updater runs once an hour, so a shorter
/// window could fall between two runs and never be met.
/// </summary>
public sealed partial record UpdateWindow(TimeOnly From, TimeOnly To)
{
    public static readonly UpdateWindow Default = new(new TimeOnly(3, 0), new TimeOnly(5, 0));

    public const int MinimumMinutes = 60;

    public const string Rule = "« HH:MM-HH:MM », l’heure de ce serveur, une heure au moins (exemple : « 03:00-05:00 », ou « 22:00-02:00 » à cheval sur minuit)";

    public static (UpdateWindow? Window, string? Error) Parse(string? text)
    {
        var match = text is null ? Match.Empty : Shape().Match(text.Trim());
        if (!match.Success) return (null, $"« update.window » : {Rule}.");
        TimeOnly At(int hour, int minute) => new(int.Parse(match.Groups[hour].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[minute].Value, CultureInfo.InvariantCulture));
        var window = new UpdateWindow(At(1, 2), At(3, 4));
        return window.Length.TotalMinutes < MinimumMinutes ? (null, $"« update.window » : {Rule}.") : (window, null);
    }

    /// <summary>How long the window lasts — across midnight when it ends « before » it starts. Equal ends: none.</summary>
    public TimeSpan Length => To > From ? To - From : To < From ? TimeSpan.FromHours(24) - (From - To) : TimeSpan.Zero;

    public bool Contains(TimeOnly time) => From < To ? time >= From && time < To : time >= From || time < To;

    /// <summary>« entre 03:00 et 05:00 ».</summary>
    public string Sentence => $"entre {From.ToString("HH:mm", CultureInfo.InvariantCulture)} et {To.ToString("HH:mm", CultureInfo.InvariantCulture)}";

    /// <summary>The contract's form, « 03:00-05:00 » (<c>UpdateRequest.window</c>).</summary>
    public override string ToString() => $"{From.ToString("HH:mm", CultureInfo.InvariantCulture)}-{To.ToString("HH:mm", CultureInfo.InvariantCulture)}";

    [GeneratedRegex(@"^([01][0-9]|2[0-3]):([0-5][0-9])-([01][0-9]|2[0-3]):([0-5][0-9])$")]
    private static partial Regex Shape();
}
