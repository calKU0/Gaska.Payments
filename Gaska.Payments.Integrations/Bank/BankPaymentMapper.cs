using System.Globalization;
using BNPService;
using Gaska.Payments.Domain.Model;

namespace Gaska.Payments.Integrations.Bank;

/// <summary>
/// Turns camt.052 entries (<c>Ntry</c>) into the payment model the matching engine works on.
/// </summary>
public static class BankPaymentMapper
{
    /// <summary>
    /// Every operation on the statement, credits and debits alike. Both reach ERP, because the
    /// cash report has to reflect the full movement on the account; only credits are matched
    /// against invoices.
    /// </summary>
    public static IEnumerable<BankPayment> MapAll(AccountReport11 report)
    {
        var accountIban = ReadAccount(report.Acct?.Id);

        foreach (var entry in report.Ntry ?? [])
        {
            if (entry.RvslIndSpecified && entry.RvslInd) continue;
            if (entry.Amt is null || entry.Amt.Value <= 0m) continue;

            var transaction = entry.NtryDtls?
                .SelectMany(details => details.TxDtls ?? [])
                .FirstOrDefault();

            var externalId = FirstNonEmpty(
                transaction?.Refs?.TxId,
                entry.AcctSvcrRef,
                entry.NtryRef,
                transaction?.Refs?.InstrId) ?? string.Empty;

            yield return new BankPayment
            {
                Id = StableId(accountIban, externalId, entry),
                ExternalId = externalId,
                IsIncoming = entry.CdtDbtInd == CreditDebitCode1.CRDT,
                Amount = entry.Amt.Value,
                Currency = entry.Amt.Ccy ?? string.Empty,
                BookingDate = ReadDate(entry.BookgDt) ?? ReadDate(entry.ValDt) ?? DateTime.Today,
                Description = BuildDescription(entry, transaction),
                // On a credit the counterparty is the debtor, on a debit the creditor.
                PayerName = entry.CdtDbtInd == CreditDebitCode1.CRDT
                    ? ReadPayerName(transaction?.RltdPties?.Dbtr?.Nm)
                    : ReadPayerName(transaction?.RltdPties?.Cdtr?.Nm),
                PayerAccount = entry.CdtDbtInd == CreditDebitCode1.CRDT
                    ? ReadAccount(transaction?.RltdPties?.DbtrAcct?.Id)
                    : ReadAccount(transaction?.RltdPties?.CdtrAcct?.Id),
                KnownContractorId = 0,
                Unsettled = 0m,
                BankReference = FirstNonEmpty(entry.AcctSvcrRef, entry.NtryRef) ?? string.Empty,
                EndToEndId = transaction?.Refs?.EndToEndId ?? string.Empty,
                // On a credit the counterparty bank is the debtor's, on a debit the creditor's.
                CounterpartyBankBic = ReadBank(entry, transaction)?.BIC?.Trim() ?? string.Empty,
                CounterpartyBankName = ReadBank(entry, transaction)?.Nm?.Trim() ?? string.Empty,
                CounterpartyBankClearing =
                    ReadBank(entry, transaction)?.ClrSysMmbId?.MmbId?.Trim() ?? string.Empty,
                CreditedAccount = accountIban,
            };
        }
    }

    /// <summary>
    /// Details of the institution holding the counterparty's account.
    /// </summary>
    /// <remarks>
    /// NOTE: BNP GOconnect does not populate the <c>RltdAgts</c> section at all - verified against
    /// the raw responses from every one of the five accounts. A statement entry carries only
    /// <c>RltdPties</c> (the counterparty's name, address and account number). The read stays
    /// because it costs nothing and would work should the bank ever start sending this, but today
    /// it always returns null.
    ///
    /// The practical consequence: the bank behind a foreign account cannot be established from
    /// the statement. Domestic ones we recognise by the clearing code embedded in the account
    /// number itself.
    /// </remarks>
    private static FinancialInstitutionIdentification7? ReadBank(
        ReportEntry2 entry, EntryTransaction2? transaction) =>
        entry.CdtDbtInd == CreditDebitCode1.CRDT
            ? transaction?.RltdAgts?.DbtrAgt?.FinInstnId
            : transaction?.RltdAgts?.CdtrAgt?.FinInstnId;

    /// <summary>
    /// An account number may be given as an IBAN or as the bank's own identifier
    /// (<c>Othr/Id</c>) - BNP returns domestic accounts in that second form.
    /// </summary>
    private static string ReadAccount(AccountIdentification4Choice? id) => id?.Item switch
    {
        string iban => iban.Trim(),
        GenericAccountIdentification1 other => other.Id?.Trim() ?? string.Empty,
        _ => string.Empty,
    };

    /// <summary>
    /// The payment title. In <c>Ustrd</c> BNP puts a <c>|</c> wherever the text is split into
    /// 35-character lines - exactly where ERP shows a space. We turn it into a space so that the
    /// description looks the same as in <c>KAZ_TrescCDC</c>, which the parser was tuned against.
    /// </summary>
    private static string BuildDescription(ReportEntry2 entry, EntryTransaction2? transaction)
    {
        var parts = new List<string>();

        var unstructured = transaction?.RmtInf?.Ustrd;
        if (unstructured is { Length: > 0 })
        {
            parts.Add(string.Join(' ', unstructured.Where(u => !string.IsNullOrWhiteSpace(u))));
        }

        if (!string.IsNullOrWhiteSpace(transaction?.AddtlTxInf)) parts.Add(transaction.AddtlTxInf);
        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(entry.AddtlNtryInf)) parts.Add(entry.AddtlNtryInf);

        return Unwrap(string.Join(" ", parts));
    }

    private static string Unwrap(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace('|', ' ').Trim();

    /// <summary>
    /// In <c>Dbtr/Nm</c> BNP glues the payer's name to their address, separated by a <c>|</c>
    /// ("ODM TECH S.C.|WARSZAWSKA 65"). Only the first segment is used for recognising the
    /// contractor - the address would merely blur a match by name.
    /// </summary>
    private static string ReadPayerName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var separator = value.IndexOf('|');
        return (separator < 0 ? value : value[..separator]).Trim();
    }

    /// <summary>
    /// A repeatable identifier for an operation. In .NET <see cref="string.GetHashCode()"/> is
    /// randomised per process, so for correlation across runs we compute FNV-1a instead.
    /// </summary>
    private static long StableId(string accountIban, string externalId, ReportEntry2 entry)
    {
        var seed = string.Join('#',
            accountIban,
            externalId,
            (ReadDate(entry.BookgDt) ?? DateTime.MinValue).ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            entry.Amt?.Value.ToString(CultureInfo.InvariantCulture) ?? "0");

        var hash = 14695981039346656037UL;
        foreach (var ch in seed)
        {
            hash ^= ch;
            hash *= 1099511628211UL;
        }

        return (long)(hash & 0x7FFF_FFFF_FFFF_FFFFUL);
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static DateTime? ReadDate(DateAndDateTimeChoice1? choice) =>
        choice?.Item is DateTime value && value != default ? value : null;
}
