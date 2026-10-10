using System.Collections.ObjectModel;

namespace ALyricEase.Infrastructure;

/// <summary>按现有对象身份同步有序投影；未变化的项不通知、不重建视图容器。</summary>
internal static class CollectionSync
{
    public static void Apply<T>(ObservableCollection<T> target, IReadOnlyList<T> desired) where T : class
    {
        var retained = new HashSet<T>(desired, ReferenceEqualityComparer.Instance);
        for (var i = target.Count - 1; i >= 0; i--)
            if (!retained.Contains(target[i])) target.RemoveAt(i);
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i])) continue;
            var oldIndex = target.IndexOf(desired[i]);
            if (oldIndex >= 0) target.Move(oldIndex, i);
            else target.Insert(i, desired[i]);
        }
    }
}
