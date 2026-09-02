using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Gaska.Payments.Desktop.Mvvm;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Sets the field and raises a change only when the value really changed.</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;

        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>
/// A delegate-backed command - as much MVVM as this application needs.
/// </summary>
/// <remarks>
/// The re-evaluation is left to WPF's own <see cref="CommandManager"/>. It used to be raised by
/// hand from one place in the view model, which meant a command left off that list was evaluated
/// once - when its button was first bound, with nothing selected - and stayed disabled for the rest
/// of the session with nothing to show for it. The button that opens the source document spent its
/// whole life that way.
///
/// The cost is that every predicate here runs on each round of <c>RequerySuggested</c>, which WPF
/// raises on keyboard and mouse activity. They are all comparisons on a handful of fields, so that
/// is cheaper than the class of bug it removes.
/// </remarks>
public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);

    /// <summary>Asks WPF to re-evaluate now, rather than waiting for the next round.</summary>
    public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
}
