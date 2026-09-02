using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Gaska.Payments.Domain.Diagnostics;

/// <summary>
/// A stopwatch that writes one line to the log when it is disposed.
/// </summary>
/// <remarks>
/// It lives here, in the project with no dependencies of its own, because it is the only assembly
/// both the service and the accounting application can see - and both of them need to say how long
/// something took. It is not matching logic and does not pretend to be; it is a diagnostic helper
/// that had nowhere better to live short of a project of its own.
///
/// The elapsed time is written as its own property, so it can be filtered on rather than only read.
/// A failed operation is still timed: how long something took before it broke is usually the more
/// interesting number.
/// </remarks>
public sealed class TimedOperation : IDisposable
{
    private readonly ILogger _logger;
    private readonly string _name;
    private readonly LogLevel _level;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    private string _outcome = string.Empty;
    private bool _finished;

    private TimedOperation(ILogger logger, string name, LogLevel level)
    {
        _logger = logger;
        _name = name;
        _level = level;

        logger.Log(level, "{Operation} started.", name);
    }

    /// <summary>Starts timing a step worth seeing in the ordinary log.</summary>
    public static TimedOperation Start(ILogger logger, string name) =>
        new(logger, name, LogLevel.Information);

    /// <summary>Starts timing a step only worth seeing when somebody is looking into a problem.</summary>
    public static TimedOperation Trace(ILogger logger, string name) =>
        new(logger, name, LogLevel.Debug);

    /// <summary>
    /// What came of the operation - appended to the closing line. Called more than once, the last
    /// answer wins.
    /// </summary>
    public TimedOperation Result(string outcome)
    {
        _outcome = outcome;
        return this;
    }

    /// <summary>Milliseconds elapsed so far, for a caller that wants to log the number itself.</summary>
    public long ElapsedMs => _stopwatch.ElapsedMilliseconds;

    public void Dispose()
    {
        if (_finished) return;
        _finished = true;

        _stopwatch.Stop();

        if (_outcome.Length == 0)
        {
            _logger.Log(_level, "{Operation} finished in {ElapsedMs} ms.", _name, _stopwatch.ElapsedMilliseconds);
        }
        else
        {
            _logger.Log(_level, "{Operation} finished in {ElapsedMs} ms: {Outcome}",
                _name, _stopwatch.ElapsedMilliseconds, _outcome);
        }
    }
}
