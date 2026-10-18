using System.Text;
using Microsoft.Extensions.Logging;

namespace PayrollSageAgent.Core.Logging;

/// <summary>
/// The console of a verb, copied into the agent's log: everything <c>add-binding</c>, <c>check-sql</c> or
/// <c>remove-binding</c> PRINTS also goes to <c>logs\agent-AAAA-MM-JJ.log</c>, one entry per line — so the
/// file IT sends us holds what they saw, refusals and SQL Server's detail included.
///
/// It is safe because of a rule the verbs already keep: nothing they print carries a secret — the
/// password prompt echoes nothing, and the pairing code is never printed back. A line is logged when it
/// ENDS; text written without a line end (the password prompt) waits for the next one. Line ends reach
/// the console through ITS <c>WriteLine</c>, so a Windows console gets its own.
/// </summary>
public sealed class LoggingWriter(TextWriter console, ILogger log) : TextWriter
{
    private readonly StringBuilder _line = new();

    public override Encoding Encoding => console.Encoding;

    public override void Write(char value)
    {
        console.Write(value);
        Collect(value);
    }

    public override void Write(string? value)
    {
        if (value is null) return;
        console.Write(value);
        foreach (var c in value) Collect(c);
    }

    public override void WriteLine(string? value)
    {
        console.WriteLine(value);
        if (value is not null) foreach (var c in value) Collect(c);
        Emit();
    }

    public override void WriteLine() => WriteLine((string?)null);

    public override void Flush() => console.Flush();

    protected override void Dispose(bool disposing)
    {
        if (disposing && _line.Length > 0) Emit();
        base.Dispose(disposing);
    }

    private void Collect(char c)
    {
        if (c == '\n') Emit();
        else if (c != '\r') _line.Append(c);
    }

    private void Emit()
    {
        var text = _line.ToString();
        _line.Clear();
        if (text.Length > 0) log.LogInformation("{Line}", text);
    }
}
