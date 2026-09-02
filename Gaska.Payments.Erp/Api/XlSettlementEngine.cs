using Microsoft.Extensions.Logging;
using cdn_api;

namespace Gaska.Payments.Erp;

/// <summary>A document payment covered by a settlement. Corrections carry a negative amount.</summary>
public sealed record SettlementLine(int DocType, int DocId, int DocLp, string DocNumber, decimal Amount);

/// <summary>GID of a settlement created in ERP - needed to be able to revoke it later.</summary>
public readonly record struct SettlementGid(int Typ, int Firma, int Numer, int Lp);

/// <summary>One settlement: the line, the amount and the GID it was created under.</summary>
public sealed record SettlementOutcome(SettlementLine Line, decimal Amount, SettlementGid Gid);

/// <summary>The outcome of settling a whole set - the set goes through entirely or not at all.</summary>
public sealed record SettlementResult(string? Error, IReadOnlyList<SettlementOutcome> Settlements)
{
    public bool Succeeded => Error is null;

    public static SettlementResult Ok(IReadOnlyList<SettlementOutcome> settlements) => new(null, settlements);

    public static SettlementResult Failed(string error) => new(error, []);
}

/// <summary>
/// Settling a bank entry against document payments through the XL API. Shared by the automat and
/// by the accounting application - both are meant to settle in exactly the same way.
/// </summary>
public sealed class XlSettlementEngine(ILogger logger)
{
    private const int BatchMode = 2;   // batch mode - no dialog windows

    /// <summary>GIDTyp of a cash or bank entry (CDN.Obiekty: "KB").</summary>
    public const int CashEntryGidType = 784;

    /// <summary>
    /// GIDTyp of a settlement. It is not listed in <c>CDN.Obiekty</c>; the value was read off
    /// what <c>XLRozliczaj</c> returns. The GID number is <c>R2_ID</c>, the company is
    /// <c>R2_GIDFirma</c>, and Lp is 0.
    /// </summary>
    public const int SettlementGidType = 433;

    /// <summary>
    /// Settles a bank entry against the given document payments.
    /// </summary>
    /// <remarks>
    /// To ERP a correction is a liability and an incoming payment a receivable, so the two cannot
    /// be settled against each other directly ("Oba dokumenty sa zobowiazaniami lub oba sa
    /// naleznosciami"). Corrections are therefore netted off against invoices first - document
    /// against document, the way an operator does it - and only what is left over is settled with
    /// the bank entry.
    ///
    /// A correction comes off the invoices one at a time: given a payment of 10 against two
    /// invoices of 500 each and a correction of -990, the first invoice is cleared in full, the
    /// second by 490, and its remainder of 10 falls to the payment.
    ///
    /// On any error everything created in this series is rolled back: a set settled halfway would
    /// be worse than one not settled at all.
    /// </remarks>
    /// <param name="entryAmount">
    /// The amount still open on the bank entry. It caps the settlement: when choosing by hand an
    /// accountant may name documents worth more than the payment covers - we then close them one
    /// by one until the transfer runs out, and the rest stays open.
    /// </param>
    public SettlementResult Settle(
        XlSession session, int entryId, IReadOnlyList<SettlementLine> lines, decimal entryAmount)
    {
        if (lines.Count == 0) return SettlementResult.Failed("Nie wskazano żadnego dokumentu do rozliczenia.");

        var done = new List<SettlementOutcome>();
        var invoices = lines.Where(l => l.Amount > 0m).ToList();
        var corrections = lines.Where(l => l.Amount < 0m).ToList();

        if (invoices.Count == 0)
        {
            return SettlementResult.Failed(
                "Zestaw zawiera same korekty – nie ma z czym ich skompensować.");
        }

        // How much is left to pay on each invoice once the corrections have come off.
        var outstanding = invoices.ToDictionary(l => l, l => l.Amount);

        foreach (var correction in corrections)
        {
            var left = -correction.Amount;

            foreach (var invoice in invoices)
            {
                if (left <= 0m) break;

                var amount = Math.Min(left, outstanding[invoice]);
                if (amount <= 0m) continue;

                var error = Post(session, DocumentPair(session, correction, invoice), amount,
                    $"{correction.DocNumber} ↔ {invoice.DocNumber}", correction, done);

                if (error is not null) return Abort(session, done, error);

                outstanding[invoice] -= amount;
                left -= amount;
            }

            if (left > 0m)
            {
                return Abort(session, done,
                    $"Korekty {correction.DocNumber} nie da się skompensować w całości – " +
                    $"zostaje {left:N2} bez faktury w zestawie.");
            }
        }

        var available = entryAmount;

        foreach (var invoice in invoices)
        {
            var amount = Math.Min(outstanding[invoice], available);
            if (amount <= 0m) continue;

            var pair = new XLGIDParaInfo_20251
            {
                Wersja = session.Version,
                Tryb = BatchMode,
                GID1Typ = CashEntryGidType,
                GID1Numer = entryId,
                GID2Typ = invoice.DocType,
                GID2Numer = invoice.DocId,
                GID2Lp = invoice.DocLp,
            };

            var error = Post(session, pair, amount, invoice.DocNumber, invoice, done);
            if (error is not null) return Abort(session, done, error);

            available -= amount;
            if (available <= 0m) break;
        }

        return SettlementResult.Ok(done);
    }

    /// <summary>Revokes a single settlement. Returns an error description, or null on success.</summary>
    public string? Revoke(XlSession session, SettlementGid gid)
    {
        var toRemove = new XLRozliczenieInfo_20251
        {
            Wersja = session.Version,
            Tryb = BatchMode,
            GIDTyp = gid.Typ,
            GIDFirma = gid.Firma,
            GIDNumer = gid.Numer,
            GIDLp = gid.Lp,
        };

        var result = cdn_api.cdn_api.XLKasujRozliczenie(session.Id, toRemove);
        if (result == 0) return null;

        logger.LogError("XLKasujRozliczenie returned {Result} for settlement {Gid}.", result, gid.Numer);
        return $"XLKasujRozliczenie zwrócił {result}.";
    }

    private string? Post(
        XlSession session,
        XLGIDParaInfo_20251 pair,
        decimal amount,
        string label,
        SettlementLine line,
        List<SettlementOutcome> done)
    {
        var settlement = new XLRozliczenieInfo_20251
        {
            Wersja = session.Version,
            Tryb = BatchMode,
            Kwota = XlSession.Amount(amount),
        };

        var result = cdn_api.cdn_api.XLRozliczaj(session.Id, pair, settlement);

        if (result != 0) return $"XLRozliczaj zwrócił {result} dla {label}: {settlement.BladOpis}";

        done.Add(new SettlementOutcome(
            line, amount,
            new SettlementGid(settlement.GIDTyp, settlement.GIDFirma, settlement.GIDNumer, settlement.GIDLp)));

        return null;
    }

    private static XLGIDParaInfo_20251 DocumentPair(
        XlSession session, SettlementLine first, SettlementLine second) => new()
    {
        Wersja = session.Version,
        Tryb = BatchMode,
        GID1Typ = first.DocType,
        GID1Numer = first.DocId,
        GID1Lp = first.DocLp,
        GID2Typ = second.DocType,
        GID2Numer = second.DocId,
        GID2Lp = second.DocLp,
    };

    private SettlementResult Abort(XlSession session, List<SettlementOutcome> done, string error)
    {
        logger.LogError("Settlement failed, revoking the whole set of {Count}: {Error}", done.Count, error);
        foreach (var outcome in done) Revoke(session, outcome.Gid);
        return SettlementResult.Failed(error);
    }
}
