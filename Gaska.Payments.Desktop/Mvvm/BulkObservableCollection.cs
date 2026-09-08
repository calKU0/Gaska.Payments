using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Gaska.Payments.Desktop.Mvvm;

/// <summary>
/// An observable collection that can be refilled in one go.
/// </summary>
/// <remarks>
/// Adding rows one at a time is what made the queue slow to open. Every <c>Add</c> raises a change
/// of its own, and the view bound to the collection answers each one by running its filter over
/// everything added so far and telling the grid about it - quadratic work over eighteen thousand
/// rows, which is the sixty days the window opens on.
///
/// <c>DeferRefresh</c> is not the answer, however much it looks like it:
/// <see cref="System.Windows.Data.ListCollectionView"/> refuses to be told about a change while a
/// refresh is deferred and throws instead. One reset is: the view re-reads the source once, and the
/// grid rebuilds once.
/// </remarks>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Replaces everything in the collection, announcing it as a single change.</summary>
    public void Reset(IEnumerable<T> items)
    {
        CheckReentrancy();

        Items.Clear();
        foreach (var item in items) Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
