using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace PCActivityTracker.ViewModels;

/// <summary>
/// Replaces a report collection with one Reset notification. Updating thousands
/// of timeline rows one-by-one otherwise causes repeated WPF layout and allocation.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceWith(IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        CheckReentrancy();
        Items.Clear();
        foreach (var value in values) Items.Add(value);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
