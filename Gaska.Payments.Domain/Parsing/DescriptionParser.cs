using System.Globalization;
using System.Text.RegularExpressions;
using Gaska.Payments.Domain.Model;

namespace Gaska.Payments.Domain.Parsing;

/// <summary>
/// Extracts references to trade documents from a payment title.
/// </summary>
/// <remarks>
/// The rules were built from a sample of several hundred real titles taken from
/// CDN.Zapisy.KAZ_TrescCDC. The variants customers actually type, all of them supported:
/// <list type="bullet">
///   <item>full number: <c>(S)FS-25371/26/SPR</c>, <c>FSE-2936/26/WDT</c>, <c>(S)FSK-1654/26/SPRK</c></item>
///   <item>other separators: <c>(S)FS-25424_26_SPR</c>, <c>(S)FS.31699/26/SPR</c>, <c>(S)FS-27622 26 SPR</c></item>
///   <item>no separators at all: <c>SFS-2636126SPR</c>, <c>FSE226826WDT</c>, <c>FS18617/26/SPR</c></item>
///   <item>lists sharing one ending: <c>FS-17887,18222/26/SPR</c>, <c>SFS-32075, 32431, 32735, 33266/26/SPR</c></item>
///   <item>numbers without a symbol: <c>25508/26/SPR</c>, <c>24211/26</c>, <c>29574, 30100, 30650</c></item>
///   <item>keyword plus number: <c>za FV 23622</c>, <c>F.18645,18698,19149</c></item>
///   <item>split payment message: <c>/VAT/154,17/IDC/6770000335/INV/(S)FS-22040/26/SPR/TXT/...</c></item>
///   <item>per-document amounts: <c>FS-18713/26/SPR (1 521,60 PLN) FS-19163/26/SPR (357,14 PLN)</c></item>
/// </list>
/// </remarks>
public sealed partial class DescriptionParser
{
    /// <summary>No trade document number in this system is longer than this.</summary>
    private const int MaxDocumentNumberDigits = 7;

    private readonly MatchingOptions _options;
    private readonly string _seriesAlternation;
    private readonly Regex _typedReferenceRegex;
    private readonly Regex _numberYearSeriesRegex;
    private readonly Regex _glueedNumberYearSeriesRegex;

    public DescriptionParser(MatchingOptions? options = null)
    {
        _options = options ?? new MatchingOptions();

        _seriesAlternation = string.Join('|', _options.KnownSeries
            .Select(s => Regex.Escape(s.ToUpperInvariant()))
            .OrderByDescending(s => s.Length));

        // (S)FS-25371/26/SPR  |  FSE226826WDT  |  SFS2636126SPR  |  ZS-56220/26/S
        _typedReferenceRegex = new Regex(
            $@"(?<![A-Z0-9])(?<sp>\(S\)|S(?=(?:FSE|FSK|FKE|FSL|FS|PAK|PA|RAK|RA)))?
               (?<typ>FSE|FSK|FKE|FSL|FS|PAK|PA|RAK|RA|WZE|WZK|WZ|ZS|ZAM|NO)
               [-._/\\:\ ]*
               (?<num>\d{{1,10}})
               (?:[-._/\\\ ]+(?<yr>\d{{2}})(?![\d]))?
               (?:[-._/\\\ ]*(?<ser>{_seriesAlternation})(?![A-Z]))?",
            RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant);

        // 25508/26/SPR  |  24211/26  |  57134 26 S - a number with no document symbol.
        // A space counts as a full separator here, because customers write the number out in
        // words ("CV FACTURA NR 57134 26 S"), and the year has to be plausible anyway.
        _numberYearSeriesRegex = new Regex(
            $@"(?<![A-Z0-9])(?<num>\d{{2,6}})[-._/\\\ ](?<yr>\d{{2}})(?![\d])
               (?:[-._/\\\ ]*(?<ser>{_seriesAlternation})(?![A-Z]))?",
            RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant);

        // 5873626S - number, year and series glued together with no separators and no
        // document symbol ("PAGO PROFORMA 5873626S" is order ZS-58736/26/S).
        _glueedNumberYearSeriesRegex = new Regex(
            $@"(?<![A-Z0-9])(?<num>\d{{5,8}})(?<ser>{_seriesAlternation})(?![A-Z0-9])",
            RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant);
    }

