using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;
using Serilog.Sinks.Email;

namespace Gaska.Payments.Service.Logging;

/// <summary>
/// The subject line of an alert: the label, the severity, the machine and the beginning of the
/// message.
/// </summary>
/// <remarks>
/// The sink renders the subject from the most severe event of the batch, which is the one worth
/// naming. The rendered message is flattened and cut short because a subject is a mail header:
/// a newline in it would either be folded into the next header or dropped, and the whole line is
/// truncated by the reader's inbox anyway.
/// </remarks>
internal sealed class AlertSubjectFormatter(string label) : ITextFormatter
{
    private const int MaxLength = 150;

    // ":lj" renders string properties as they read rather than as JSON - the same form the log
    // file and the console use. Without it a subject comes out as: failed for run "FORPL".
    private readonly MessageTemplateTextFormatter _message = new("{Message:lj}", formatProvider: null);

    public void Format(LogEvent logEvent, TextWriter output)
    {
        var head = $"{label} [{Level(logEvent)}] {Environment.MachineName}: ";

        using var message = new StringWriter();
        _message.Format(logEvent, message);

        var line = string.Join(' ', message.ToString()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        var room = MaxLength - head.Length;
        output.Write(head);
        output.Write(line.Length <= room ? line : string.Concat(line.AsSpan(0, Math.Max(0, room - 1)), "…"));
    }

    internal static string Level(LogEvent logEvent) => logEvent.Level switch
    {
        LogEventLevel.Fatal => "FATAL",
        LogEventLevel.Error => "ERROR",
        LogEventLevel.Warning => "WARNING",
        _ => logEvent.Level.ToString().ToUpperInvariant(),
    };
}

/// <summary>
/// The body of an alert: a summary of the batch, then every event in the shape the log file uses.
/// </summary>
/// <remarks>
/// Implemented as an <see cref="IBatchTextFormatter"/> rather than a plain one so that the message
/// opens with what the reader needs first - how many events there were, over what stretch of time
/// and how severe - instead of leaving them to count stack traces. The events themselves are
/// written exactly as the log file writes them, so a line found in an alert can be searched for in
/// the file or in Seq without translating it.
/// </remarks>
internal sealed class AlertBodyFormatter : IBatchTextFormatter
{
    private const string Template =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] ({SourceContext}) {Message:lj}{NewLine}{Exception}";

    private readonly MessageTemplateTextFormatter _event = new(Template, formatProvider: null);

    public void FormatBatch(IEnumerable<LogEvent> logEvents, TextWriter output)
    {
        var events = logEvents as IReadOnlyList<LogEvent> ?? [.. logEvents];
        if (events.Count == 0) return;

        var counts = events
            .GroupBy(e => e.Level)
            .OrderByDescending(g => g.Key)
            .Select(g => $"{g.Count()} × {AlertSubjectFormatter.Level(g.First())}");

        output.WriteLine($"Gaska.Payments.Service on {Environment.MachineName}");
        output.WriteLine(
            events.Count == 1
                ? $"One event at {events[0].Timestamp:yyyy-MM-dd HH:mm:ss}"
                : $"{events.Count} events between {events.Min(e => e.Timestamp):yyyy-MM-dd HH:mm:ss} "
                  + $"and {events.Max(e => e.Timestamp):yyyy-MM-dd HH:mm:ss}");
        output.WriteLine(string.Join(", ", counts));
        output.WriteLine();

        foreach (var logEvent in events)
        {
            output.WriteLine(new string('-', 78));
            Format(logEvent, output);
        }
    }

    public void Format(LogEvent logEvent, TextWriter output) => _event.Format(logEvent, output);
}
