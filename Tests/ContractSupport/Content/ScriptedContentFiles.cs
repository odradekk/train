// 内容文件端口的有限情景替身；按路径和字节上限回放配置好的请求和结果。
// 不实现路径安全、实际读取或内容规则。
using System.Collections.Immutable;
using Train.Content.Contracts;

namespace Train.Content.ContractSupport;

/// <summary>每项配置只使用一次，调用顺序不限；同一路径的不同字节上限互不约束，可表示同一文件的超限与成功读取，也可表示两次读取之间文件变化；成功字节不得超出该项上限。</summary>
public sealed class ScriptedContentFiles : IContentFiles
{
    public sealed record ReadCall(ContentPath Path, long MaximumBytes,
        ContentIoResult<ImmutableArray<byte>> Result);

    private readonly Dictionary<(ContentPath Path, long MaximumBytes), ContentIoResult<ImmutableArray<byte>>> _reads = new();

    public ScriptedContentFiles(params ReadCall[] reads)
    {
        foreach (var read in reads)
        {
            if (read.Result is ContentIoResult<ImmutableArray<byte>>.Success success &&
                success.Value.Length > read.MaximumBytes)
                throw new ArgumentException("成功结果不能超过配置的字节上限。", nameof(reads));
            if (!_reads.TryAdd((read.Path, read.MaximumBytes), read.Result))
                throw new ArgumentException("重复配置相同的路径和字节上限。", nameof(reads));
        }
    }

    public ContentIoResult<ImmutableArray<ContentFileEntry>> Enumerate(ContentPath directory) =>
        throw new InvalidOperationException("UNCONFIGURED: 目录枚举未配置。");

    public ContentIoResult<ImmutableArray<byte>> Read(ContentPath file, long maximumBytes)
    {
        if (!_reads.Remove((file, maximumBytes), out var result))
            throw new InvalidOperationException("UNCONFIGURED: 路径和字节上限未配置，或配置已使用。");
        return result;
    }

    public void AssertComplete()
    {
        if (_reads.Count != 0)
            throw new InvalidOperationException("INCOMPLETE: 仍有配置的读取未发生。");
    }
}
