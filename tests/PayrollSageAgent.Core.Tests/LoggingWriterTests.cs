using Microsoft.Extensions.Logging;
using PayrollSageAgent.Core.Logging;

namespace PayrollSageAgent.Core.Tests;

public sealed class LoggingWriterTests
{
    [Fact]
    public void What_a_verb_prints_reaches_the_console_whole_and_the_log_line_by_line()
    {
        var console = new StringWriter();
        var log = new Lines();
        using (var writer = new LoggingWriter(console, log))
        {
            writer.WriteLine("Vérification du compte SQL « agent_a »…");
            writer.Write("Mot de passe du compte SQL : ");
            writer.WriteLine();
            writer.Write("Refusé : un\r\nDétail : deux\n");
            writer.Write("sans fin de ligne");
        }

        Assert.Equal(
            ["Vérification du compte SQL « agent_a »…", "Mot de passe du compte SQL : ", "Refusé : un", "Détail : deux", "sans fin de ligne"],
            log.Received);
        Assert.StartsWith("Vérification du compte SQL « agent_a »…" + Environment.NewLine + "Mot de passe du compte SQL : ", console.ToString(), StringComparison.Ordinal);
    }

    private sealed class Lines : ILogger
    {
        public List<string> Received { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Received.Add(formatter(state, exception));
    }
}
