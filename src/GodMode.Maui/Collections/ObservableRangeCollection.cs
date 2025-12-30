using System.Collections.ObjectModel;

namespace GodMode.Maui.Collections;

/// <summary>
/// ObservableCollection with AddRange support.
/// </summary>
public class ObservableRangeCollection<T> : ObservableCollection<T>
{
    /// <summary>
    /// Adds a range of items using individual Add notifications.
    /// </summary>
    public void AddRange(IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            Add(item);
        }
    }
}
