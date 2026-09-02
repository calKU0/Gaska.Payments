using System.Text.RegularExpressions;
using Gaska.Payments.Domain.Model;
using Gaska.Payments.Domain.Parsing;

namespace Gaska.Payments.Integrations.Bank;

/// <summary>
/// Recognises what an operation on a statement is, so that it reaches ERP under the right cash
/// operation.
/// </summary>
public static partial class BankOperationClassifier
{
    public static string Classify(BankPayment payment, IReadOnlySet<string> ownAccounts)
    {
        if (IsBankFee(payment)) return PaymentCategory.BankFee;
        if (IsSplitPaymentLeg(payment, ownAccounts)) return PaymentCategory.SplitPayment;

        return PaymentCategory.Standard;
    }

    /// <summary>
    /// A split payment leg is a transfer between our own accounts carrying a split payment
    /// message.
    /// </summary>
    /// <remarks>
    /// Both conditions are needed. Transfers from customers carry the split payment message too -
    /// the bank repeats the same text on both legs. A transfer between our own accounts on its
    /// own is, for instance, a tax payment from the VAT account (described as <c>/TI/...</c>),
    /// which goes into ERP as an ordinary operation and is settled like one.
    /// </remarks>
    private static bool IsSplitPaymentLeg(BankPayment payment, IReadOnlySet<string> ownAccounts)
    {
        var counterparty = TextNormalizer.NormalizeAccount(payment.PayerAccount);
        if (counterparty.Length == 0 || !ownAccounts.Contains(counterparty)) return false;

        return SplitPaymentMarkerRegex().IsMatch(TextNormalizer.NormalizeCompact(payment.Description));
    }

    /// <summary>
    /// A commission: a debit with no counterparty. A missing contractor alone is not enough - a
    /// currency conversion looks the same - so we additionally require an empty description, or
    /// one that speaks plainly of a commission or of charges.
    /// </summary>
    private static bool IsBankFee(BankPayment payment)
    {
        if (payment.IsIncoming) return false;
        if (!string.IsNullOrWhiteSpace(payment.PayerAccount)) return false;

        var description = TextNormalizer.Normalize(payment.Description);
        return description.Length == 0 || FeeKeywordRegex().IsMatch(description);
    }

    /// <summary>The opening marker of a split payment message.</summary>
    [GeneratedRegex(@"/VAT/", RegexOptions.CultureInvariant)]
    private static partial Regex SplitPaymentMarkerRegex();

    [GeneratedRegex(@"PROWIZJ|OPLAT|KOSZT|CHARGE|COMMISSION|FEE", RegexOptions.CultureInvariant)]
    private static partial Regex FeeKeywordRegex();
}