    public ParsedDescription Parse(string? description)
    {
        var compact = TextNormalizer.NormalizeCompact(description);
        if (compact.Length == 0)
        {
            return new ParsedDescription { Original = description ?? string.Empty, Compact = string.Empty };
        }

        // The text is analysed twice, because neither form is sufficient on its own:
        //  * without spaces - glues back numbers cut by the 35-character wrapping
        //    ("(S)FS-23 069/26/SPR"), but loses word boundaries ("INVOICE FSE-4760/26" becomes
        //    one run),
        //  * with spaces - word boundaries are real, but cut numbers stay cut.
        // From the second pass we therefore take only references that carry a year: a cut number
        // has no year, so false positives cannot slip through.
        var spaced = TextNormalizer.Normalize(description);

        var (nip, vat, invField) = ParseSplitPayment(compact, new bool[compact.Length]);
        var ksef = KsefRegex().Matches(compact).Select(m => m.Value).ToList();

        var references = new List<DocumentReference>();
        references.AddRange(Extract(compact, requireYearForTypedReferences: false));
        references.AddRange(Extract(spaced, requireYearForTypedReferences: true));

        return new ParsedDescription
        {
            Original = description ?? string.Empty,
            Compact = compact,
            References = Deduplicate(references),
            SplitPaymentNip = nip,
            SplitPaymentVat = vat,
            SplitPaymentInvoiceField = invField,
            KsefNumbers = ksef,
            MentionsInvoiceWithoutNumber = references.Count == 0 && InvoiceWordRegex().IsMatch(compact),
        };
    }

    private List<DocumentReference> Extract(string text, bool requireYearForTypedReferences)
    {
        // A "character already used" mask, so that the same digit cannot end up in a document
        // number, a date, an amount and a tax id all at once.
        var consumed = new bool[text.Length];

        MaskAll(KsefRegex(), text, consumed);
        MaskAll(NipWithDateRegex(), text, consumed);
        MaskAll(IbanRegex(), text, consumed);
        MaskAll(LongAccountRegex(), text, consumed);
        MaskAll(IsoDateRegex(), text, consumed);
        MaskAll(DottedDateRegex(), text, consumed);
        MaskAll(CompactDateRegex(), text, consumed);
        MaskAll(MppIdcRegex(), text, consumed);
        MaskAll(AmountRegex(), text, consumed);

        var references = new List<DocumentReference>();
        references.AddRange(ExtractTypedReferences(text, consumed, requireYearForTypedReferences));
        references.AddRange(ExtractUntypedNumberYearReferences(text, consumed));
        references.AddRange(ExtractGluedNumberYearSeriesReferences(text, consumed));

        // Only now do we cut out identifiers such as "88FA79C00000" (letters mixed with
        // digits). Not earlier: a glued invoice number - "SFS2636126SPR" - looks exactly the same.
        MaskAll(MixedAlphanumericTokenRegex(), text, consumed);
        references.AddRange(ExtractBareNumbers(text, consumed));
        return references;
    }

    // ---------------------------------------------------------------- MPP ---

    private static (string? Nip, decimal? Vat, string? InvField) ParseSplitPayment(string compact, bool[] consumed)
    {
        string? nip = null;
        decimal? vat = null;
        string? invField = null;

        var vatMatch = MppVatRegex().Match(compact);
        if (vatMatch.Success)
        {
            vat = ParseAmount(vatMatch.Groups["v"].Value);
            Mask(consumed, vatMatch.Groups["v"]);
        }

        var idcMatch = MppIdcRegex().Match(compact);
        if (idcMatch.Success)
        {
            nip = TextNormalizer.DigitsOnly(idcMatch.Groups["n"].Value);
            Mask(consumed, idcMatch.Groups["n"]);
        }

        var invMatch = MppInvRegex().Match(compact);
        if (invMatch.Success) invField = invMatch.Groups["i"].Value;

        return (nip, vat, invField);
    }

