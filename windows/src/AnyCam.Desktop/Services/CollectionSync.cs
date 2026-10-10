using System.Collections.ObjectModel;

namespace AnyCam.Desktop.Services;

public static class CollectionSync
{
    /// <summary>
    /// Makes <paramref name="target"/> equal to <paramref name="desired"/> with in-place removes and inserts,
    /// so a bound ComboBox keeps its selection when the selected item is still present.
    /// </summary>
    public static void SyncTo<T>(this ObservableCollection<T> target, IReadOnlyList<T> desired)
    {
        var comparer = EqualityComparer<T>.Default;
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(target[i], comparer))
                target.RemoveAt(i);
        }
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && comparer.Equals(target[i], desired[i]))
                continue;
            var existing = -1;
            for (var j = i + 1; j < target.Count; j++)
            {
                if (comparer.Equals(target[j], desired[i]))
                {
                    existing = j;
                    break;
                }
            }
            if (existing >= 0)
                target.Move(existing, i);
            else
                target.Insert(i, desired[i]);
        }
    }
}
