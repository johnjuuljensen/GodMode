using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace GodMode.Maui.Collections;

/// <summary>
/// ObservableCollection with AddRange support for bulk operations.
/// Uses individual Add for small batches (smooth UI) and Reset for large batches (performance).
/// </summary>
public class ObservableRangeCollection<T> : ObservableCollection<T>
{
    // Threshold for switching between individual Add and bulk Reset
    private const int BulkThreshold = 5;

    /// <summary>
    /// Adds a range of items intelligently:
    /// - Small batches (≤5): individual Add notifications for smooth UI
    /// - Large batches (>5): single Reset notification for performance
    /// </summary>
    public void AddRange(IEnumerable<T> items)
    {
        var itemList = items as IList<T> ?? items.ToList();
        if (itemList.Count == 0) return;

        if (itemList.Count <= BulkThreshold)
        {
            // Small batch - individual adds are smooth
            foreach (var item in itemList)
            {
                Add(item);
            }
        }
        else
        {
            // Large batch - use Reset to avoid UI thrashing
            CheckReentrancy();
            foreach (var item in itemList)
            {
                Items.Add(item);
            }
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    /// <summary>
    /// Replaces all items with new items, raising a single notification.
    /// </summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();

        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