    // --------------------------------------------------- document numbers ---

    private IEnumerable<DocumentReference> ExtractTypedReferences(string compact, bool[] consumed, bool requireYear)
    {
        var results = new List<DocumentReference>();

        foreach (Match match in _typedReferenceRegex.Matches(compact))
        {
            if (IsConsumed(consumed, match.Index, match.Length)) continue;
            if (requireYear && !match.Groups["yr"].Success) continue;

            var kind = MapKind(match.Groups["typ"].Value);
            if (kind == DocumentKind.Unknown) continue;

            var digits = match.Groups["num"].Value;
            var year = match.Groups["yr"].Success ? int.Parse(match.Groups["yr"].Value, CultureInfo.InvariantCulture) : (int?)null;
            var series = match.Groups["ser"].Success ? match.Groups["ser"].Value : null;

            // "FSE226826WDT" - the year typed with no separator, stuck onto the number.
            if (year is null && digits.Length >= 5 && (series is not null || digits.Length >= 6))
            {
                var candidateYear = int.Parse(digits[^2..], CultureInfo.InvariantCulture);
                if (IsPlausibleYear(candidateYear))
                {
                    year = candidateYear;
                    digits = digits[..^2];
                }
            }

            var group = new List<DocumentReference>();

            if (digits.Length is >= 8 and <= 12 && digits.Length % 2 == 0 && year is null)
            {
                // "FS-2896828969" - two numbers glued together with no separator.
                var half = digits.Length / 2;
                AddIfValid(group, MakeReference(kind, digits[..half], null, null, ReferenceStrength.ListContinuation, match.Value));
                AddIfValid(group, MakeReference(kind, digits[half..], null, null, ReferenceStrength.ListContinuation, match.Value));
            }
            else
            {
                var strength = series is not null && year is not null
                    ? ReferenceStrength.FullNumber
                    : year is not null
                        ? ReferenceStrength.KindNumberYear
                        : ReferenceStrength.KindAndNumber;

                AddIfValid(group, MakeReference(kind, digits, year, series, strength, match.Value));
            }

            if (group.Count == 0) continue;

            Mask(consumed, match.Index, match.Length);

            // A list sharing one ending: "FS-17887,18222/26/SPR"
            var cursor = match.Index + match.Length;
            var tail = ReadListContinuation(compact, consumed, kind, ref cursor, group);
            if (tail is not null)
            {
                year ??= tail.Value.Year;
                series ??= tail.Value.Series;
            }

            // A year or series given once at the end of a list applies to every element.
            for (var i = 0; i < group.Count; i++)
            {
                var reference = group[i];
                var effectiveYear = reference.Year ?? year;
                var effectiveSeries = reference.Series ?? series;
                if (effectiveYear == reference.Year && effectiveSeries == reference.Series) continue;

                group[i] = reference with
                {
                    Year = effectiveYear,
                    Series = effectiveSeries,
                    Strength = reference.Strength == ReferenceStrength.ListContinuation
                        ? ReferenceStrength.ListContinuation
                        : effectiveSeries is not null && effectiveYear is not null
                            ? ReferenceStrength.FullNumber
                            : effectiveYear is not null
                                ? ReferenceStrength.KindNumberYear
                                : reference.Strength,
                };
            }

            // A per-document amount: "FS-18713/26/SPR (1 521,60 PLN)"
            var declared = ReadDeclaredAmount(compact, consumed, cursor);
            if (declared is not null && group.Count == 1)
            {
                group[0] = group[0] with { DeclaredAmount = declared };
            }

            results.AddRange(group);
        }

        return results;
    }

