using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gaska.Payments.Desktop.Data;

/// <summary>
/// Bank details read off ibancalculator.com for one account number. Every field may be empty -
/// the site knows a good deal about European banks and next to nothing about some others.
/// </summary>
public sealed record IbanLookupResult(
    bool Valid,
    string BankCode,
    string Bic,
    string Name,
    string Street,
    string PostalCode,
    string City,
    string Message)
{
    public static readonly IbanLookupResult Nothing =
        new(false, "", "", "", "", "", "", "Strona nie zwróciła danych banku.");

    public bool HasAnything =>
        Bic.Length > 0 || Name.Length > 0 || Street.Length > 0 || PostalCode.Length > 0;
}

/// <summary>
/// Looking a bank up on <c>pl.ibancalculator.com</c> from the account number.
/// </summary>
/// <remarks>
/// BNP sends no details of the counterparty's bank in any message, so a bank absent from the ERP
/// register has to be described by hand off whatever document the operator has. This fetches the
/// same details from the public IBAN register instead, and the operator sees the page it came
/// from, side by side with the form, and confirms it.
///
/// The page is asked for over a plain GET - the form posts, but it answers to the same parameters
/// in the query string, which is what the site's own "link to this result" does. That matters
/// here: a URL can be handed straight to the browser control, so what the operator reads is the
/// very page the fields were filled from, not a second request that might answer differently.
///
/// One lookup per press of a button by a person. Nothing runs in a loop and nothing is fetched
/// for the queue in bulk.
/// </remarks>
public static class IbanLookup
{
    /// <summary>The page showing the result for one account number.</summary>
    public static string Url(string account)
    {
        var iban = new string([.. account.Where(char.IsLetterOrDigit)]).ToUpperInvariant();

        return "https://pl.ibancalculator.com/iban_validieren.html?no_cache=1"
             + $"&tx_valIBAN_pi1%5biban%5d={Uri.EscapeDataString(iban)}"
             + "&tx_valIBAN_pi1%5bfi%5d=fi";
    }

    /// <summary>
    /// The script run against the loaded page. It returns JSON, so the reading of the page stays
    /// in one piece instead of being split between a script and a regular expression here.
    /// </summary>
    /// <remarks>
    /// Read from the two fieldsets the page is built of: "Dokonane metody sprawdzenia" carries the
    /// national bank code, "Wynik" the BIC, the name and the address. The shapes vary by country
    /// and all of them are handled: the bank name is sometimes a link to the bank's site
    /// (mBank) and sometimes plain text (Commerzbank); the BIC is sometimes a link with the
    /// city appended after it; the address is two lines with a street (<c>ul. Roosevelta 22 /
    /// 60-829 Poznań</c>) or one line without (<c>50447 Köln</c>) - so the last line is always
    /// the town and whatever precedes it is the street.
    /// </remarks>
    public const string ExtractScript = """
        (function () {
            const sets = [...document.querySelectorAll('fieldset')];
            const of = (re) => sets.find(f => re.test(f.querySelector('legend')?.textContent || ''));

            const result = of(/Wynik/i);
            const checks = of(/metody sprawdzenia/i);
            // The page carries its copy-to-clipboard code inline, and textContent would drag the
            // whole of it into anything read off a fieldset.
            const text = (el) => {
                if (!el) return '';
                const clone = el.cloneNode(true);
                clone.querySelectorAll('script, button, legend').forEach(e => e.remove());
                return (clone.textContent || '').replace(/ /g, ' ').replace(/\s+/g, ' ').trim();
            };

            if (!result) {
                return JSON.stringify({
                    valid: false,
                    message: text(document.querySelector('.tx-valiban-pi1')).slice(0, 300)
                });
            }

            const paragraphs = [...result.querySelectorAll('p')];
            const labelled = (label) => paragraphs.find(p => {
                const b = p.querySelector('b');
                return b && b.textContent.trim().toLowerCase().startsWith(label);
            });

            // The value of a "<b>Label:</b> value" paragraph, with the label taken off.
            const valueOf = (p) => {
                if (!p) return '';
                const clone = p.cloneNode(true);
                clone.querySelectorAll('b, button, script').forEach(e => e.remove());
                return (clone.textContent || '').replace(/ /g, ' ').trim();
            };

            const bicParagraph = labelled('bic');
            const bicText = valueOf(bicParagraph);
            const bic = (bicText.match(/\b[A-Z]{4}[A-Z]{2}[A-Z0-9]{2}([A-Z0-9]{3})?\b/) || [''])[0];

            const namePara = labelled('bank');
            const name = valueOf(namePara);

            // The address is the paragraph straight after the bank's name and carries no label.
            let street = '', postalCode = '', city = '';
            if (namePara) {
                const next = namePara.nextElementSibling;
                if (next && next.tagName === 'P' && !next.querySelector('b')) {
                    const lines = next.innerHTML.split(/<br\s*\/?>/i)
                        .map(h => { const d = document.createElement('div'); d.innerHTML = h;
                                    return (d.textContent || '').replace(/ /g, ' ').trim(); })
                        .filter(l => l.length > 0);

                    if (lines.length > 0) {
                        const town = lines[lines.length - 1];
                        street = lines.slice(0, -1).join(', ');

                        const m = town.match(/^(\S*\d\S*)\s+(.*)$/);
                        if (m) { postalCode = m[1]; city = m[2]; } else { city = town; }
                    }
                }
            }

            const code = (text(checks).match(/Krajowy kod banku\s+([^:]+):/) || ['', ''])[1].trim();
            const valid = /prawid[łl]owy IBAN/i.test(text(result));

            return JSON.stringify({
                valid: valid,
                bankCode: code,
                bic: bic,
                name: name,
                street: street,
                postalCode: postalCode,
                city: city,
                message: valid ? '' : text(result).slice(0, 300)
            });
        })();
        """;

    /// <summary>Turns what the script returned into a result, tolerating anything unexpected.</summary>
    public static IbanLookupResult Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return IbanLookupResult.Nothing;

        try
        {
            // ExecuteScriptAsync hands back a JSON value, so our JSON string arrives quoted and
            // escaped - it has to be unwrapped before it can be read as an object.
            var text = json.TrimStart().StartsWith('"')
                ? JsonSerializer.Deserialize<string>(json)
                : json;

            if (string.IsNullOrWhiteSpace(text)) return IbanLookupResult.Nothing;

            var raw = JsonSerializer.Deserialize<RawLookup>(text);
            if (raw is null) return IbanLookupResult.Nothing;

            return new IbanLookupResult(
                raw.Valid,
                Clean(raw.BankCode), Clean(raw.Bic), Clean(raw.Name),
                Clean(raw.Street), Clean(raw.PostalCode), Clean(raw.City),
                Clean(raw.Message));
        }
        catch (JsonException)
        {
            return IbanLookupResult.Nothing;
        }
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;

    private sealed record RawLookup(
        [property: JsonPropertyName("valid")] bool Valid,
        [property: JsonPropertyName("bankCode")] string? BankCode,
        [property: JsonPropertyName("bic")] string? Bic,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("street")] string? Street,
        [property: JsonPropertyName("postalCode")] string? PostalCode,
        [property: JsonPropertyName("city")] string? City,
        [property: JsonPropertyName("message")] string? Message);
}
