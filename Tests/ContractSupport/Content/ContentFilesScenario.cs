// 内容文件端口的共享情景；调用方提供真实端口或有限情景替身。
// 只断言公开的字节和超限结果，不推断来源的文件系统行为。
using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;

namespace Train.Content.ContractSupport;

/// <summary>核对两个等长文件的固定字节、结果不可变性与超限失败；调用方提供端口及独立的期望字节。</summary>
public static class ContentFilesScenario
{
    public static void AssertFixedReadAndLimit(IContentFiles files, ContentPath path,
        ImmutableArray<byte> expected, ContentPath otherPath, ImmutableArray<byte> otherExpected)
    {
        Assert.IsFalse(expected.IsDefaultOrEmpty, "情景需要非空字节以产生超限请求。");
        Assert.AreNotEqual(path, otherPath, "情景需要两个不同路径。");
        Assert.IsFalse(otherExpected.IsDefaultOrEmpty, "第二个路径需要非空字节。");
        Assert.AreEqual(expected.Length, otherExpected.Length,
            "情景需要两个文件字节等长，才能检出同长度缓冲区复用。");
        Assert.IsFalse(expected.SequenceEqual(otherExpected), "两个路径需要不同字节以检出缓冲区复用。");
        var first = files.Read(path, expected.Length);
        Assert.IsInstanceOfType<ContentIoResult<ImmutableArray<byte>>.Success>(first);
        var retained = ((ContentIoResult<ImmutableArray<byte>>.Success)first).Value;
        CollectionAssert.AreEqual(expected.ToArray(), retained.ToArray());

        var other = files.Read(otherPath, otherExpected.Length);
        Assert.IsInstanceOfType<ContentIoResult<ImmutableArray<byte>>.Success>(other);
        CollectionAssert.AreEqual(otherExpected.ToArray(),
            ((ContentIoResult<ImmutableArray<byte>>.Success)other).Value.ToArray());
        CollectionAssert.AreEqual(expected.ToArray(), retained.ToArray(),
            "读取另一个文件不能改变已返回的字节。");

        var limited = files.Read(path, expected.Length - 1);
        Assert.IsInstanceOfType<ContentIoResult<ImmutableArray<byte>>.Failure>(limited);
        Assert.AreEqual(ContentIoFailure.LimitExceeded,
            ((ContentIoResult<ImmutableArray<byte>>.Failure)limited).Kind);

        CollectionAssert.AreEqual(expected.ToArray(), retained.ToArray(),
            "后续读取和失败不能改变已返回的字节。");
    }
}
