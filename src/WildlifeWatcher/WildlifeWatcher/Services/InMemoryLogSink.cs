using Serilog.Core;
using Serilog.Events;

namespace WildlifeWatcher.Services;

public record LogEntry(DateTime Timestamp, string Level, string Message, bool IsWarningOrAbove);

/// <summary>
/// Serilog sink that forwards log events to the UI via a static event.
/// Kept static so ViewModels can subscribe without a DI reference to the sink itself.
/// </summary>
public class InMemoryLogSink : ILogEventSink
{
    public static event Action<LogEntry>? EntryAdded;

    public void Emit(LogEvent logEvent)
    {
        var entry = new LogEntry(
            logEvent.Timestamp.LocalDateTime,
            LevelLabel(logEvent.Level),
            logEvent.RenderMessage(),
            logEvent.Level >= LogEventLevel.Warning);

        EntryAdded?.Invoke(entry);
    }

    private static string LevelLabel(LogEventLevel level) => level switch
    {
        LogEventLevel.Debug       => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning     => "WRN",
        LogEventLevel.Error       => "ERR",
        LogEventLevel.Fatal       => "FTL",
        _                         => "???"
    };
}
