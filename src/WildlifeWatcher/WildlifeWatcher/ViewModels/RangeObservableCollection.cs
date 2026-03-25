using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace WildlifeWatcher.ViewModels;

/// <summary>
/// ObservableCollection that supports bulk operations while firing only a single
/// CollectionChanged notification per operation. Prevents O(n²) WrapPanel layout
/// passes when populating large lists one item at a time.
/// </summary>
public class RangeObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>
    /// Appends all items, firing one Add notification per item. WPF's ItemsControl does not
    /// support range (multi-item) Add notifications and throws NotSupportedException for them.
    /// WPF coalesces layout invalidations within a dispatcher frame, so this produces a single
    /// layout pass even though notifications are sent individually.
    /// </summary>
    public void AddRange(IEnumerable<T> items)
    {
        foreach (var item in items)
            Add(item);
    }

    /// <summary>
    /// Replaces all contents and fires a single Reset notification.
    /// </summary>
    public void ResetWith(IEnumerable<T> newItems)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in newItems)
            Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
