using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;

namespace Train.ContractSupport.Tests.Content;

[TestClass]
public sealed class ContentFilesScenarioTests
{
    private static readonly ContentPath Path = new(new ContentRoot("fixture"), "asset.bin");
    private static readonly ContentPath OtherPath = new(Path.Root, "other.bin");
    private static readonly ImmutableArray<byte> Bytes = ImmutableArray.Create<byte>(0, 127, 255);
    private static readonly ImmutableArray<byte> OtherBytes = ImmutableArray.Create<byte>(1, 2, 3);

    [TestMethod]
    public void FixedReadAndLimitScenarioAcceptsConfiguredResponsesInAnyOrder()
    {
        var files = new ScriptedContentFiles(
            new(Path, 2, new ContentIoResult<ImmutableArray<byte>>.Failure(
                ContentIoFailure.LimitExceeded, "configured limit")),
            new(OtherPath, 3, new ContentIoResult<ImmutableArray<byte>>.Success(ImmutableArray.Create<byte>(1, 2, 3))),
            new(Path, 3, new ContentIoResult<ImmutableArray<byte>>.Success(ImmutableArray.Create<byte>(0, 127, 255))));

        ContentFilesScenario.AssertFixedReadAndLimit(files, Path, Bytes, OtherPath, OtherBytes);
        files.AssertComplete();
    }

    [TestMethod]
    public void UnconfiguredInputsAndCallsFailWithoutConsumingExpectedRead()
    {
        var files = new ScriptedContentFiles(new ScriptedContentFiles.ReadCall(Path, 1,
            new ContentIoResult<ImmutableArray<byte>>.Success(ImmutableArray.Create<byte>(42))));

        AssertUnconfigured(() => files.Enumerate(Path));
        AssertUnconfigured(() => files.Read(Path, 0));
        AssertUnconfigured(() => files.Read(OtherPath, 1));
        var incomplete = Assert.ThrowsExactly<InvalidOperationException>(() => files.AssertComplete());
        StringAssert.StartsWith(incomplete.Message, "INCOMPLETE:");
        files.Read(Path, 1);
        files.AssertComplete();
        AssertUnconfigured(() => files.Read(Path, 1));
    }

    [TestMethod]
    public void ScriptRejectsSuccessLargerThanConfiguredLimit()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new ScriptedContentFiles(
            new ScriptedContentFiles.ReadCall(Path, 2,
                new ContentIoResult<ImmutableArray<byte>>.Success(Bytes))));
    }

    [TestMethod]
    public void ScenarioRejectsTruncatedSuccessForAnOverLimitRead()
    {
        Assert.ThrowsExactly<AssertFailedException>(() =>
            ContentFilesScenario.AssertFixedReadAndLimit(new ViolatingContentFiles(false),
                Path, Bytes, OtherPath, OtherBytes));
    }

    [TestMethod]
    public void ScenarioRejectsReusedReadBuffer()
    {
        Assert.ThrowsExactly<AssertFailedException>(() =>
            ContentFilesScenario.AssertFixedReadAndLimit(new ViolatingContentFiles(true),
                Path, Bytes, OtherPath, OtherBytes));
    }

    private static void AssertUnconfigured(Action operation)
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(operation);
        StringAssert.StartsWith(error.Message, "UNCONFIGURED:");
    }

    private sealed class ViolatingContentFiles(bool reuseBuffer) : IContentFiles
    {
        private readonly byte[] _buffer = new byte[Bytes.Length];

        public ContentIoResult<ImmutableArray<ContentFileEntry>> Enumerate(ContentPath directory) =>
            throw new InvalidOperationException("反例不使用枚举。");

        public ContentIoResult<ImmutableArray<byte>> Read(ContentPath file, long maximumBytes)
        {
            var bytes = file == Path ? Bytes : OtherBytes;
            if (bytes.Length > maximumBytes)
            {
                if (reuseBuffer)
                    return new ContentIoResult<ImmutableArray<byte>>.Failure(
                        ContentIoFailure.LimitExceeded, "configured limit");
                return new ContentIoResult<ImmutableArray<byte>>.Success(
                    bytes.Take((int)maximumBytes).ToImmutableArray());
            }
            if (!reuseBuffer)
                return new ContentIoResult<ImmutableArray<byte>>.Success(bytes);
            bytes.CopyTo(_buffer);
            // 故意把可写数组暴露成不可变结果，模拟宿主复用缓冲区的契约违规。
            return new ContentIoResult<ImmutableArray<byte>>.Success(
                ImmutableCollectionsMarshal.AsImmutableArray(_buffer));
        }
    }
}
