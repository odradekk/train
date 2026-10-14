namespace Train.Ui;

internal sealed class EconomicQueries(Action<string> report)
{
    internal string? MissingEntry { get; private set; }

    internal bool TryRead<T>(string entry, Func<T> read, out T value)
    {
        value = default!;
        if (MissingEntry is not null) return false;
        try { value = read(); return true; }
        // #325、#384 提供查询后删除；缺少生产实现不能变成空库存或零报价。
        catch (NotImplementedException)
        {
            MissingEntry = entry;
            report($"核心尚未提供：{entry}");
            return false;
        }
    }
}
