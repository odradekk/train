// 内容文件端口情景的执行与反例检查。
// 这里只验证共享断言和有限替身，真实宿主 I/O 留给后续验证。
using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;

namespace Train.ContractSupport.Tests.Content;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:content-contract-support")]
public sealed class ContentFilesScenarioTests
{
    private static readonly ContentPath Path = new(new ContentRoot("fixture"), "asset.bin");

    [TestMethod]
    public void FixedReadAndLimitScenarioAcceptsConfiguredResponses()
    {
        var bytes = ImmutableArray.Create<byte>(0, 127, 255);
        var success = new ContentIoResult<ImmutableArray<byte>>.Success(bytes);
        var files = new ScriptedContentFiles(
            new(Path, 3, success),
            new(Path, 2, new ContentIoResult<ImmutableArray<byte>>.Failure(
                ContentIoFailure.LimitExceeded, "configured limit")),
            new(Path, 3, success));

        ContentFilesScenario.AssertFixedReadAndLimit(files, Path, bytes);
        files.AssertComplete();
    }

    [TestMethod]
    public void UnconfiguredInputsAndCallsFailWithoutConsumingExpectedRead()
    {
        var files = new ScriptedContentFiles(new ScriptedContentFiles.ReadCall(Path, 1,
            new ContentIoResult<ImmutableArray<byte>>.Success(ImmutableArray.Create<byte>(42))));

        Assert.ThrowsException<InvalidOperationException>(() => files.Enumerate(Path));
        Assert.ThrowsException<InvalidOperationException>(() => files.Read(Path, 0));
        Assert.ThrowsException<InvalidOperationException>(() =>
            files.Read(new ContentPath(Path.Root, "other.bin"), 1));
        Assert.ThrowsException<InvalidOperationException>(() => files.AssertComplete());
        files.Read(Path, 1);
        files.AssertComplete();
        Assert.ThrowsException<InvalidOperationException>(() => files.Read(Path, 1));
    }

    [TestMethod]
    public void ScenarioRejectsTruncatedSuccessForAnOverLimitRead()
    {
        var bytes = ImmutableArray.Create<byte>(1, 2);
        var files = new ScriptedContentFiles(
            new(Path, 2, new ContentIoResult<ImmutableArray<byte>>.Success(bytes)),
            new(Path, 1, new ContentIoResult<ImmutableArray<byte>>.Success(ImmutableArray.Create<byte>(1))));

        Assert.ThrowsException<AssertFailedException>(() =>
            ContentFilesScenario.AssertFixedReadAndLimit(files, Path, bytes));
        files.AssertComplete();
    }
}