    private (int? Year, string? Series)? ReadListContinuation(
        string compact, bool[] consumed, DocumentKind kind, ref int cursor, List<DocumentReference> group)
    {
        (int? Year, string? Series)? tail = null;
        var appended = false;

        while (cursor < compact.Length)
        {
            var slice = compact[cursor..];
            var item = ListItemRegex().Match(slice);
            if (!item.Success || item.Index != 0) break;

            var digits = item.Groups["num"].Value;
            var year = item.Groups["yr"].Success ? int.Parse(item.Groups["yr"].Value, CultureInfo.InvariantCulture) : (int?)null;
            var series = item.Groups["ser"].Success ? NormalizeSeries(item.Groups["ser"].Value) : null;

            if (year is not null && !IsPlausibleYear(year.Value)) break;

            AddIfValid(group, MakeReference(kind, digits, year, series, ReferenceStrength.ListContinuation, item.Value));
            Mask(consumed, cursor, item.Length);
            cursor += item.Length;
            appended = true;

            if (year is not null || series is not null) tail = (year, series);
        }

        return appended ? tail : null;
    }

    private static decimal? ReadDeclaredAmount(string compact, bool[] consumed, int cursor)
    {
        if (cursor >= compact.Length) return null;

        var match = DeclaredAmountRegex().Match(compact[cursor..]);
        if (!match.Success || match.Index != 0) return null;

        Mask(consumed, cursor, match.Length);
        return ParseAmount(match.Groups["v"].Value);
    }

    private IEnumerable<DocumentReference> ExtractUntypedNumberYearReferences(string compact, bool[] consumed)
    {
        foreach (Match match in _numberYearSeriesRegex.Matches(compact))
        {
            if (IsConsumed(consumed, match.Index, match.Length)) continue;

            var year = int.Parse(match.Groups["yr"].Value, CultureInfo.InvariantCulture);
            if (!IsPlausibleYear(year)) continue;

            var series = match.Groups["ser"].Success ? NormalizeSeries(match.Groups["ser"].Value) : null;
            Mask(consumed, match.Index, match.Length);

            var reference = MakeReference(
                DocumentKind.Unknown,
                match.Groups["num"].Value,
                year,
                series,
                series is not null ? ReferenceStrength.KindNumberYear : ReferenceStrength.InvoiceKeyword,
                match.Value);

            if (reference is not null) yield return reference;
        }
    }

    /// <summary>
    /// Number, year and series glued into one run with no document symbol - "5873626S".
    /// The last two digits are the year, the rest is the number.
    /// </summary>
    private IEnumerable<DocumentReference> ExtractGluedNumberYearSeriesReferences(string compact, bool[] consumed)
    {
        foreach (Match match in _glueedNumberYearSeriesRegex.Matches(compact))
        {
            if (IsConsumed(consumed, match.Index, match.Length)) continue;

            var digits = match.Groups["num"].Value;
            var year = int.Parse(digits[^2..], CultureInfo.InvariantCulture);
            if (!IsPlausibleYear(year)) continue;

            var reference = MakeReference(
                DocumentKind.Unknown,
                digits[..^2],
                year,
                NormalizeSeries(match.Groups["ser"].Value),
                ReferenceStrength.KindNumberYear,
                match.Value);

            if (reference is null) continue;

            Mask(consumed, match.Index, match.Length);
            yield return reference;
        }
    }

    private IEnumerable<DocumentReference> ExtractBareNumbers(string compact, bool[] consumed)
    {
        foreach (Match match in BareNumberRegex().Matches(compact))
        {
            if (IsConsumed(consumed, match.Index, match.Length)) continue;

            var digits = match.Value;
            if (digits.Length > _options.MaxBareNumberLength) continue;

            var value = long.Parse(digits, CultureInfo.InvariantCulture);
            if (value == 0) continue;
            if (digits.Length == 4 && value is >= 1990 and <= 2100) continue; // a year

            var hasKeyword = HasInvoiceKeywordBefore(compact, match.Index);
            var strength = hasKeyword ? ReferenceStrength.InvoiceKeyword : ReferenceStrength.BareNumber;
            var kind = hasKeyword ? DocumentKind.AnyInvoice : DocumentKind.Unknown;

            Mask(consumed, match.Index, match.Length);

            var reference = MakeReference(kind, digits, null, null, strength, digits);
            if (reference is not null) yield return reference;
        }
    }

