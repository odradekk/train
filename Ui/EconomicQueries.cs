namespace Train.Ui;

internal sealed class EconomicQueries(Action<string> report)
{
    internal string? MissingEntry { get; private set; }

    internal bool TryRead<T>(string entry, Func<T> read, out T value)
    {
        value = default!;
        if (MissingEntry is not null) return false;
        try { value = read(); return true; }
        // FindSettlement、Economy、QuoteTrade、QuoteVehicle 实现后删除；不能用替代数据掩盖缺失实现。
        catch (NotImplementedException)
        {
            MissingEntry = entry;
            report($"核心尚未提供：{entry}");
            return false;
        }
    }
}
