using System.Globalization;
using Microsoft.Extensions.Logging;
using cdn_api;

namespace Gaska.Payments.Erp;

/// <summary>
/// An XL API session. It signs in when constructed and signs out when disposed.
/// </summary>
/// <remarks>
/// The native <c>cdn_api.dll</c> does not stand alone - it imports several dozen libraries of the
/// ERP XL client, so it only loads from that client's installation directory. On the target
/// machines that directory is on <c>Path</c>, and that is enough: the system finds both
/// <c>cdn_api.dll</c> and everything it depends on through it.
/// </remarks>
public sealed class XlSession : IDisposable
{
    private readonly ILogger _logger;
    private bool _disposed;

    public int Id { get; }

    public int Version { get; }

    private XlSession(int id, int version, ILogger logger)
    {
        Id = id;
        Version = version;
        _logger = logger;
    }

    /// <summary>
    /// Signing in with credentials from configuration - batch mode, no windows. For the automat.
    /// </summary>
    public static XlSession Open(XlOptions options, ILogger logger) =>
        Login(options, logger, batch: true);

    /// <summary>
    /// Signing in through the Comarch ERP XL window. We supply neither operator nor password, so
    /// the API opens its own sign-in window - we never touch the password and ERP itself guards
    /// the permissions.
    /// </summary>
    /// <remarks>
    /// The database is passed on when configuration names one: the sign-in window then has it
    /// preselected and nobody confuses production with the test company. An empty
    /// <c>Xl:Database</c> leaves the choice to the operator.
    /// </remarks>
    public static XlSession OpenInteractive(XlOptions options, ILogger logger) =>
        Login(options, logger, batch: false);

    private static XlSession Login(XlOptions options, ILogger logger, bool batch)
    {
        var login = new XLLoginInfo_20251
        {
            Wersja = options.ApiVersion,
            TrybWsadowy = batch ? 1 : 0,
            ProgramID = "Gaska.Payments.Service",
        };

        // An empty database is not passed on: the API recognises incomplete credentials by the
        // field being empty, and only then opens its own sign-in window.
        if (options.Database.Trim().Length > 0) login.Baza = options.Database.Trim();

        if (batch)
        {
            login.OpeIdent = options.Operator;
            login.OpeHaslo = options.Password;
        }

        var sessionId = 0;

        // Signing in takes seconds rather than milliseconds and is the first thing to suspect when
        // a cycle is slow, so it is timed like any other step.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = cdn_api.cdn_api.XLLogin(login, ref sessionId);
        stopwatch.Stop();

        if (result != 0 || sessionId == 0)
        {
            throw new InvalidOperationException(
                batch
                    ? $"XLLogin zwrócił {result} dla bazy '{options.Database}' i operatora '{options.Operator}'."
                    : $"Logowanie do Comarch ERP XL nie powiodło się (XLLogin zwrócił {result}).");
        }

        logger.LogInformation(
            "Signed in to XL as {Operator} on database {Database}, session {Session}, took {ElapsedMs} ms.",
            batch ? options.Operator : "(operator chosen in the ERP window)",
            options.Database.Trim().Length > 0 ? options.Database : "(chosen in the ERP window)",
            sessionId, stopwatch.ElapsedMilliseconds);

        return new XlSession(sessionId, options.ApiVersion, logger);
    }

    /// <summary>An amount in the format the API expects - decimal point, no thousands separator.</summary>
    public static string Amount(decimal value) => value.ToString("F2", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var result = cdn_api.cdn_api.XLLogout(Id);
        if (result != 0) _logger.LogWarning("XLLogout returned {Result}.", result);
        else _logger.LogDebug("Signed out of XL, session {Session}.", Id);
    }
}
