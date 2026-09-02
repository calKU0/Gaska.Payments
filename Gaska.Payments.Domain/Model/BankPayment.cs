namespace Gaska.Payments.Domain.Model;

/// <summary>
/// An operation from a bank statement (an ERP cash entry on the credit side, or a camt.053
/// entry downloaded from BNP GOconnect).
/// </summary>
public sealed record BankPayment
{
    /// <summary>ERP entry id (CDN.Zapisy.KAZ_GIDNumer) or the bank's operation id.</summary>
    public required long Id { get; init; }

    /// <summary>The original payment title (KAZ_TrescCDC / RmtInf-Ustrd).</summary>
    public required string Description { get; init; }

    /// <summary>Payer name and address as supplied by the bank.</summary>
    public string PayerName { get; init; } = string.Empty;

    /// <summary>Payer account (IBAN or national number).</summary>
    public string PayerAccount { get; init; } = string.Empty;

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }

    public required DateTime BookingDate { get; init; }

    /// <summary>Contractor already indicated by ERP or by the import (0 = unknown).</summary>
    public int KnownContractorId { get; init; }

    /// <summary>
    /// Amount still open for settlement. Filled in from ERP for payments the accounting team has
    /// already settled in part - we then match only the remainder, not the whole transfer.
    /// </summary>
    public decimal Unsettled { get; init; }

    /// <summary>Operation id assigned by the bank (camt: Refs/TxId), as text.</summary>
    public string ExternalId { get; init; } = string.Empty;

    /// <summary>The bank's reference for the operation (camt: AcctSvcrRef / NtryRef).</summary>
    public string BankReference { get; init; } = string.Empty;

    /// <summary>Reference assigned by the originator (camt: Refs/EndToEndId).</summary>
    public string EndToEndId { get; init; } = string.Empty;

    /// <summary>
    /// Details of the counterparty's bank as supplied in the statement (camt: RltdAgts).
    /// </summary>
    /// <remarks>
    /// Domestic accounts carry the clearing code inside the account number itself, foreign ones
    /// do not - for those the BIC from the statement is the only certain way to identify the
    /// bank. It is an authoritative source and requires sending account numbers to no outside
    /// service.
    /// </remarks>
    public string CounterpartyBankBic { get; init; } = string.Empty;

    public string CounterpartyBankName { get; init; } = string.Empty;

    /// <summary>National clearing code of the counterparty's bank (camt: ClrSysMmbId/MmbId).</summary>
    public string CounterpartyBankClearing { get; init; } = string.Empty;

    /// <summary>IBAN of our own account the operation belongs to.</summary>
    public string CreditedAccount { get; init; } = string.Empty;

    /// <summary>
    /// True for a credit (money coming in), false for a debit. Debits reach ERP as cash entries
    /// as well, but are not matched against sales documents.
    /// </summary>
    public bool IsIncoming { get; init; } = true;

    public decimal AmountToAllocate => Unsettled > 0m ? Unsettled : Amount;
}
