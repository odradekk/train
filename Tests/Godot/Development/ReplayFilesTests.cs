using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Development;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Godot.Tests.Host;

namespace Train.Godot.Tests.Development;

[TestClass]
[TestCategory("integration")]
public sealed class ReplayFilesTests
{
    [TestMethod]
    public void ProductionPackageReadBoundaryExportAndOrdinaryLoadKeepOpaquePayloads()
    {
        using var files = new ReplayFiles();
        using var run = new ReplayFixture();
        var package = Path.Combine(files.Root, "recording.zip");
        run.Package.Write(package);
        var oldId = run.Host.Host.GetSaveStatus()!.Identity.Id;
        run.Open(package, fromFile: true);
        Assert.IsTrue(run.Host.Host.GetStatus()!.IsReplay);
        var capture = new FixedCoreCapture(new(new(2, 3), "boundary checkpoint", "boundary digest"),
            HostCoreScenario.References, "boundary-core", HostCoreScenario.Input, new(7));
        run.Replay.QueueAdvance(2, capture.Position, new(ReplayStop.RequestedPosition, capture.Position, [], null, null),
            ReplayFixture.View(capture.Position));
        Assert.AreEqual(capture.Position, run.Host.Host.AdvanceReplay(2, capture.Position)!.Position);
        run.Replay.SetCapture(capture);
        run.Host.CaptureContent = (_, references) =>
        {
            Assert.AreSame(capture.References, references);
            return new ContentResult<IContentCapture>.Success(new FixedContentCapture("boundary-content", new(9)));
        };
        var store = new FileSaveStore(files.Root);
        using var saves = new SaveService(run.Host.Host, store, () => 0);
        using var tools = new DeveloperTools(run.Host.Host, saves, () => 0);
        var exported = Path.Combine(files.Root, "boundary.json");
        Assert.IsNull(tools.ExportReplayBoundary(exported));
        Pump(run, saves, tools, () => !tools.IsBusy);
        var document = store.Read(exported);
        Assert.AreNotEqual(Guid.Empty, document.Header.Id);
        Assert.AreNotEqual(oldId, document.Header.Id);
        Assert.AreEqual(new SavePosition(2, 3), document.Header.Position);
        Assert.AreEqual("boundary-core", document.CorePayload);
        Assert.AreEqual("boundary-content", document.ContentPayload);
        Assert.IsTrue(run.Host.Host.GetStatus()!.IsReplay);

        var session = new ScriptedCoreSession(ReplayFixture.View(capture.Position));
        var command = new CoreCommand.StartTrain(new(1));
        session.QueueSubmit(command, CommandSource.Console, new SubmissionResult.Completed(
            new(1, capture.Position, new(2, 4), new CommandDecision.Accepted([], [])), [], null), ReplayFixture.View(new(2, 4)));
        run.Host.Core.DecodeStateCall = payload =>
        {
            Assert.AreEqual("boundary-core", payload);
            return new CoreResult<ICoreCapture>.Success(capture);
        };
        run.Host.Factory.DecodeCaptureCall = payload =>
        {
            Assert.AreEqual("boundary-content", payload);
            return new ContentResult<IContentCapture>.Success(new FixedContentCapture(payload, new(9)));
        };
        run.Host.Load = _ => new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(run.Host.Content));
        run.Host.Core.PrepareRestoreCall = (state, _, _) =>
        {
            Assert.AreSame(capture, state);
            return new CoreResult<ICoreSessionCandidate>.Success(new ScriptedCoreCandidate(session, capture.References));
        };
        Assert.IsNull(saves.Load(new(exported, store.ReadHeader(exported), false)));
        if (saves.Leaving is not null) saves.ChooseLeave(LeaveChoice.Discard);
        Pump(run, saves, tools, () => run.Host.Host.GetStatus() is { IsReplay: false } && !saves.HasPendingWork);
        Assert.AreEqual(1, run.Replay.DisposeCount);
        Assert.AreEqual(document.Header.Id, run.Host.Host.GetSaveStatus()!.Identity.Id);
        Assert.IsInstanceOfType<SubmissionResult.Completed>(run.Host.Host.Submit(run.Host.Host.GetStatus()!.Binding, command, CommandSource.Console));
        Assert.AreEqual(1, session.SubmitCount);
    }

    [TestMethod]
    public void FailedBoundaryPublicationPreservesExistingFileAndReplay()
    {
        using var files = new ReplayFiles();
        using var run = new ReplayFixture();
        var package = Path.Combine(files.Root, "recording.zip");
        run.Package.Write(package);
        run.Open(package, fromFile: true);
        run.Replay.SetCapture(run.Host.Capture(new(0, 0)));
        var target = Path.Combine(files.Root, "preserved.json");
        const string original = "existing target bytes";
        File.WriteAllText(target, original);
        var store = new FileSaveStore(files.Root, File.WriteAllText,
            (_, _, _) => throw new IOException("publication denied"));
        using var saves = new SaveService(run.Host.Host, store, () => 0);
        using var tools = new DeveloperTools(run.Host.Host, saves, () => 0);
        Assert.AreEqual("overwrite-confirmation", tools.ExportReplayBoundary(target)?.Code);
        Assert.IsNull(tools.ExportReplayBoundary(target, overwriteConfirmed: true));
        Pump(run, saves, tools, () => !tools.IsBusy);
        StringAssert.Contains(tools.LastMessage!, "publication denied");
        Assert.AreEqual(original, File.ReadAllText(target));
        Assert.IsTrue(run.Host.Host.GetStatus()!.IsReplay);
        Assert.AreEqual(0, Directory.GetFiles(files.Root, "*.tmp").Length);
    }

    [TestMethod]
    public void InvalidPackageFileReportsFailureAndPreservesSession()
    {
        using var files = new ReplayFiles();
        using var run = new ReplayFixture();
        var path = Path.Combine(files.Root, "bad.zip");
        File.WriteAllText(path, "not a ZIP archive");
        var before = run.Host.Host.GetStatus()!.Binding;
        Assert.IsNull(run.Host.Host.BeginReplay(path).Problem);
        run.Host.PumpUntil(() => !run.Host.Host.IsBusy);
        Assert.AreEqual(before, run.Host.Host.GetStatus()!.Binding);
        Assert.IsFalse(run.Host.Host.GetStatus()!.IsReplay);
        Assert.IsTrue(run.Host.Logs.Any(log => log.EventId == "host_replay_failed" &&
            log.Attributes["code"] as string == "preparation_exception"));
        Assert.AreEqual(0, run.Host.Session.DisposeCount);
    }

    [TestMethod]
    public void CancelOpeningReplayAfterSavePublicationKeepsTheOrdinarySession()
    {
        using var files = new ReplayFiles();
        using var run = new ReplayFixture();
        using var published = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var binding = run.Host.Host.GetStatus()!.Binding;
        var store = new FileSaveStore(files.Root, File.WriteAllText, (candidate, target, overwrite) =>
        {
            File.Move(candidate, target, overwrite);
            published.Set();
            HostFixture.WaitForSignal(release, "保存发布完成后返回");
        });
        using var saves = new SaveService(run.Host.Host, store, () => 0);
        using var tools = new DeveloperTools(run.Host.Host, saves);
        try
        {
            Assert.IsNull(tools.OpenReplay("unused.zip"));
            saves.ChooseLeave(LeaveChoice.Save);
            HostFixture.WaitForSignal(published, "保存已发布");
            tools.Cancel();
            release.Set();
            Pump(run, saves, tools, () => !tools.IsBusy);
            Assert.AreEqual(binding, run.Host.Host.GetStatus()!.Binding);
            Assert.IsFalse(run.Host.Host.GetStatus()!.IsReplay);
            Assert.AreEqual(0, run.Host.Session.DisposeCount);
        }
        finally { release.Set(); }
    }

    private static void Pump(ReplayFixture run, SaveService saves, DeveloperTools tools, Func<bool> completed)
        => Assert.IsTrue(SpinWait.SpinUntil(() =>
        {
            run.Host.Host.ProcessFrame(0); saves.Process(); tools.Process(); return completed();
        }, HostFixture.HangTimeout), "后台文件或恢复操作超时：" + tools.LastMessage + "；" + saves.LastMessage);

    private sealed class ReplayFiles : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "train-replay-tests-" + Guid.NewGuid().ToString("N"));
        internal ReplayFiles() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