    private static void AddIfValid(List<DocumentReference> group, DocumentReference? reference)
    {
        if (reference is not null) group.Add(reference);
    }

    private static bool HasInvoiceKeywordBefore(string compact, int index)
    {
        var from = Math.Max(0, index - 24);
        var window = compact[from..index];
        return InvoiceKeywordRegex().IsMatch(window);
    }

    // --------------------------------------------------------------- helpers ---

    /// <summary>
    /// Builds a reference from the digits found. Returns <c>null</c> when the run cannot be a
    /// document number - ours fit in a handful of digits, and longer runs are references from
    /// foreign systems that would not fit an <see cref="int"/> anyway.
    /// </summary>
    private DocumentReference? MakeReference(
        DocumentKind kind, string digits, int? year, string? series, ReferenceStrength strength, string raw)
    {
        var trimmed = digits.TrimStart('0');
        if (trimmed.Length == 0 || trimmed.Length > MaxDocumentNumberDigits) return null;

        var number = int.Parse(trimmed, CultureInfo.InvariantCulture);
        return new DocumentReference(kind, number, year, NormalizeSeries(series), strength, raw, null, kind.IsCorrection());
    }

    private string? NormalizeSeries(string? series)
    {
        if (string.IsNullOrWhiteSpace(series)) return null;
        var upper = series.ToUpperInvariant();
        return _options.KnownSeries.Contains(upper, StringComparer.OrdinalIgnoreCase) ? upper : null;
    }

    private static bool IsPlausibleYear(int year) => year is >= 15 and <= 40;

    private static DocumentKind MapKind(string symbol) => symbol.ToUpperInvariant() switch
    {
        "FS" => DocumentKind.Fs,
        "FSE" => DocumentKind.Fse,
        "FSK" => DocumentKind.Fsk,
        "FKE" => DocumentKind.Fke,
        "FSL" => DocumentKind.Fsl,
        "PA" => DocumentKind.Pa,
        "PAK" => DocumentKind.Pak,
        "RA" or "RAK" => DocumentKind.Ra,
        "WZ" or "WZE" or "WZK" => DocumentKind.Wz,
        "ZS" or "ZAM" => DocumentKind.Zs,
        "NO" => DocumentKind.No,
        _ => DocumentKind.Unknown,
    };

    private static IReadOnlyList<DocumentReference> Deduplicate(List<DocumentReference> references)
    {
        return references
            .GroupBy(r => (r.Kind, r.Number, r.Year, r.Series))
            .Select(g => g.OrderByDescending(r => r.Strength).First())
            .OrderByDescending(r => r.Strength)
            .ThenBy(r => r.Number)
            .ToList();
    }

    private static decimal? ParseAmount(string text)
    {
        var cleaned = text.Replace(" ", string.Empty).Replace(',', '.');
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? Math.Abs(value)
            : null;
    }

    private static List<string> MaskAll(Regex regex, string text, bool[] consumed)
    {
        var found = new List<string>();
        foreach (Match match in regex.Matches(text))
        {
            found.Add(match.Value);
            Mask(consumed, match.Index, match.Length);
        }
        return found;
    }

    private static void Mask(bool[] consumed, Group group) => Mask(consumed, group.Index, group.Length);

    private static void Mask(bool[] consumed, int start, int length)
    {
        for (var i = start; i < start + length && i < consumed.Length; i++) consumed[i] = true;
    }

    private static bool IsConsumed(bool[] consumed, int start, int length)
    {
        for (var i = start; i < start + length && i < consumed.Length; i++)
        {
            if (consumed[i]) return true;
        }
        return false;
    }

    // ------------------------------------------------- regular expressions ---

