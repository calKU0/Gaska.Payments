using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.Payments.Erp;

/// <summary>
/// The one thread the XL API is used from, holding one session for as long as the service runs.
/// </summary>
/// <remarks>
/// Two rules of the native library shape this, and neither is ours to change.
///
/// It binds itself to whichever thread first calls into it, and every later call from any other
/// thread dies with an <c>SEHException</c> - an access violation inside <c>cdn_api.dll</c> that
/// nothing on our side can catch or recover from. Proved on the test company: logging in, out and
/// in again on one thread works twice over, while the very first login from a second thread
/// faults. The service used to call it from whatever thread-pool thread the cycle happened to
/// resume on, so the first cycle after start-up always worked and every later one always failed,
/// every two hours, until somebody restarted the service.
///
/// The session itself is opened once, at start-up, and closed when the service stops - rather than
/// around every cycle. One sign-in a day instead of a dozen, and nothing that can half-succeed in
/// between. It does mean an ERP licence is held for as long as the service runs; the accounting
/// application has always held one the same way, for a whole working day.
///
/// A session lost underneath us - ERP restarted, the session dropped from the other side - is not
/// the end: a pass that fails outright drops it, and the next one signs in again. That is safe
/// because it happens on this same thread, which is the only thing the library minds.
/// </remarks>
public sealed class XlSessionHost : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly XlOptions _options;
    private readonly ILogger _logger;

    /// <summary>Only ever touched on <see cref="_thread"/>, so it needs no lock.</summary>
    private XlSession? _session;

    public XlSessionHost(IOptions<XlOptions> options, ILogger<XlSessionHost> logger)
    {
        _options = options.Value;
        _logger = logger;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "XL API",
        };

        // Left in the default apartment on purpose. This is the batch side: no sign-in window and
        // no dialogs, and a pool thread - which is what used to run this, successfully, on the
        // first cycle - is the same kind of thread.
        _thread.Start();
    }

    /// <summary>
    /// Runs the work on the XL thread, against the session held there.
    /// </summary>
    /// <remarks>
    /// A failure escaping the work is taken to mean the session is no longer good for anything,
    /// so it is closed and the next call signs in afresh. A single entry that ERP refuses does not
    /// come through here - the posting pass records those and carries on.
    /// </remarks>
    public Task<T> RunAsync<T>(Func<XlSession, T> work, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

        try
        {
            _queue.Add(() =>
            {
                if (completion.Task.IsCompleted) return;

                try
                {
                    completion.SetResult(work(SignedIn()));
                }
                catch (Exception exception)
                {
                    Drop(exception);
                    completion.TrySetException(exception);
                }
            }, cancellationToken);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }

        return completion.Task;
    }

    private void Run()
    {
        // Signed in at start-up, so a wrong operator or password is seen at once rather than at
        // the first cycle. A failure here is not fatal - ERP may simply not be up yet, and the
        // first cycle tries again.
        try
        {
            SignedIn();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception,
                "Could not sign in to XL at start-up - the first cycle will try again.");
        }

        foreach (var work in _queue.GetConsumingEnumerable()) work();

        _session?.Dispose();
        _session = null;
    }

    private XlSession SignedIn() => _session ??= XlSession.Open(_options, _logger);

    private void Drop(Exception cause)
    {
        if (_session is null) return;

        _logger.LogWarning(cause,
            "The XL session failed - closing it, the next cycle will sign in again.");

        try { _session.Dispose(); }
        catch (Exception exception) { _logger.LogWarning(exception, "XLLogout failed on the broken session."); }

        _session = null;
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(30));
    }
}
