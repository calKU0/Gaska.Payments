using System.Collections.Concurrent;
using System.Threading;
using Gaska.Payments.Erp;
using Microsoft.Extensions.Logging;

namespace Gaska.Payments.Desktop.Data;

/// <summary>
/// A single thread that holds the XL session from sign-in until the application closes, and runs
/// every request on it in turn.
/// </summary>
/// <remarks>
/// The native XL API is thread-bound: a session opened on one thread must not be used from
/// another. Each settlement used to open a session of its own, but ever since we sign in through
/// the Comarch ERP XL window that would mean asking for the password on every click.
/// </remarks>
public sealed class XlWorker : IDisposable
{
    private readonly BlockingCollection<Action<XlSession>> _queue = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;

    public XlWorker(XlOptions options, ILogger logger)
    {
        _thread = new Thread(() => Run(options, logger))
        {
            IsBackground = true,
            Name = "XL API",
        };

        // The ERP sign-in window is a native dialog - safer to give it an STA thread.
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Completes once the operator has signed in through the ERP window - or faults if they gave up.</summary>
    public Task Ready => _ready.Task;

    /// <summary>Runs a request on the session's thread and returns its result.</summary>
    public Task<T> RunAsync<T>(Func<XlSession, T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        _queue.Add(session =>
        {
            try
            {
                completion.SetResult(work(session));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });

        return completion.Task;
    }

    private void Run(XlOptions options, ILogger logger)
    {
        XlSession session;

        try
        {
            session = XlSession.OpenInteractive(options, logger);
            _ready.SetResult(true);
        }
        catch (Exception ex)
        {
            _ready.SetException(ex);
            return;
        }

        try
        {
            foreach (var work in _queue.GetConsumingEnumerable()) work(session);
        }
        finally
        {
            session.Dispose();
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}