    [GeneratedRegex(@"(?<!\d)\d{10}-\d{8}-?[0-9A-Z]{8,20}(?:-[0-9A-Z]{2})?", RegexOptions.CultureInvariant)]
    private static partial Regex KsefRegex();

    /// <summary>Tax id plus the date that opens a KSeF number, also when line wrapping cut it with a space.</summary>
    [GeneratedRegex(@"(?<!\d)\d{10}\ ?-\ ?[\d\ ]{4,12}", RegexOptions.CultureInvariant)]
    private static partial Regex NipWithDateRegex();

    /// <summary>A token of at least eight characters mixing letters and digits - a foreign system's identifier.</summary>
    [GeneratedRegex(@"(?<![0-9A-Z])(?=[0-9A-Z]{8,})[0-9A-Z]*[A-Z][0-9A-Z]*\d[0-9A-Z]*(?![0-9A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex MixedAlphanumericTokenRegex();

    [GeneratedRegex(@"(?<![A-Z0-9])[A-Z]{2}\d{2}[A-Z0-9]{10,30}(?![A-Z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex IbanRegex();

    [GeneratedRegex(@"(?<!\d)\d{20,34}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex LongAccountRegex();

    [GeneratedRegex(@"(?<!\d)(?:19|20)\d{2}-\d{2}-\d{2}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDateRegex();

    [GeneratedRegex(@"(?<!\d)\d{1,2}[.\-]\d{1,2}(?:[.\-](?:19|20)?\d{2})?(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex DottedDateRegex();

    [GeneratedRegex(@"(?<!\d)(?:19|20)\d{2}(?:0[1-9]|1[0-2])(?:0[1-9]|[12]\d|3[01])(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex CompactDateRegex();

    [GeneratedRegex(@"(?<!\d)\d{1,9},\d{2}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex AmountRegex();

    [GeneratedRegex(@"/VAT/(?<v>\d{1,9},\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex MppVatRegex();

    [GeneratedRegex(@"/IDC/(?<n>[A-Z]{0,2}[\d\-]{10,20})", RegexOptions.CultureInvariant)]
    private static partial Regex MppIdcRegex();

    [GeneratedRegex(@"/INV/(?<i>.*?)(?:/TXT/|$)", RegexOptions.CultureInvariant)]
    private static partial Regex MppInvRegex();

    [GeneratedRegex(@"^[,;+&\ ]+(?<num>\d{2,6})(?:[-._/\\\ ]+(?<yr>\d{2})(?!\d))?(?:[-._/\\\ ]*(?<ser>[A-Z]{1,5})(?![A-Z]))?", RegexOptions.CultureInvariant)]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"^[\(\-=\ ]{0,3}(?<v>\d{1,3}(?:\ ?\d{3})*,\d{2})\ ?(?:PLN|EUR|USD|ZL)?\)?", RegexOptions.CultureInvariant)]
    private static partial Regex DeclaredAmountRegex();

    /// <remarks>
    /// A number may follow a dot, and often does ("F.32502", "nr.15723"), so we only cut runs
    /// that are stuck to other digits. Decimal parts of amounts are harmless here because the
    /// amounts were masked earlier, and the three-digit minimum sieves out the rest.
    /// </remarks>
    [GeneratedRegex(@"(?<!\d)\d{3,12}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex BareNumberRegex();

    [GeneratedRegex(@"(?:FVAT|FAKTURY|FAKTURA|FAKTURE|FAKT|FAK|FRA|FV|FA|F|INVOICE|INV|RACHUNEK|PRZELEW|DOTYCZY|DOT|SPLATA|ZAPLATA|PLATNOSC|NR|ZA)[.:#\-\ ]*$", RegexOptions.CultureInvariant)]
    private static partial Regex InvoiceKeywordRegex();

    [GeneratedRegex(@"FAKTUR|FVAT|INVOICE|RACHUNK|PLATNOSC|ZAPLATA|SPLATA", RegexOptions.CultureInvariant)]
    private static partial Regex InvoiceWordRegex();
}
