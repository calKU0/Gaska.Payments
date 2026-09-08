using System.IO;
using System.Windows;
using Gaska.Payments.Desktop.Data;
using Microsoft.Web.WebView2.Core;

namespace Gaska.Payments.Desktop.Views;

/// <summary>
/// The ibancalculator.com page for one account number, shown beside the bank form.
/// </summary>
/// <remarks>
/// It is the operator's evidence: the fields are filled from this very page, and it stays open so
/// that they can read it and confirm what was copied across. It belongs to the bank form, so
/// closing the form closes it too; the operator may also close it on their own at any time.
/// </remarks>
public partial class IbanPreviewWindow : Window
{
    private TaskCompletionSource<bool>? _loaded;

    public IbanPreviewWindow()
    {
        InitializeComponent();
        Browser.NavigationCompleted += OnNavigationCompleted;
    }

    /// <summary>
    /// Loads the page for the account number and reads the bank details off it.
    /// </summary>
    /// <remarks>
    /// The browser is prepared with a user data folder of its own under the user's profile.
    /// WebView2 puts it next to the executable by default, and the application is run from a
    /// share where nobody may write.
    /// </remarks>
    public async Task<IbanLookupResult> LookUpAsync(string account)
    {
        StatusText.Text = "Otwieram stronę i sprawdzam numer rachunku…";

        try
        {
            if (Browser.CoreWebView2 is null)
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Gaska.Payments", "WebView2");

                Directory.CreateDirectory(folder);

                var environment = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null, userDataFolder: folder);

                await Browser.EnsureCoreWebView2Async(environment);
                BlockEverythingButTheSite(Browser.CoreWebView2!);
            }
        }
        catch (Exception exception)
        {
            // The Evergreen runtime is missing or will not start. The form still works - this
            // was only ever a way to save typing.
            StatusText.Text = $"Nie udało się uruchomić podglądu strony: {exception.Message}";
            return IbanLookupResult.Nothing with
            {
                Message = "Podgląd strony jest niedostępny (brak środowiska Microsoft Edge WebView2). "
                        + "Dane banku wpisz ręcznie.",
            };
        }

        var browser = Browser.CoreWebView2;

        if (browser is null)
        {
            return IbanLookupResult.Nothing with { Message = "Podgląd strony się nie uruchomił." };
        }

        _loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.Navigate(IbanLookup.Url(account));

        // A page that never finishes loading must not leave the form waiting for ever.
        var finished = await Task.WhenAny(_loaded.Task, Task.Delay(TimeSpan.FromSeconds(30)));

        if (finished != _loaded.Task)
        {
            StatusText.Text = "Strona nie odpowiedziała w ciągu 30 sekund – dane wpisz ręcznie.";
            return IbanLookupResult.Nothing with { Message = "Strona nie odpowiedziała w ciągu 30 sekund." };
        }

        var json = await browser.ExecuteScriptAsync(IbanLookup.ExtractScript);
        var result = IbanLookup.Parse(json);

        StatusText.Text = result switch
        {
            { Valid: true, HasAnything: true } =>
                "Dane przepisane do formularza. Porównaj je z tą stroną i popraw, jeśli trzeba.",
            { Valid: true } =>
                "Numer rachunku jest prawidłowy, ale strona nie podaje danych tego banku.",
            _ => $"Strona nie potwierdziła numeru rachunku. {result.Message}".Trim(),
        };

        return result;
    }

    /// <summary>
    /// The hosts this window is allowed to fetch from: the site itself and the CDN its stylesheet
    /// and images come from.
    /// </summary>
    private static readonly string[] Allowed = ["pl.ibancalculator.com", ".kxcdn.com"];

    /// <summary>
    /// Lets the window load the page and nothing else.
    /// </summary>
    /// <remarks>
    /// This is what takes the cookie notice away, and it takes it away without answering it. The
    /// notice is Sourcepoint's, drawn in an iframe from <c>cdn.privacy-mgmt.com</c>; with that host
    /// unreachable the consent script never runs and there is nothing to dismiss. Clicking
    /// "accept" would have been the other way to silence it, and a worse one: it would record a
    /// consent to tracking in the company's name that nobody asked anybody for.
    ///
    /// The same rule keeps the rest of the third parties out - an ad SDK, a prebid host and two
    /// trackers. None of them has any business running inside an accounting application, and the
    /// page we came for is server-rendered HTML that does not need a line of their JavaScript.
    /// It does mean the site's advertising does not load; the window opens a few times a day, when
    /// an accountant meets a bank ERP does not know.
    ///
    /// An allow-list rather than a list of things to block, because a new tracker on the page
    /// would slip past a block-list silently. If the site moves its CDN the page loses its styling
    /// and nothing else - the details are read from the structure of the HTML, not from how it
    /// looks.
    /// </remarks>
    private static void BlockEverythingButTheSite(CoreWebView2 browser)
    {
        browser.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);

        browser.WebResourceRequested += (_, e) =>
        {
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri)) return;

            var host = uri.Host;
            var wanted = Allowed.Any(a => a.StartsWith('.')
                ? host.EndsWith(a, StringComparison.OrdinalIgnoreCase)
                : host.Equals(a, StringComparison.OrdinalIgnoreCase));

            if (wanted) return;

            e.Response = browser.Environment.CreateWebResourceResponse(
                Content: null, StatusCode: 204, ReasonPhrase: "Blocked", Headers: string.Empty);
        };
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e) =>
        _loaded?.TrySetResult(e.IsSuccess);
}
