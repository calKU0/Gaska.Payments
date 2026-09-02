using Gaska.Payments.Domain.Model;
using Gaska.Payments.Domain.Parsing;

namespace Gaska.Payments.Erp;

/// <summary>
/// Ties an operation downloaded from GOconnect to the entry that corresponds to it in
/// <c>CDN.Zapisy</c>.
/// </summary>
/// <remarks>
/// ERP does not keep the bank's reference for an operation (<c>KAZ_UnikalnyId</c> is empty), so
/// the correlation runs on amount, currency, date and title similarity. It is needed for two
/// things: skipping payments the accounting team has already settled by hand, and comparing what
/// the engine proposes with what they decided.
/// </remarks>
public static class BankToErpCorrelator
{
    private const double MinimumTitleSimilarity = 0.55;
    private const int MaxDayDistance = 4;

    public static Dictionary<long, ErpReadRepository.ErpBankEntry> Correlate(
        IReadOnlyList<BankPayment> payments,
        IReadOnlyList<ErpReadRepository.ErpBankEntry> erpEntries)
    {
        var byAmount = erpEntries
            .GroupBy(e => (e.Amount, e.Currency))
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new Dictionary<long, ErpReadRepository.ErpBankEntry>();
        var used = new HashSet<long>();

        foreach (var payment in payments)
        {
            if (!byAmount.TryGetValue((payment.Amount, payment.Currency), out var candidates)) continue;

            var paymentText = TextNormalizer.NormalizeCompact(payment.Description);

            var best = candidates
                .Where(c => !used.Contains(c.Id))
                .Where(c => Math.Abs((c.Date - payment.BookingDate).TotalDays) <= MaxDayDistance)
                .Select(c => new
                {
                    Entry = c,
                    Similarity = Similarity(paymentText, TextNormalizer.NormalizeCompact(c.Description)),
                    DayDistance = Math.Abs((c.Date - payment.BookingDate).TotalDays),
                })
                .OrderByDescending(x => x.Similarity)
                .ThenBy(x => x.DayDistance)
                .FirstOrDefault();

            // With an identical amount and date the title need not agree character for
            // character (ERP does shorten it), but wholly different descriptions are rejected.
            if (best is null || best.Similarity < MinimumTitleSimilarity) continue;

            result[payment.Id] = best.Entry;
            used.Add(best.Entry.Id);
        }

        return result;
    }

    /// <summary>Share of shared three-character fragments - robust to shortening and reordering.</summary>
    private static double Similarity(string left, string right)
    {
        if (left.Length == 0 && right.Length == 0) return 1.0;
        if (left.Length < 3 || right.Length < 3) return left == right ? 1.0 : 0.0;

        var leftGrams = Trigrams(left);
        var rightGrams = Trigrams(right);

        var shared = leftGrams.Count(rightGrams.Contains);
        return shared / (double)Math.Min(leftGrams.Count, rightGrams.Count);
    }

    private static HashSet<string> Trigrams(string value)
    {
        var grams = new HashSet<string>();
        for (var i = 0; i + 3 <= value.Length; i++) grams.Add(value.Substring(i, 3));
        return grams;
    }
}
