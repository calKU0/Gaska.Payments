using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows;

namespace Gaska.Payments.Desktop.Mvvm;

/// <summary>
/// Makes a <see cref="Popup"/> driven by a <see cref="ToggleButton"/> behave the way a combo box
/// does: clicking the button while the list is open closes it.
/// </summary>
/// <remarks>
/// With <c>StaysOpen="False"</c> the popup closes itself on any mouse press outside, the button
/// included - and then the button, which is still about to process that very click, toggles itself
/// back on and the list reopens. To the user the second click does nothing.
///
/// The fix is the one a combo box uses: while the list is open the mouse is captured, so a press
/// anywhere outside reaches us instead of the button. We close the list and the button never sees
/// the click, so it cannot reopen it. Presses inside the list still work normally, because the
/// capture covers the whole subtree.
///
/// The capture has to be held until the press has finished routing. The outside-press handler runs
/// on a preview of that press, so giving the capture back inside it hands the very same press to
/// the button, which checks itself again - the symptom the capture was there to prevent. It is
/// released when the popup closes instead.
///
/// The capture goes on the popup's <see cref="Popup.Child"/>, not on the popup itself: a popup is
/// only a placeholder in its parent's tree and its content lives in a window of its own, so
/// <c>Mouse.Capture</c> on the popup returns false and nothing would be captured at all.
/// </remarks>
public static class DropdownPopup
{
    /// <summary>The button that opens the popup. Setting it wires the two together.</summary>
    public static readonly DependencyProperty ToggleProperty =
        DependencyProperty.RegisterAttached(
            "Toggle", typeof(ToggleButton), typeof(DropdownPopup),
            new PropertyMetadata(null, OnToggleChanged));

    public static ToggleButton? GetToggle(DependencyObject element) =>
        (ToggleButton?)element.GetValue(ToggleProperty);

    public static void SetToggle(DependencyObject element, ToggleButton? value) =>
        element.SetValue(ToggleProperty, value);

    private static void OnToggleChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Popup popup) return;

        popup.Opened -= OnOpened;
        popup.Closed -= OnClosed;

        if (e.NewValue is null) return;

        popup.Opened += OnOpened;
        popup.Closed += OnClosed;
    }

    private static void OnOpened(object? sender, EventArgs e)
    {
        if (sender is not Popup { Child: UIElement content } popup) return;

        // The child carries the toggle too, so the handler does not have to walk any tree to
        // find it. Setting it on a non-popup is a no-op, so this does not recurse.
        SetToggle(content, GetToggle(popup));

        Mouse.AddPreviewMouseDownOutsideCapturedElementHandler(content, OnPressedOutside);

        if (!Mouse.Capture(content, CaptureMode.SubTree))
        {
            // Nothing captured means no outside press would reach us, and the list could only be
            // closed from the button - which is the very thing that does not work. Better to give
            // the popup its own closing back than to leave a list that will not go away.
            Mouse.RemovePreviewMouseDownOutsideCapturedElementHandler(content, OnPressedOutside);
            popup.StaysOpen = false;
        }
    }

    private static void OnClosed(object? sender, EventArgs e)
    {
        if (sender is not Popup { Child: UIElement content }) return;

        Mouse.RemovePreviewMouseDownOutsideCapturedElementHandler(content, OnPressedOutside);
        Release(content);
    }

    /// <summary>Gives the capture back, but only if it is still ours.</summary>
    private static void Release(UIElement content)
    {
        if (ReferenceEquals(Mouse.Captured, content)) Mouse.Capture(null);
    }

    /// <summary>
    /// A press outside the open list closes it - on the button that opened it as well as anywhere
    /// else on the window.
    /// </summary>
    /// <remarks>
    /// The button is unchecked rather than the popup being closed directly: <c>IsOpen</c> is bound
    /// to the button, and assigning to it would replace that binding with a local value and the
    /// list would never open again.
    /// </remarks>
    private static void OnPressedOutside(object sender, MouseButtonEventArgs e)
    {
        if (sender is not UIElement content) return;

        // The capture is deliberately NOT released here. Releasing it first was the whole bug:
        // this notification is only a preview of a press that has not been routed yet, so with
        // the capture gone the press went on to the toggle button, which checked itself again and
        // the list reopened - the second click looked like it did nothing. While the capture is
        // still ours the press has nowhere else to go. Closed gives it back a moment later.
        if (GetToggle(content) is { } toggle) toggle.IsChecked = false;
    }
}
