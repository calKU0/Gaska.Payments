using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Gaska.Payments.Desktop.Mvvm;

/// <summary>
/// The search box that lives inside a drop-down list, above the entries.
/// </summary>
/// <remarks>
/// A box of its own standing above the closed drop-down was the previous answer, and it cost a row
/// of the panel for each of the two pickers. Putting it inside the list gives the room back and
/// reads the way people expect a long list to behave.
///
/// It is not <c>IsEditable</c> on the combo box. That one mixes the text being typed with the value
/// selected - the closed field shows whatever was last typed rather than what is chosen, and the
/// selection is dropped whenever the list underneath changes, which here it does on every
/// keystroke. A separate box inside the popup keeps the two apart: the closed field always shows
/// the chosen entry, the box only ever narrows the list.
///
/// The attached property carries the text to and from the view model, so the template stays free of
/// any particular property name and both pickers share it.
/// </remarks>
public static class PickerSearch
{
    /// <summary>Name the template has to give its search box for this to find it.</summary>
    public const string BoxName = "PART_Search";

    /// <summary>What is typed in the box - bound two-way to whatever the view model narrows by.</summary>
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text", typeof(string), typeof(PickerSearch),
            new FrameworkPropertyMetadata(
                string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnTextChanged));

    public static string GetText(DependencyObject element) => (string)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);

    /// <summary>Whether the two handlers below are already on this combo box.</summary>
    private static readonly DependencyProperty WiredProperty =
        DependencyProperty.RegisterAttached(
            "Wired", typeof(bool), typeof(PickerSearch), new PropertyMetadata(false));

    private static void OnTextChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not ComboBox combo || (bool)combo.GetValue(WiredProperty)) return;

        combo.SetValue(WiredProperty, true);
        combo.DropDownOpened += OnOpened;
        combo.DropDownClosed += OnClosed;
    }

    /// <summary>
    /// Opening the list puts the caret in the box, so narrowing a long list is one gesture.
    /// </summary>
    /// <remarks>
    /// After a dispatcher pass, not straight away: the popup's content is built when it opens, so
    /// the box does not exist yet at the moment the event is raised, and focusing it before the
    /// combo box has finished taking focus itself loses the caret again.
    /// </remarks>
    private static void OnOpened(object? sender, EventArgs e)
    {
        if (sender is not ComboBox combo) return;

        combo.Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () =>
            {
                if (Box(combo) is not { } box) return;

                box.Focus();
                box.SelectAll();
            });
    }

    /// <summary>
    /// Closing it empties the box, so the list is whole again the next time it is opened.
    /// </summary>
    /// <remarks>
    /// The entry that was chosen stays chosen: both pickers keep whatever is selected in their
    /// list whatever the filter says, which is what makes emptying it safe here.
    /// </remarks>
    private static void OnClosed(object? sender, EventArgs e)
    {
        if (sender is ComboBox combo && GetText(combo).Length > 0) SetText(combo, string.Empty);
    }

    private static TextBox? Box(ComboBox combo) =>
        combo.Template?.FindName(BoxName, combo) as TextBox;
}
