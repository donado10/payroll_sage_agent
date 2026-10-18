using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PayrollSageAgent.Core.Logging;

/// <summary>
/// The agent's log as files an IT person can zip and send: <c>agent-AAAA-MM-JJ.log</c> in the agent's
/// <c>logs</c> folder, one per day, the last <see cref="KeepDays"/> days kept.
///
/// One line per event — local time with its offset, level, category, message — and an exception's type,
/// message and stack on the lines under it. What goes in is what the <c>[LoggerMessage]</c> lines say, so
/// the rules of <c>.claude/rules/secrets-and-logging.md</c> hold here unchanged: no token, no password,
/// no account. [HARD] A logger never stops the service: a file it cannot open or write is skipped, and
/// the next line tries again.
/// </summary>
public sealed class FileLoggerProvider(string folder, TimeProvider clock) : ILoggerProvider
{
    public const int KeepDays = 30;
    private const string Prefix = "agent-";
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Lock _gate = new();
    private StreamWriter? _writer;
    private DateOnly _day;

    public string Folder { get; } = folder;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, ShortName(categoryName));

    /// <summary>The file a line written at <paramref name="day"/> goes to.</summary>
    public string PathFor(DateOnly day) => Path.Combine(Folder, $"{Prefix}{day:yyyy-MM-dd}.log");

    internal void Write(LogLevel level, string category, string message, Exception? error)
    {
        var now = clock.GetLocalNow();
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture))
            .Append(" [").Append(Level(level)).Append("] ")
            .Append(category).Append(" : ").Append(message);
        if (error is not null) line.AppendLine().Append("    ").Append(error.ToString().Replace("\n", "\n    ", StringComparison.Ordinal));

        lock (_gate)
        {
            try
            {
                var writer = WriterFor(DateOnly.FromDateTime(now.DateTime));
                writer.WriteLine(line.ToString());
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // The log is not worth the service: drop this line, reopen on the next one.
                _writer?.Dispose();
                _writer = null;
            }
        }
    }

    private StreamWriter WriterFor(DateOnly day)
    {
        if (_writer is not null && day == _day) return _writer;
        _writer?.Dispose();
        _writer = null;
        Directory.CreateDirectory(Folder);
        var stream = new FileStream(PathFor(day), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, Utf8) { AutoFlush = true };
        _day = day;
        Prune(day);
        return _writer;
    }

    /// <summary>Removes the files older than <see cref="KeepDays"/> — by the date in their NAME, never another file.</summary>
    private void Prune(DateOnly today)
    {
        var oldest = today.AddDays(-(KeepDays - 1));
        foreach (var file in Directory.EnumerateFiles(Folder, $"{Prefix}*.log"))
        {
            var stem = Path.GetFileNameWithoutExtension(file)[Prefix.Length..];
            if (DateOnly.TryParseExact(stem, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day < oldest)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
                {
                    // Tried again tomorrow.
                }
            }
        }
    }

    /// <summary><c>PayrollSageAgent.Core.Jobs.BindingLoop</c> → <c>BindingLoop</c>: the class says enough.</summary>
    private static string ShortName(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot < 0 ? category : category[(dot + 1)..];
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "---",
    };

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
