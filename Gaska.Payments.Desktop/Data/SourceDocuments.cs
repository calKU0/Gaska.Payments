using System.IO;

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
public sealed class SourceDocuments(string archiveDirectory)
{
    private const string Statements = "Wyciągi";

    /// <summary>The account folder of each register, worked out once and kept.</summary>
    private readonly Dictionary<string, string?> _folders = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _root = archiveDirectory.Trim();

    public bool IsConfigured => _root.Length > 0;

    /// <summary>
    /// The file behind the entry, or null when the archive holds none.
    /// </summary>
    public string? Find(PaymentRow row)
    {
        if (!IsConfigured) return null;

        // A cash on delivery entry carries its report's path, written when the report was read.
        if (row.SourceFile.Length > 0) return File.Exists(row.SourceFile) ? row.SourceFile : null;

        var folder = StatementFolder(row.RegisterSeries);
        if (folder is null) return null;

        var path = Path.Combine(
            folder, row.BookingDate.ToString("yyyy-MM"),
            $"{row.RegisterSeries}_{row.BookingDate:yyyy-MM-dd}.pdf");

        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// The archive folder of one register - the one whose name starts with the register's symbol.
    /// </summary>
    /// <remarks>
    /// Looked up by prefix rather than assembled, because the second half of the name is the
    /// account number and the application has no reason to know it. The answer is kept, including
    /// the answer "there is no such folder", so that clicking through the queue does not go to
    /// disk on every row.
    /// </remarks>
    private string? StatementFolder(string series)
    {
        if (_folders.TryGetValue(series, out var found)) return found;

        var statements = Path.Combine(_root, Statements);

        try
        {
            found = Directory.Exists(statements)
                ? Directory.EnumerateDirectories(statements, $"{series} - *").FirstOrDefault()
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An archive on a share that is not answering. Nothing to open, and nothing worth
            // stopping the queue for.
            found = null;
        }

        _folders[series] = found;
        return found;
    }
}
