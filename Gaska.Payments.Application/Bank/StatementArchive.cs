using Gaska.Payments.Application.Settlement;
using Gaska.Payments.Erp;
using Gaska.Payments.Integrations.Archive;
using Gaska.Payments.Integrations.Bank;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.Payments.Application.Bank;

/// <summary>
/// How many statements one pass saved, how many it refreshed, and for how many days the bank had
/// none.
/// </summary>
public sealed record StatementArchiveSummary(int Saved, int Refreshed, int Unavailable);

/// <summary>
/// Keeps an archive of the daily PDF statements on disk, one file per account and day.
/// </summary>
/// <remarks>
/// A statement is an accounting record, so the archive is kept apart from the matching cycle and a
/// failure here must never stop settlement - the caller guards it.
///
/// A file that already exists is never fetched again, with one exception: today's. Today's
/// statement is still open - transfers keep arriving until the bank closes the day - so it is
/// fetched on every pass and the file on disk replaced. That is also what puts the button in the
/// accounting application beside a transfer booked an hour ago.
///
/// Everything older is fetched once. That is what makes an hourly service cheap: in the steady
/// state there is yesterday's to fetch and today's to refresh. A gap left by an outage fills
/// itself in on the next pass, because the days inside the window are walked through and only the
/// missing ones are asked for.
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
        if (!_archive.IsConfigured) return new StatementArchiveSummary(0, 0, 0);

        var root = _archive.Root;

        var registers = await erp.GetBankRegistersAsync(_settlement.AllRegisters, cancellationToken);

        // Yesterday is the newest day worth asking about. BNP writes no PDF for a day still open -
        // asked for today's it answers "E502: Wyciąg za dany okres jest niedostępny", whether or
        // not there was any turnover - so asking would be one wasted request per account per pass,
        // all day, for a file that cannot exist yet.
        var newest = DateTime.Today.AddDays(-1);
        var oldest = DateTime.Today.AddDays(-Math.Max(1, _bnp.StatementBackfillDays));

        var saved = 0;
        var refreshed = 0;
        var unavailable = 0;

        foreach (var register in registers.Where(r => r.Iban.Length > 0))
        {
            var account = new BnpAccount(register.Iban, register.Currency);
            var folder = ArchivePaths.StatementFolder(root, register.Series, register.NormalizedAccount);
            var latestOnDisk = LatestSavedDay(folder, register.Series);
            var refused = ReadRefusals(folder);
            var learned = false;

            for (var day = newest; day >= oldest; day = day.AddDays(-1))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var path = ArchivePaths.Statement(root, register.Series, register.NormalizedAccount, day);

                // A day still open at the bank is fetched again on every pass and the file
                // replaced; anything older that is already on disk is finished with.
                if (File.Exists(path) && !StillOpen(day)) continue;

                // A day the bank has already answered "no statement" for. It writes one only for a
                // day with turnover, so on a quiet day there will never be a file - and without
                // remembering that, every past Saturday was asked about again every hour.
                if (refused.GetValueOrDefault(day) >= AttemptsBeforeWritingOff(day)) continue;

                // A statement we already hold for a later day proves the bank has moved past this
                // one, so its absence is final.
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

                        // Written off for good, but only once the day is closed at the bank. A day
                        // still open has no statement yet by definition, and writing that off
                        // would mean never fetching it at all.
                        refused[day] = refused.GetValueOrDefault(day) + 1;
                        learned = true;

                        continue;
                    }

                    var existed = File.Exists(path);
                    Save(path, statement.Content);

                    if (existed) refreshed++; else saved++;

                    // A refresh happens every hour all day long and says nothing new, so it does
                    // not belong at information level - the first save of a day does.
                    logger.Log(
                        existed ? LogLevel.Debug : LogLevel.Information,
                        "{Verb} statement {Series} for {Day:yyyy-MM-dd}, {Size} B in {ElapsedMs} ms: {Path}",
                        existed ? "Refreshed" : "Saved",
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

            if (learned) RememberRefusals(folder, register.Series, refused);
        }

        return new StatementArchiveSummary(saved, refreshed, unavailable);
    }

    /// <summary>
    /// Whether the bank may still add to that day's statement, so it is worth fetching again.
    /// </summary>
    /// <remarks>
    /// Yesterday, and only yesterday. That is the day whose statement has just appeared, and a
    /// pass that ran shortly after midnight can have taken it before the bank finished writing it;
    /// fetching it again through the day replaces what was incomplete. Today is not asked about at
    /// all - the bank has nothing to give for a day that has not closed.
    ///
    /// One day is the whole cost: one request per account per pass, against leaving a half-written
    /// statement in the archive for good.
    /// </remarks>
    private static bool StillOpen(DateTime day) => day == DateTime.Today.AddDays(-1);

    /// <summary>The file recording which days this account has no statement for.</summary>
    private static string RefusalsPath(string folder) => Path.Combine(folder, "_dni-bez-wyciagu.txt");

    /// <summary>
    /// How many times the bank may answer "no statement" for a day before it is written off.
    /// </summary>
    /// <remarks>
    /// A closed day is written off on the first refusal: the bank writes a statement only for a
    /// day with turnover, so on a quiet Saturday there will never be a file, and asking again is
    /// asking about the past.
    ///
    /// Yesterday gets three goes. Its statement is produced by the bank's overnight run, and a
    /// pass at half past midnight can be ahead of it - but only just ahead, and it certainly is
    /// not still coming ninety minutes later. Before this, yesterday counted as open all day and
    /// was asked about on every pass: on Monday the 7th that was 06.09, a Sunday with no turnover,
    /// asked of fourteen accounts sixteen times over.
    /// </remarks>
    private static int AttemptsBeforeWritingOff(DateTime day) => StillOpen(day) ? 3 : 1;

    /// <summary>
    /// Days the bank has answered "no statement" for, and how many times.
    /// </summary>
    /// <remarks>
    /// One file per account rather than a marker per day, so the archive stays a folder of
    /// statements and nothing else. It is the archive's own state and needs no database: the worst
    /// a lost file can cost is one more round of asking.
    ///
    /// A line is <c>yyyy-MM-dd</c> or <c>yyyy-MM-dd;attempts</c>. The bare form is what the first
    /// version of this file wrote and means "written off", so old archives keep working.
    /// </remarks>
    private static Dictionary<DateTime, int> ReadRefusals(string folder)
    {
        var days = new Dictionary<DateTime, int>();
        var path = RefusalsPath(folder);

        try
        {
            if (!File.Exists(path)) return days;

            foreach (var line in File.ReadAllLines(path))
            {
                var parts = line.Split(';', StringSplitOptions.TrimEntries);

                if (!DateTime.TryParseExact(parts[0], "yyyy-MM-dd", null,
                        System.Globalization.DateTimeStyles.None, out var day))
                {
                    continue;
                }

                days[day] = parts.Length > 1 && int.TryParse(parts[1], out var attempts)
                    ? attempts
                    : int.MaxValue;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An archive on a share that is not answering. Asking the bank again is the harmless
            // outcome, so it is not worth failing the pass for.
        }

        return days;
    }

    /// <summary>Writes back what the bank refused, with the day the archive no longer asks about.</summary>
    private void RememberRefusals(string folder, string series, Dictionary<DateTime, int> refused)
    {
        try
        {
            Directory.CreateDirectory(folder);

            File.WriteAllLines(
                RefusalsPath(folder),
                refused.OrderByDescending(e => e.Key)
                    .Select(e => $"{e.Key:yyyy-MM-dd};{Math.Min(e.Value, AttemptsBeforeWritingOff(e.Key))}"));

            var settled = refused
                .Where(e => e.Value >= AttemptsBeforeWritingOff(e.Key))
                .Select(e => e.Key.ToString("yyyy-MM-dd"))
                .ToList();

            if (settled.Count > 0)
            {
                logger.LogDebug(
                    "{Series}: days written off as having no statement: {Days}",
                    series, string.Join(", ", settled));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception,
                "Could not record the days without a statement in {Folder} - they will be asked about again.",
                folder);
        }
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
