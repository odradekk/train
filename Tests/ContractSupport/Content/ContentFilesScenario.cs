// 内容文件端口的共享情景；调用方提供真实端口或有限替身。
// 只断言公开的字节和超限结果，不推断来源的文件系统行为。
using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;

namespace Train.ContractSupport.Tests.Content;

/// <summary>核对公开文件端口的固定字节与超限失败；实际读取由调用方提供。</summary>
public static class ContentFilesScenario
{
    public static void AssertFixedReadAndLimit(IContentFiles files, ContentPath path,
        ImmutableArray<byte> expected)
    {
        Assert.IsFalse(expected.IsDefaultOrEmpty, "情景需要非空字节以产生超限请求。");
        var first = files.Read(path, expected.Length);
        Assert.IsInstanceOfType<ContentIoResult<ImmutableArray<byte>>.Success>(first);
        var retained = ((ContentIoResult<ImmutableArray<byte>>.Success)first).Value;
        CollectionAssert.AreEqual(expected.ToArray(), retained.ToArray());

        var limited = files.Read(path, expected.Length - 1);
        Assert.IsInstanceOfType<ContentIoResult<ImmutableArray<byte>>.Failure>(limited);
        Assert.AreEqual(ContentIoFailure.LimitExceeded,
            ((ContentIoResult<ImmutableArray<byte>>.Failure)limited).Kind);

        var again = files.Read(path, expected.Length);
        Assert.IsInstanceOfType<ContentIoResult<ImmutableArray<byte>>.Success>(again);
        CollectionAssert.AreEqual(expected.ToArray(),
            ((ContentIoResult<ImmutableArray<byte>>.Success)again).Value.ToArray());
        CollectionAssert.AreEqual(expected.ToArray(), retained.ToArray(),
            "后续读取和失败不能改变已返回的字节。");
    }
}
