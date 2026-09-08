using System.IO;
using Microsoft.Extensions.Logging;

namespace Gaska.Payments.Desktop.Data;

/// <summary>
/// Finds the document an entry came from: the bank statement behind a transfer, or the courier's
/// payout report behind a cash on delivery.
/// </summary>
/// <remarks>
/// The service writes both into one archive, whose root is named in <c>Archive:Directory</c> - the
/// same setting on both sides. The layout underneath is
/// <c>Wyciągi\{SERIA} - {rachunek}\{yyyy-MM}\{SERIA}_{data}.pdf</c> for statements and
/// <c>Pobrania\{Kurier}\{yyyy-MM}\...</c> for the reports.
///
/// A courier report is found by the path stored on the row, because nothing about the entry says
/// which of the day's several files it came out of. A statement needs no stored path: register and
/// date name it, and working it out means the archive can be moved or refilled without touching
/// anything in the database.
///
/// The rules are repeated here rather than shared with the service. The application would
/// otherwise have to reference the whole infrastructure project - a mail client, a SOAP stack and
/// a spreadsheet reader - to learn how to build two paths.
/// </remarks>
public sealed class SourceDocuments(string archiveDirectory, ILogger? logger = null)
{
    private const string Statements = "Wyciągi";

    /// <summary>The account folder of each register, worked out once and kept.</summary>
    private readonly Dictionary<string, string?> _folders = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _root = archiveDirectory.Trim();

    public bool IsConfigured => _root.Length > 0;

    /// <summary>Whether the archive root is actually there - a setting alone proves nothing.</summary>
    public bool Exists => IsConfigured && Directory.Exists(Path.Combine(_root, Statements));

    /// <summary>
    /// The archive as it is for the startup log: where it is looked for and whether it answers.
    /// </summary>
    /// <remarks>
    /// Worth a line of its own because the archive is the one setting the application cannot
    /// complain about by itself. A wrong root looks exactly like a day with no statement - the
    /// button is simply not there - and the service writes to a folder of its own, so the two can
    /// drift apart without anything noticing. That is what happened: the service was filing to a
    /// share while the application looked in a local folder that did not exist.
    /// </remarks>
    public string Describe() => !IsConfigured
        ? "archiwum nieskonfigurowane (Archive:Directory)"
        : $"archiwum {_root} ({(Exists ? "dostępne" : "NIEDOSTĘPNE albo bez podfolderu " + Statements)})";

    /// <summary>
    /// The file behind the entry, or null when the archive holds none.
    /// </summary>
    /// <summary>
    /// Guards what has been looked up.
    /// </summary>
    /// <remarks>
    /// The queue is built on a worker thread while the operator can still click a row, and
    /// entering a row asks the archive again. Two threads, one set of dictionaries.
    /// </remarks>
    private readonly Lock _gate = new();

    public string? Find(PaymentRow row)
    {
        if (!IsConfigured) return null;

        lock (_gate) return Look(row);
    }

    private string? Look(PaymentRow row)
    {

        // A cash on delivery entry carries its report's path, written when the report was read.
        // Answers are remembered because one report covers a whole payout - the same path comes
        // back on dozens of rows, and over a share each of them was a round trip of its own.
        if (row.SourceFile.Length > 0)
        {
            if (!_files.TryGetValue(row.SourceFile, out var there))
            {
                there = FileIsThere(row.SourceFile);
                _files[row.SourceFile] = there;
            }

            return there ? row.SourceFile : null;
        }

        var folder = StatementFolder(row.RegisterSeries);
        if (folder is null) return null;

        var month = row.BookingDate.ToString("yyyy-MM");
        var name = $"{row.RegisterSeries}_{row.BookingDate:yyyy-MM-dd}.pdf";

        return MonthOf(folder, month).Contains(name)
            ? Path.Combine(folder, month, name)
            : null;
    }

    /// <summary>Whether one file is there, treating an unreachable share as "no".</summary>
    private static bool FileIsThere(string path)
    {
        try { return File.Exists(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>What each answer cost is remembered - see <see cref="MonthOf"/>.</summary>
    private readonly Dictionary<string, bool> _files = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, HashSet<string>> _months = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The statements one month's folder holds, listed once.
    /// </summary>
    /// <remarks>
    /// Asking <c>File.Exists</c> per transfer meant one round trip per row - eighteen thousand of
    /// them over the sixty days the queue opens on, against a folder on a share. One listing per
    /// register and month answers all of them: a month holds at most thirty-one statements, and
    /// the queue spans two or three months.
    ///
    /// A month that is not there yet is remembered as empty and asked about again on the next
    /// load, not on the next row - today's statement appears during the day, and the queue is
    /// reloaded often enough for that to show up.
    /// </remarks>
    private HashSet<string> MonthOf(string folder, string month)
    {
        var key = Path.Combine(folder, month);
        if (_months.TryGetValue(key, out var files)) return files;

        try
        {
            files = Directory.Exists(key)
                ? new HashSet<string>(
                    Directory.EnumerateFiles(key, "*.pdf").Select(f => Path.GetFileName(f)!),
                    StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Warn("Nie mogę odczytać {Folder}: {Blad}", key, exception.Message);
            files = [];
        }

        _months[key] = files;
        return files;
    }

    /// <summary>
    /// Forgets what was found, so the next load looks at the archive again.
    /// </summary>
    /// <remarks>
    /// Called when the queue is reloaded. Without it a statement written after the application
    /// started would stay invisible for the rest of the session.
    /// </remarks>
    public void Forget()
    {
        lock (_gate)
        {
            _files.Clear();
            _months.Clear();
            _folders.Clear();
        }
    }

    /// <summary>
    /// The archive folder of one register - the one whose name starts with the register's symbol.
    /// </summary>
    /// <remarks>
    /// Looked up by prefix rather than assembled, because the second half of the name is the
    /// account number and the application has no reason to know it.
    ///
    /// A folder that was found is remembered, so that clicking through the queue does not go to
    /// disk on every row. A folder that was not found is looked for again: the service creates one
    /// the first time it saves a statement for that register, and a "no" cached at start-up would
    /// otherwise hide every statement of that register until the application was restarted.
    /// </remarks>
    private string? StatementFolder(string series)
    {
        if (_folders.TryGetValue(series, out var found) && found is not null) return found;

        var statements = Path.Combine(_root, Statements);

        try
        {
            if (!Directory.Exists(statements))
            {
                Warn("Nie ma folderu {Folder} - przyciski z wyciągami się nie pokażą.", statements);
                found = null;
            }
            else
            {
                found = Directory.EnumerateDirectories(statements, $"{series} - *").FirstOrDefault();

                if (found is null)
                {
                    Warn("W {Folder} nie ma folderu rejestru {Series} - jego wyciągów nie pokażę.",
                        statements, series);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An archive on a share that is not answering. Nothing to open, and nothing worth
            // stopping the queue for - but it must not pass in silence either, because from the
            // window it is indistinguishable from a day the bank had no statement for.
            Warn("Nie mogę odczytać {Folder}: {Blad}", statements, exception.Message);
            found = null;
        }

        _folders[series] = found;
        return found;
    }

    /// <summary>Said once per reason, so clicking through the queue does not fill the log.</summary>
    private readonly HashSet<string> _said = [];

    private void Warn(string message, params object?[] arguments)
    {
        if (logger is null) return;

        var key = message + string.Join('|', arguments);
        if (!_said.Add(key)) return;

        logger.LogWarning(message, arguments);
    }
}
