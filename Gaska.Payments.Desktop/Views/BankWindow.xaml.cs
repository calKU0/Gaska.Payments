using System.Windows.Controls;
using System.Windows;
using Gaska.Payments.Desktop.Data;
using Gaska.Payments.Desktop.Mvvm;

namespace Gaska.Payments.Desktop.Views;

/// <summary>
/// Asks for the details of a bank Comarch ERP XL will not tie to the account by itself.
/// </summary>
/// <remarks>
/// It appears in two situations: the bank is absent from the register, or it is there but its
/// card holds a clearing code XL will not find inside the account number (the rule is described at
/// <see cref="IbanParts"/>). The only field filled in for the operator is the bank code - that
/// follows directly from the account number and can be verified. The name and SWIFT code are not
/// suggested from the register: taken off somebody else's account they turned out to be the name
/// of a branch, or of an entirely different bank, and one click confirmed them. BNP sends no bank
/// details in any message.
/// </remarks>
public partial class BankWindow : Window
{
    private readonly string _account;

    private BankWindow(BankPrompt prompt)
    {
        InitializeComponent();

        _account = prompt.Account;
        var known = prompt.Known;

        AccountBox.Text = prompt.Account;

        // The code from the IBAN registry decides; the number off the card is used only when
        // the layout of numbers in that country is unknown to us and the card happens to hold
        // something sensible.
        BankCodeBox.Text = prompt.BankCode.Length > 0
            ? prompt.BankCode
            : known is { BankCode.Length: > 0 } && Fits(known.BankCode) ? known.BankCode : string.Empty;

        BicBox.Text = known?.Bic ?? string.Empty;
        NameBox.Text = known?.Name ?? string.Empty;
        StreetBox.Text = known?.Street ?? string.Empty;
        CityBox.Text = known?.City ?? string.Empty;
        PostalCodeBox.Text = known?.PostalCode ?? string.Empty;

        CountryBox.Text = known is { CountryCode.Length: 2 }
            ? known.CountryCode
            : IbanParts.Country(prompt.Account);

        if (prompt.RepairsExistingCard)
        {
            TitleText.Text = "Karta banku do poprawienia";
            IntroText.Text =
                $"W kartotece jest bank {Describe(known)}, ale Comarch ERP XL nie zwiąże go z tym " +
                "rachunkiem: jego numer rozliczeniowy nie pasuje do numeru IBAN. Poprawimy tę " +
                "kartę zamiast zakładać drugą.";
            ConfirmButton.Content = "Popraw kartę banku";
        }
        else
        {
            TitleText.Text = "Bank spoza kartoteki";
            IntroText.Text =
                "Tego banku nie ma w kartotece ERP, a wyciąg z BNP nie zawiera jego danych. " +
                "Wypełniony jest tylko kod banku – wyliczony z numeru rachunku. Nazwę i SWIFT " +
                "wpisz z dokumentu, który masz przed sobą.";
            ConfirmButton.Content = "Załóż bank";
        }

        SourceText.Text = prompt.BankCode.Length > 0
            ? "Kod banku wyliczyliśmy z numeru rachunku – w każdym kraju stoi w tym samym miejscu. " +
              "Po nim, i tylko po nim, ERP XL wiąże bank z rachunkiem."
            : $"Nie znamy układu numeru rachunku w kraju {IbanParts.Country(prompt.Account)}, " +
              "więc kod banku trzeba wskazać samemu – to ta część numeru, która opisuje bank.";

        // The window sizes itself to its content and cannot be resized, so on a small screen -
        // or a normal one at 150% scaling - it would simply be taller than the desktop, with the
        // buttons under the edge and no way to reach them. Capped to the screen; the content
        // scrolls inside whatever is left.
        SourceInitialized += (_, _) =>
            MaxHeight = Math.Max(320, ScreenArea.WorkAreaFor(this).Height - (2 * ScreenArea.Gap));

        Loaded += (_, _) =>
        {
            if (BankCodeBox.Text.Length == 0) BankCodeBox.Focus();
            else NameBox.Focus();
        };
    }

    /// <summary>The details collected, or null when the operator gave up.</summary>
    public BankDetails? Result { get; private set; }

    /// <summary>Shows the window and returns the bank details, or null.</summary>
    public static BankDetails? Ask(Window? owner, BankPrompt prompt)
    {
        var window = new BankWindow(prompt);
        if (owner is not null && owner.IsLoaded) window.Owner = owner;

        return window.ShowDialog() == true ? window.Result : null;
    }

    private static string Describe(BankDetails? bank) =>
        bank is null ? "z kartoteki" : bank.Name.Length > 0 ? $"„{bank.Name}”" : bank.Bic;

    /// <summary>
    /// Whether the code typed in sits in the account number where a bank code belongs.
    /// </summary>
    /// <remarks>
    /// "It occurs somewhere in the number" is not enough: the operator could type a fragment from
    /// the middle, validation would let it through, and XL still would not match such a card. We
    /// check the position from the IBAN registry - for Italy and San Marino that is the second
    /// position of the BBAN, for the rest the first. Where a country's layout is unknown we
    /// require the start of the number.
    /// </remarks>
    private bool Fits(string code)
    {
        if (code.Length == 0) return false;

        var bban = IbanParts.Bban(_account);
        var offset = IbanParts.Layout(_account)?.Offset ?? 0;

        return bban.Length >= offset + code.Length
            && string.Compare(bban, offset, code, 0, code.Length, StringComparison.OrdinalIgnoreCase) == 0;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        var code = BankCodeBox.Text.Trim();

        // Without this the card will be created but XL will still leave the account with no
        // bank - which is the whole point.
        if (code.Length < 2 || !Fits(code))
        {
            Fail("Kod banku musi stać na początku numeru rachunku, licząc po cyfrach kontrolnych.", BankCodeBox);
            return;
        }

        var bic = BicBox.Text.Trim();

        if (bic.Length is not (0 or 8 or 11))
        {
            Fail("SWIFT ma osiem albo jedenaście znaków. Można go też zostawić pusty.", BicBox);
            return;
        }

        Result = new BankDetails(
            bic,
            NameBox.Text.Trim(),
            StreetBox.Text.Trim(),
            CityBox.Text.Trim(),
            PostalCodeBox.Text.Trim(),
            CountryBox.Text.Trim(),
            code);

        DialogResult = true;
    }

