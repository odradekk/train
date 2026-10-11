// 内容文件端口的有限替身；只回放配置好的请求和结果。
// 不实现路径安全、实际读取或内容规则。
using System.Collections.Immutable;
using Train.Content.Contracts;

namespace Train.ContractSupport.Tests.Content;

/// <summary>仅回放明确配置的读取，不执行 I/O、路径规则或超限计算。</summary>
public sealed class ScriptedContentFiles(params ScriptedContentFiles.ReadCall[] reads) : IContentFiles
{
    public sealed record ReadCall(ContentPath Path, long MaximumBytes,
        ContentIoResult<ImmutableArray<byte>> Result);

    private readonly ReadCall[] _reads = (ReadCall[])reads.Clone();
    private int _next;

    public ContentIoResult<ImmutableArray<ContentFileEntry>> Enumerate(ContentPath directory) =>
        throw new InvalidOperationException("未配置目录枚举。");

    public ContentIoResult<ImmutableArray<byte>> Read(ContentPath file, long maximumBytes)
    {
        if (_next == _reads.Length)
            throw new InvalidOperationException("未配置后续读取。");
        var expected = _reads[_next];
        if (file != expected.Path || maximumBytes != expected.MaximumBytes)
            throw new InvalidOperationException("读取输入或调用顺序与配置不符。");
        _next++;
        return expected.Result;
    }

    public void AssertComplete()
    {
        if (_next != _reads.Length)
            throw new InvalidOperationException("仍有配置的读取未发生。");
    }
}
