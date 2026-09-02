using Gaska.Payments.Application.Settlement;
using Gaska.Payments.Erp;
using Gaska.Payments.Integrations.Archive;
using Gaska.Payments.Integrations.Bank;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.Payments.Application.Bank;

/// <summary>How many statements one pass saved, and for how many days the bank had none.</summary>
public sealed record StatementArchiveSummary(int Saved, int Unavailable);

/// <summary>
/// Keeps an archive of the daily PDF statements on disk, one file per account and day.
/// </summary>
/// <remarks>
/// A statement is an accounting record, so the archive is kept apart from the matching cycle and a
/// failure here must never stop settlement - the caller guards it.
///
/// A file that already exists is never fetched again. That is what makes an hourly service cheap:
/// in the steady state there is exactly one day to fetch, yesterday's. It also means a gap left by
/// an outage fills itself in on the next pass, because the days inside the window are walked
/// through and only the missing ones are asked for.
/// </remarks>
public sealed class StatementArchive(
    BnpStatementClient bankClient,
    ErpReadRepository erp,
    IOptions<BnpConnectionOptions> bnpOptions,
    IOptions<SettlementOptions> settlementOptions,
    IOptions<ArchiveOptions> archiveOptions,
    ILogger<StatementArchive> logger)
{
    private readonly BnpConnectionOptions _bnp = bnpOptions.Value;
    private readonly SettlementOptions _settlement = settlementOptions.Value;
    private readonly ArchiveOptions _archive = archiveOptions.Value;

    /// <summary>
    /// Downloads the statements that are still missing: yesterday's, and any earlier day inside
    /// the backfill window that has no file yet.
    /// </summary>
    public async Task<StatementArchiveSummary> RunAsync(CancellationToken cancellationToken = default)
    {
        if (!_archive.IsConfigured) return new StatementArchiveSummary(0, 0);

        var root = _archive.Root;

        var registers = await erp.GetBankRegistersAsync(_settlement.AllRegisters, cancellationToken);

        // The last day the bank can have closed a statement for is yesterday; today's is still open.
        var newest = DateTime.Today.AddDays(-1);
        var oldest = DateTime.Today.AddDays(-Math.Max(1, _bnp.StatementBackfillDays));

        var saved = 0;
        var unavailable = 0;

        foreach (var register in registers.Where(r => r.Iban.Length > 0))
        {
            var account = new BnpAccount(register.Iban, register.Currency);
            var folder = ArchivePaths.StatementFolder(root, register.Series, register.NormalizedAccount);
            var latestOnDisk = LatestSavedDay(folder, register.Series);

            for (var day = newest; day >= oldest; day = day.AddDays(-1))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var path = ArchivePaths.Statement(root, register.Series, register.NormalizedAccount, day);
                if (File.Exists(path)) continue;

                // A statement we already hold for a later day proves the bank has moved past this
                // one, so its absence is final - a weekend or a holiday. Without this the archive
                // would ask about every past Saturday on every pass, forever.
                if (latestOnDisk is { } latest && day < latest) continue;

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    var statement = await bankClient.GetStatementPdfAsync(account, day, cancellationToken);

                    if (statement is null)
                    {
                        // No statement for that day. Normal for a weekend, and also what an
                        // account outside the GOconnect agreement answers - the client has
                        // already recorded the reason at debug level.
                        unavailable++;
                        continue;
                    }

                    Save(path, statement.Content);
                    saved++;

                    logger.LogInformation(
                        "Saved statement {Series} for {Day:yyyy-MM-dd}, {Size} B in {ElapsedMs} ms: {Path}",
                        register.Series, day, statement.Content.Length,
                        stopwatch.ElapsedMilliseconds, path);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // One account or one day must not cost us the remaining statements.
                    unavailable++;
                    logger.LogWarning(exception,
                        "Could not fetch statement {Series} for {Day:yyyy-MM-dd} after {ElapsedMs} ms.",
                        register.Series, day, stopwatch.ElapsedMilliseconds);
                }
            }
        }

        return new StatementArchiveSummary(saved, unavailable);
    }

    /// <summary>
    /// The latest day this account already has a statement for, or <c>null</c> when the archive
    /// holds none. Read from the file names, so the archive needs no state of its own.
    /// </summary>
    private static DateTime? LatestSavedDay(string folder, string series)
    {
        if (!Directory.Exists(folder)) return null;

        DateTime? latest = null;

        foreach (var file in Directory.EnumerateFiles(folder, $"{series}_*.pdf", SearchOption.AllDirectories))
        {
            var stamp = Path.GetFileNameWithoutExtension(file)[(series.Length + 1)..];

            if (DateTime.TryParseExact(stamp, "yyyy-MM-dd", null,
                    System.Globalization.DateTimeStyles.None, out var day) &&
                (latest is null || day > latest))
            {
                latest = day;
            }
        }

        return latest;
    }

    /// <summary>
    /// Writes the file under a temporary name and moves it into place.
    /// </summary>
    /// <remarks>
    /// The archive's rule is "a file that exists is complete". A process killed mid-write would
    /// otherwise leave a truncated PDF that no later pass would ever replace.
    /// </remarks>
    private static void Save(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temporary = path + ".part";
        File.WriteAllBytes(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }
}
