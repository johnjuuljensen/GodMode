using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace GodMode.Maui.Collections;

/// <summary>
/// ObservableCollection with AddRange support for bulk operations.
/// Raises a single Reset notification instead of one per item.
/// </summary>
public class ObservableRangeCollection<T> : ObservableCollection<T>
{
    /// <summary>
    /// Adds a range of items and raises a single collection changed notification.
    /// </summary>
    public void AddRange(IEnumerable<T> items)
    {
        var itemList = items as IList<T> ?? items.ToList();
        if (itemList.Count == 0) return;

        CheckReentrancy();

        foreach (var item in itemList)
        {
            Items.Add(item);
        }

        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
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

        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
