using System.Windows.Controls;
using System.Windows;
using Gaska.Payments.Desktop.Data;

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
            CityBox.Text.Trim(),
            PostalCodeBox.Text.Trim(),
            CountryBox.Text.Trim(),
            code);

        DialogResult = true;
    }

    private void Fail(string message, Control focus)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        focus.Focus();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