    /// <summary>
    /// Fetches what the public IBAN register knows about this bank and fills the fields with it.
    /// </summary>
    /// <remarks>
    /// The page stays open beside the form. Nothing here is taken on trust: the operator reads the
    /// page the fields came from and corrects whatever the register got wrong before confirming.
    /// </remarks>
    private async void OnLookUp(object sender, RoutedEventArgs e)
    {
        var account = AccountBox.Text.Trim();

        if (account.Length < 10)
        {
            Fail("Bez numeru rachunku nie ma czego sprawdzić.", BankCodeBox);
            return;
        }

        ErrorText.Visibility = Visibility.Collapsed;
        LookupButton.IsEnabled = false;
        LookupButton.Content = "Sprawdzam…";

        try
        {
            var opening = _preview is null;
            _preview ??= NewPreview();

            // Laid out before it is shown, so the pair does not jump into place in front of the
            // operator. Only on opening - a window they have since moved stays where they put it.
            if (opening) ArrangeSideBySide(_preview);

            _preview.Show();
            _preview.Activate();

            var found = await _preview.LookUpAsync(account);

            if (!found.HasAnything)
            {
                Fail(found.Message.Length > 0
                        ? found.Message
                        : "Strona nie podaje danych tego banku – wpisz je z dokumentu.",
                    NameBox);
                return;
            }

            Fill(found);
        }
        catch (Exception exception)
        {
            Fail($"Nie udało się pobrać danych banku: {exception.Message}", NameBox);
        }
        finally
        {
            LookupButton.IsEnabled = true;
            LookupButton.Content = "Pobierz dane z sieci";
        }
    }

    private IbanPreviewWindow? _preview;

    /// <summary>
    /// The preview, owned by this window so that closing the form closes it as well.
    /// </summary>
    private IbanPreviewWindow NewPreview()
    {
        var preview = new IbanPreviewWindow { Owner = this };
        preview.Closed += (_, _) => _preview = null;

        return preview;
    }

    /// <summary>
    /// Puts the form on the left and the page on the right, side by side, neither covering the
    /// other.
    /// </summary>
    /// <remarks>
    /// The form opens in the middle of the screen, so a page placed beside it would hang off the
    /// edge or sit on top of it. The two are measured together and centred as a pair, on the
    /// screen the form is actually on.
    ///
    /// Done once, when the page is first opened. Whatever the operator drags afterwards stays
    /// where they dragged it.
    /// </remarks>
    private void ArrangeSideBySide(Window preview)
    {
        var (form, page) = ScreenArea.PairSideBySide(
            ScreenArea.WorkAreaFor(this),
            new Size(ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height),
            new Size(preview.Width, preview.Height));

        // The form sizes itself to its content, so only its position is set.
        Left = form.Left;
        Top = form.Top;

        preview.Left = page.Left;
        preview.Top = page.Top;
        preview.Width = page.Width;
        preview.Height = page.Height;
    }

    /// <summary>
    /// Writes into the form what the register returned, leaving anything it does not know alone.
    /// </summary>
    /// <remarks>
    /// The bank code is the exception and is taken only when it stands where a bank code belongs
    /// in this account number. It is the one field ERP matches on, our own value comes from the
    /// IBAN registry, and a register that counts the digits differently for some country must not
    /// be allowed to overwrite it - a card created under a code XL will not find is a card that
    /// exists and does nothing.
    /// </remarks>
    private void Fill(IbanLookupResult found)
    {
        if (found.Bic.Length > 0) BicBox.Text = found.Bic;
        if (found.Name.Length > 0) NameBox.Text = found.Name;
        if (found.Street.Length > 0) StreetBox.Text = found.Street;
        if (found.City.Length > 0) CityBox.Text = found.City;
        if (found.PostalCode.Length > 0) PostalCodeBox.Text = found.PostalCode;

        if (BankCodeBox.Text.Trim().Length == 0 && found.BankCode.Length > 0 && Fits(found.BankCode))
        {
            BankCodeBox.Text = found.BankCode;
        }

        var ignored = found.BankCode.Length > 0
                      && !string.Equals(found.BankCode, BankCodeBox.Text.Trim(), StringComparison.OrdinalIgnoreCase);

        SourceText.Text = ignored
            ? $"Dane z ibancalculator.com. Kod banku ze strony ({found.BankCode}) różni się od "
              + $"wyliczonego z numeru ({BankCodeBox.Text.Trim()}) – zostawiam wyliczony, bo to po nim "
              + "ERP XL wiąże bank z rachunkiem. Porównaj resztę z otwartą stroną."
            : "Dane z ibancalculator.com – porównaj je z otwartą obok stroną i popraw, jeśli trzeba.";
    }

    private void Fail(string message, Control focus)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        focus.Focus();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
