namespace Gaska.Payments.Erp.Posting;

/// <summary>
/// Statements that change a cash entry already in ERP.
/// </summary>
/// <remarks>
/// Straight SQL against <c>CDN.Zapisy</c> rather than the XL API, because the API offers no way:
/// for cash there are only <c>XLDodajRaport</c>, <c>XLDodajZapis</c> and <c>XLZamknijRaport</c> -
/// no function modifies an existing entry (checked against every function in the library).
///
/// They live here, in one place, because both the service and the accounting application need
/// them and they must not drift apart: the service settles what it is sure of by itself, the
/// operator settles the rest by hand, and an entry has to end up looking the same either way.
/// </remarks>
public static class CashEntrySql
{
    /// <summary>
    /// Puts on the cash entry the party its open items were actually closed with, along with the
    /// contra account that follows from that party.
    /// </summary>
    /// <remarks>
    /// The entry is posted before the party is known - on the anonymous one - whenever the payer's
    /// account is not on anybody's card in ERP. That account often appears afterwards, added by
    /// hand in ERP, and then the very next pass matches the contractor and settles the invoices.
    /// Without this the entry would stay on the anonymous party while the open item it closed
    /// hangs on the contractor's document: the settlement is right, the books are not.
    ///
    /// Parameters: <c>@paymentId</c>, <c>@entry</c>, <c>@contractor</c>, <c>@konto</c> (empty to
    /// let the statement choose).
    /// </remarks>
    public const string SetEntryContractor = """
        -- The account to book against: the one chosen in the application, or - when nothing was
        -- chosen, which is every settlement the service makes on its own - the one this
        -- contractor's other entries carry. The cards declare nothing to go on: of 200
        -- multi-account contractors checked, not one has an account in CDN.KntKonta in any
        -- period, so usage is the only evidence there is.
        DECLARE @wybrane VARCHAR(50) = NULLIF(RTRIM(ISNULL(@konto, '')), '');

        DECLARE @docelowe VARCHAR(50) = COALESCE(@wybrane, (
            SELECT TOP 1 KAZ_KontoPrzec
            FROM CDN.Zapisy
            WHERE KAZ_KNTNumer = @contractor AND ISNULL(KAZ_KontoPrzec, '') <> ''
            GROUP BY KAZ_KontoPrzec
            ORDER BY COUNT(*) DESC));

        -- The state before the first change is remembered once; later changes do not overwrite
        -- it, so that a revoke returns to what the automat set rather than to a previous attempt.
        UPDATE p
        SET p.EntryContractorBefore = z.KAZ_KNTNumer,
            p.EntryAccountBefore = ISNULL(z.KAZ_KontoPrzec, '')
        FROM pay.Payment AS p
        INNER JOIN CDN.Zapisy AS z ON z.KAZ_GIDNumer = @entry
        WHERE p.PaymentId = @paymentId
          AND p.EntryContractorBefore IS NULL
          AND (z.KAZ_KNTNumer <> @contractor
               OR ISNULL(RTRIM(z.KAZ_KontoPrzec), '') <> ISNULL(@docelowe, ISNULL(RTRIM(z.KAZ_KontoPrzec), '')));

        UPDATE CDN.Zapisy
        SET KAZ_KNTTyp = 32,
            KAZ_KNTNumer = @contractor,
            KAZ_KontoPrzec = ISNULL(@docelowe, KAZ_KontoPrzec)
        WHERE KAZ_GIDNumer = @entry
          AND (KAZ_KNTNumer <> @contractor
               OR ISNULL(RTRIM(KAZ_KontoPrzec), '') <> ISNULL(@docelowe, ISNULL(RTRIM(KAZ_KontoPrzec), '')));
        """;

    /// <summary>
    /// Writes onto the entry the numbers of the documents it settled.
    /// </summary>
    /// <remarks>
    /// Until it is settled the entry carries the bank's own reference here, which is what
    /// identifies it while it is being posted. Once the open items are closed the accountants look
    /// for the entry by the invoice it paid, not by a reference only the bank uses - and that is
    /// already how the cash on delivery entries are numbered, by the hand that made them.
    ///
    /// Nothing is lost by overwriting it: the bank's reference stays in
    /// <c>pay.Payment.BankExternalId</c>, and the payment title stays in <c>KAZ_Tresc</c>. The one
    /// consequence is that the posting pass can no longer recognise this entry by its reference -
    /// it does not need to, because a settled operation already holds its <c>ErpEntryId</c>, and
    /// should our table ever be rebuilt the content-based pairing finds it anyway.
    ///
    /// Parameters: <c>@entry</c>, <c>@numer</c>.
    /// </remarks>
    public const string SetEntryDocumentNumber = """
        UPDATE CDN.Zapisy
        SET KAZ_NumerDokumentu = @numer
        WHERE KAZ_GIDNumer = @entry
          AND @numer <> ''
          AND ISNULL(RTRIM(KAZ_NumerDokumentu), '') <> @numer;
        """;

    /// <summary>Width of <c>KAZ_NumerDokumentu</c>.</summary>
    private const int NumberWidth = 40;

    /// <summary>
    /// The document numbers as they go onto the entry: as many as fit, then how many were left.
    /// </summary>
    /// <remarks>
    /// The field holds forty characters and a settlement can close a dozen invoices, so it will
    /// not always fit. Cutting mid-number would leave something that looks like an invoice number
    /// and is not; naming what fits and counting the rest - <c>FS-1/26, FS-2/26 +3</c> - is honest
    /// about there being more, and the whole list is on our side anyway.
    /// </remarks>
    public static string NumberFor(IReadOnlyList<string> documentNumbers)
    {
        var numbers = documentNumbers
            .Select(n => n.Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (numbers.Count == 0) return string.Empty;

        var text = string.Empty;

        for (var taken = 0; taken < numbers.Count; taken++)
        {
            var candidate = taken == 0 ? numbers[0] : $"{text}, {numbers[taken]}";
            var left = numbers.Count - taken - 1;
            var withTail = left > 0 ? $"{candidate} +{left}" : candidate;

            // The first number goes in whatever its length - a truncated one is still the best
            // clue available, and an empty field is no clue at all.
            if (withTail.Length > NumberWidth)
            {
                return taken == 0
                    ? numbers[0][..Math.Min(numbers[0].Length, NumberWidth)]
                    : $"{text} +{numbers.Count - taken}";
            }

            text = candidate;
        }

        return text;
    }

    /// <summary>
    /// Restores on the entry the party and account from before the contractor was swapped.
    /// </summary>
    /// <remarks>
    /// Called when revoking a settlement: the open item disappears, so the entry should return to
    /// the shape it was posted in. When nobody swapped anything there is nothing to undo.
    ///
    /// Parameters: <c>@paymentId</c>, <c>@entry</c>.
    /// </remarks>
    public const string RestoreEntryContractor = """
        UPDATE z
        SET z.KAZ_KNTNumer = p.EntryContractorBefore,
            z.KAZ_KNTTyp = CASE WHEN p.EntryContractorBefore = 0 THEN 0 ELSE 32 END,
            z.KAZ_KontoPrzec = p.EntryAccountBefore
        FROM CDN.Zapisy AS z
        INNER JOIN pay.Payment AS p ON p.PaymentId = @paymentId
        WHERE z.KAZ_GIDNumer = @entry AND p.EntryContractorBefore IS NOT NULL;

        UPDATE pay.Payment
        SET EntryContractorBefore = NULL, EntryAccountBefore = NULL
        WHERE PaymentId = @paymentId;
        """;
}
