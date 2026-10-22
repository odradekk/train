namespace Train.Ui;

// 两组行之间变化的连续区间：相同的开头和结尾保留，只替换中间。
internal readonly record struct RowChange(int Start, int Removed, int Inserted)
{
    internal static RowChange Between<T>(IReadOnlyList<T> old, IReadOnlyList<T> next) where T : IEquatable<T>
    {
        var limit = Math.Min(old.Count, next.Count);
        var start = 0;
        while (start < limit && old[start].Equals(next[start])) start++;
        var end = 0;
        while (end < limit - start && old[old.Count - 1 - end].Equals(next[next.Count - 1 - end])) end++;
        return new(start, old.Count - start - end, next.Count - start - end);
    }
}
