using Gaska.Payments.Erp.Posting;
using Microsoft.Data.SqlClient;

namespace Gaska.Payments.Application.Posting;

/// <summary>
/// Records the outcome of posting right after each operation, synchronously.
/// </summary>
/// <remarks>
/// <c>await</c> must not be used inside an XL session - the native API is bound to its thread.
/// Deferring the write is out too: if the process breaks off midway, ERP is left holding
/// documents we know nothing about, and the next pass would create duplicates.
///
/// A connection is opened per operation. A pass runs for a good quarter of an hour, and a single
/// connection held open that long can be dropped by the network while posting is under way.
/// </remarks>
public sealed class PostingJournal(string connectionString)
{
    public void MarkPosted(long paymentId, int entryId) => Execute(
        """
        UPDATE pay.Payment
        SET ErpEntryId = @entryId, PostedAt = SYSDATETIME(), PostingError = NULL
        WHERE PaymentId = @paymentId
        """,
        ("@entryId", entryId), ("@paymentId", paymentId));

    public void MarkFailed(long paymentId, string error) => Execute(
        "UPDATE pay.Payment SET PostingError = @error WHERE PaymentId = @paymentId",
        ("@error", error.Length > 500 ? error[..500] : error), ("@paymentId", paymentId));

    public void MarkAllocationSettled(int allocationId, int settlementId) => Execute(
        "UPDATE pay.Allocation SET SettlementId = @settlementId WHERE AllocationId = @id",
        ("@settlementId", settlementId), ("@id", allocationId));

    public void MarkSettled(long paymentId) => Execute(
        "UPDATE pay.Payment SET Status = 'Posted', SettledAt = SYSDATETIME() WHERE PaymentId = @id",
        ("@id", paymentId));

    /// <summary>
    /// Puts the party the open items were closed with onto the cash entry, with its contra
    /// account. Does nothing when the entry already names that party.
    /// </summary>
    /// <remarks>
    /// The same statement the accounting application runs when an operator settles by hand - an
    /// entry must end up the same whoever settled it. See <see cref="CashEntrySql"/>.
    /// </remarks>
    public void UpdateEntryContractor(
        long paymentId, int entryId, int contractorId, string account = "") => Execute(
        CashEntrySql.SetEntryContractor,
        ("@contractor", contractorId), ("@entry", entryId), ("@paymentId", paymentId),
        ("@konto", account));

    /// <summary>
    /// Gives an unsettled entry its contractor's usual contra account, when it has none. See
    /// <see cref="CashEntrySql.SetEntryAccount"/>.
    /// </summary>
    public void UpdateEntryAccount(int entryId, int contractorId) => Execute(
        CashEntrySql.SetEntryAccount, ("@entry", entryId), ("@contractor", contractorId));

    /// <summary>
    /// Writes the numbers of the settled documents onto the entry, in place of the bank's
    /// reference. See <see cref="CashEntrySql.SetEntryDocumentNumber"/>.
    /// </summary>
    public void UpdateEntryDocumentNumber(int entryId, IReadOnlyList<string> documentNumbers) => Execute(
        CashEntrySql.SetEntryDocumentNumber,
        ("@entry", entryId), ("@numer", CashEntrySql.NumberFor(documentNumbers)));

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();

        using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }
}
