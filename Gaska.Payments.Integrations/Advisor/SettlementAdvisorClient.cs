using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace Gaska.Payments.Integrations.Advisor;

/// <summary>A document the model proposes to settle, and how much of the transfer goes to it.</summary>
/// <param name="DocType">The payment's <c>TrP_GIDTyp</c>.</param>
/// <param name="DocId">The payment's <c>TrP_GIDNumer</c>.</param>
/// <param name="DocLp">The payment's <c>TrP_GIDLp</c>.</param>
/// <param name="Amount">What the model would settle against it - zero for a document it only cites.</param>
/// <param name="Reason">The model's word on why.</param>
public sealed record AdvisedDocument(int DocType, int DocId, int DocLp, decimal Amount, string Reason);

/// <summary>What the model made of one transfer.</summary>
/// <param name="Summary">Its reasoning for the transfer as a whole - "opis_zbiorczy".</param>
/// <param name="Raw">The answer exactly as it came, kept for when the model is questioned.</param>
public sealed record SettlementAdvice(IReadOnlyList<AdvisedDocument> Documents, string Summary, string Raw)
{
    /// <summary>
    /// The documents the model would actually settle something against. It also lists the ones it
    /// merely read as evidence - already settled, named in the title - with nothing to settle on
    /// them; those are its reasoning, not a proposal. A correction to net off carries a negative
    /// amount, so it is the zero that is left out, not the sign.
    /// </summary>
    public IReadOnlyList<AdvisedDocument> Proposed =>
        [.. Documents.Where(d => Math.Abs(d.Amount) > 0.004m)];
}

/// <summary>
/// Asks the language model behind the n8n workflow which documents a transfer pays.
/// </summary>
/// <remarks>
/// The workflow reads everything it needs from the database itself, so all it is given is the
/// payment's id. That id goes as text, in the body and in the address both: it is a 64-bit number,
/// and n8n is JavaScript, where a number that large loses its last digits - the workflow would look
/// up a payment that does not exist.
///
/// One question takes minutes - nine on the first transfer tried: the model reads the contractor's
/// documents one by one. The caller asks one at a time; this class only asks.
/// </remarks>
public sealed class SettlementAdvisorClient(HttpClient http, Uri endpoint)
{
    public async Task<SettlementAdvice> AskAsync(long paymentId, CancellationToken cancellationToken)
    {
        var id = paymentId.ToString(CultureInfo.InvariantCulture);
        var address = new UriBuilder(endpoint) { Query = $"PaymentId={id}" }.Uri;

        using var response = await http.PostAsJsonAsync(address, new { PaymentId = id }, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"The advisor answered {(int)response.StatusCode}: {Shorten(body, 300)}", null, response.StatusCode);
        }

        return Parse(body);
    }

    /// <summary>
    /// Reads the model's answer: <c>{"output": {"dokumenty": [...], "opis_zbiorczy": "..."}}</c>.
    /// </summary>
    /// <remarks>
    /// Lenient about the envelope, strict about the documents. n8n wraps a single item in an array
    /// or not depending on how the workflow ends, and a model now and then hands its JSON back as a
    /// string, fenced in markdown - all of that is unwrapped. A document without its three keys is
    /// dropped: there is nothing to point at.
    /// </remarks>
    public static SettlementAdvice Parse(string body)
    {
        using var document = JsonDocument.Parse(body);

        var output = Unwrap(document.RootElement);
        using var nested = output.ValueKind == JsonValueKind.String ? JsonDocument.Parse(StripFence(output.GetString()!)) : null;
        if (nested is not null) output = Unwrap(nested.RootElement);

        if (output.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"The advisor's answer holds no object: {Shorten(body, 300)}");
        }

        var documents = new List<AdvisedDocument>();

        if (output.TryGetProperty("dokumenty", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (Integer(item, "pl.TrP_GIDTyp") is not { } type
                    || Integer(item, "pl.TrP_GIDNumer") is not { } number
                    || Integer(item, "pl.TrP_GIDLp") is not { } lp)
                {
                    continue;
                }

                documents.Add(new AdvisedDocument(
                    type, number, lp,
                    Decimal(item, "kwota_do_rozliczenia") ?? 0m,
                    Text(item, "opis_rozliczenia")));
            }
        }

        return new SettlementAdvice(documents, Text(output, "opis_zbiorczy"), body);
    }

    /// <summary>Down through the array n8n may wrap it in, and into "output" when it is there.</summary>
    private static JsonElement Unwrap(JsonElement element)
    {
        while (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() > 0) element = element[0];

        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty("output", out var output)
            ? output
            : element;
    }

    private static string StripFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;

        var start = trimmed.IndexOf('\n');
        var end = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return start >= 0 && end > start ? trimmed[(start + 1)..end] : trimmed.Trim('`');
    }

    private static int? Integer(JsonElement item, string name) =>
        Decimal(item, name) is { } value && value == Math.Truncate(value) ? (int)value : null;

    /// <summary>A number, whether the model wrote it as one or as text - with a comma, even.</summary>
    private static decimal? Decimal(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(
                value.GetString()!.Replace(" ", string.Empty).Replace(',', '.'),
                NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!.Trim()
            : string.Empty;

    private static string Shorten(string text, int length) => text.Length <= length ? text : text[..length] + "…";
}
