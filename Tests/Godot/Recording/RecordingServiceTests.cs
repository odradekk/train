using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Recording;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Godot.Tests.Host;

namespace Train.Godot.Tests.Recording;

[TestClass]
public sealed class RecordingServiceTests
{
    [TestMethod]
    [TestCategory("fast")]
    public void Initial_publication_waits_for_owner_process_and_pending_write_blocks_new_starts()
    {
        using var fixture = new HostFixture();
        Configure(fixture);
        fixture.ReadyHost();
        var completion = new TaskCompletionSource<FileWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextCompletion = new TaskCompletionSource<FileWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirms = new List<(RecordingPointId, RecordingWriteOutcome, int)>();
        fixture.Session.ConfirmRecordingStartCall = (point, outcome) =>
        { confirms.Add((point, outcome, Environment.CurrentManagedThreadId)); return new([], []); };
        var captures = 0;
        var window = HostRecordingTests.Window(fixture);
        fixture.Session.ReadRecordingCall = () => window;
        fixture.Session.CaptureRecordingStartCall = () =>
        {
            captures++;
            window = HostRecordingTests.Window(fixture, point: 2, step: 2);
            return new CoreResult<RecordingStart>.Success(window.Starts[0]);
        };
        var writes = new List<RecordingSnapshot>();
        using var service = new RecordingService(fixture.Host, Store(), intervalSteps: 2,
            write: (snapshot, _, _, _) =>
            {
                writes.Add(snapshot);
                return writes.Count == 1 ? completion.Task : nextCompletion.Task;
            });
        service.Process();
        service.Process();
        Assert.AreEqual(1, writes.Count);
        Assert.AreEqual(0, captures, "初始起点来自 ReadRecording，不能额外 Capture。");
        Assert.AreEqual(0, confirms.Count);
        Assert.AreEqual(HostFixture.Options.Runtime, writes[0].Runtime);
        Advance(fixture, 1);
        Advance(fixture, 2);
        service.Process();
        Assert.AreEqual(0, captures, "未确认写入期间不能捕获新起点。");
        completion.SetResult(new(true));
        Assert.AreEqual(0, confirms.Count, "后台完成不能直接确认核心。");
        service.Process();
        Assert.AreEqual((new RecordingPointId(1), RecordingWriteOutcome.PublishedWithDependencies,
            Environment.CurrentManagedThreadId), confirms.Single());
        Assert.AreEqual(1, captures);
        Assert.AreEqual(2, writes.Count);
        nextCompletion.SetResult(new(true));
        service.Close(() => { });
        fixture.AssertNoUnexpectedFailures();
    }

    [TestMethod]
    [TestCategory("fast")]
    public void Failed_start_is_confirmed_once_and_commands_do_not_schedule_window_writes()
    {
        using var fixture = new HostFixture();
        Configure(fixture);
        fixture.ReadyHost();
        var initial = HostRecordingTests.Window(fixture);
        var failed = initial with { Gaps = [new(new(0, 0), null, "write-failed")] };
        var window = initial;
        fixture.Session.ReadRecordingCall = () => window;
        var confirms = new List<RecordingWriteOutcome>();
        fixture.Session.ConfirmRecordingStartCall = (point, outcome) =>
        {
            confirms.Add(outcome);
            if (point == new RecordingPointId(1)) window = failed;
            return new([], failed.Gaps);
        };
        var captures = 0;
        fixture.Session.CaptureRecordingStartCall = () =>
        {
            captures++;
            window = HostRecordingTests.Window(fixture, point: 2, step: 2) with { Gaps = failed.Gaps };
            return new CoreResult<RecordingStart>.Success(window.Starts[0]);
        };
        var writes = 0;
        var nextCompletion = new TaskCompletionSource<FileWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new RecordingService(fixture.Host, Store(), intervalSteps: 2,
            write: (_, _, _, _) =>
            {
                writes++;
                return writes == 1 ? Task.FromResult(new FileWriteResult(false, "write-failed", "disk failure")) :
                    nextCompletion.Task;
            });
        service.Process();
        service.Process();
        service.Process();
        CollectionAssert.AreEqual(new[] { RecordingWriteOutcome.Failed }, confirms);
        Assert.IsTrue(service.Status!.Incomplete);
        Assert.IsFalse(service.Status.HasUsableStart);
        StringAssert.Contains(service.LastMessage!, "disk failure");
        var command = new CoreCommand.StopTrain(new(1));
        fixture.Session.QueueSubmit(command, CommandSource.Console,
            new SubmissionResult.Completed(new(1, new(0, 0), new(0, 90000), new CommandDecision.Accepted([], [])), [], null),
            new FixedCoreReadView(new(0, 90000), HostCoreScenario.Input));
        fixture.Host.Submit(fixture.Host.GetStatus()!.Binding, command, CommandSource.Console);
        service.Process();
        Advance(fixture, 1);
        service.Process();
        Assert.AreEqual(1, writes);
        Assert.AreEqual(0, captures, "命令序号和未达间隔的模拟步均不能触发起点。");
        Advance(fixture, 2);
        service.Process();
        Assert.AreEqual(2, writes);
        Assert.AreEqual(1, captures);
        Assert.AreEqual(1, confirms.Count);
        nextCompletion.SetResult(new(true));
        service.Close(() => { });
        fixture.AssertNoUnexpectedFailures();
    }

    [TestMethod]
    [TestCategory("fast")]
    public void Late_publication_confirms_retired_session_then_disposes_it_without_touching_new_session()
    {
        using var fixture = new HostFixture();
        Configure(fixture);
        fixture.ReadyHost();
        var completion = new TaskCompletionSource<FileWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextCompletion = new TaskCompletionSource<FileWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldConfirms = 0;
        fixture.Session.ConfirmRecordingStartCall = (_, outcome) =>
        {
            Assert.AreEqual(RecordingWriteOutcome.PublishedWithDependencies, outcome);
            Assert.AreEqual(0, fixture.Session.DisposeCount);
            oldConfirms++;
            return new([], []);
        };
        var writes = 0;
        using var service = new RecordingService(fixture.Host, Store(), write: (_, _, _, _) =>
        {
            writes++;
            return writes == 1 ? completion.Task : nextCompletion.Task;
        });
        service.Process();
        var oldBinding = fixture.Host.GetStatus()!.Binding;
        var next = new ScriptedCoreSession(new FixedCoreReadView(new(5, 0), HostCoreScenario.Input));
        next.ReadRecordingCall = () => HostRecordingTests.Window(fixture, point: 7, step: 5);
        var newConfirms = 0;
        next.ConfirmRecordingStartCall = (_, _) => { newConfirms++; return new([], []); };
        fixture.Core.PrepareRestoreCall = (_, _, _) =>
            new CoreResult<ICoreSessionCandidate>.Success(new ScriptedCoreCandidate(next, HostCoreScenario.References));
        fixture.Load = _ => new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(fixture.Content));
        Assert.IsNull(fixture.Host.BeginRestore("core-state", "content-state", HostFixture.Options).Problem);
        fixture.PumpUntil(() => !fixture.Host.IsBusy);
        Assert.AreNotEqual(oldBinding, fixture.Host.GetStatus()!.Binding);
        Assert.AreEqual(0, fixture.Session.DisposeCount);
        service.Process();
        completion.SetResult(new(true));
        service.Process();
        Assert.AreEqual(1, oldConfirms);
        Assert.AreEqual(1, fixture.Session.DisposeCount);
        Assert.AreEqual(0, newConfirms);
        Assert.IsFalse(service.Status!.HasUsableStart, "旧局发布结果不能使新局已有可用起点。");
        Assert.AreEqual(2, writes);
        nextCompletion.SetResult(new(true));
        service.Close(() => { });
        fixture.AssertNoUnexpectedFailures();
    }

    [TestMethod]
    [TestCategory("integration")]
    public void Export_requires_overwrite_confirmation_and_publishes_a_valid_zip()
    {
        WithDirectory(root =>
        {
            using var fixture = new HostFixture();
            Configure(fixture);
            fixture.ReadyHost();
            var store = new FileSaveStore(root);
            using var service = new RecordingService(fixture.Host, store, write: (snapshot, path, overwrite, cancellation) =>
            {
                var package = RecordingPackage.Capture(snapshot.Window, snapshot.Content, snapshot.Runtime);
                return Task.FromResult(store.Write(path, package.Write, package.Validate, overwrite, cancellation));
            });
            var target = Path.Combine(root, "export.zip");
            byte[] old = [1, 2, 3, 4];
            File.WriteAllBytes(target, old);
            Assert.AreEqual("overwrite-confirmation", service.Export(target)?.Code);
            CollectionAssert.AreEqual(old, File.ReadAllBytes(target));
            Assert.IsFalse(service.IsBusy);
            Assert.IsNull(service.Export(target, true));
            var closed = false;
            service.Close(() => closed = true);
            Assert.IsTrue(closed);
            Assert.IsNull(fixture.Host.CaptureRecording(fixture.Host.GetStatus()!.Binding,
                HostRecordingTests.Window(fixture)).Failure);
            RecordingPackage.Capture(HostRecordingTests.Window(fixture),
                new FixedContentReplayCapture([fixture.Content], "opaque content"), HostFixture.Options.Runtime).Validate(target);
            CollectionAssert.AreEqual(new[] { target }, Directory.GetFiles(root));
            fixture.AssertNoUnexpectedFailures();
        });
    }

    [TestMethod]
    [TestCategory("fast")]
    public void Fault_fragment_has_its_own_file_and_failed_evidence_keeps_frozen_export_available()
    {
        using var fixture = new HostFixture();
        Configure(fixture);
        fixture.ReadyHost();
        var initial = HostRecordingTests.Window(fixture);
        var fault = new CoreFault("fault", new(0, 1), "submit", "field", null);
        var faultReferences = HostCoreScenario.References with { Inputs = [new("fault-input")] };
        var faultWindow = initial with
        {
            EndPosition = fault.Position,
            FaultSegments = [new(initial.Starts[0], [], [], fault, faultReferences)]
        };
        fixture.Session.ReadRecordingCall = () => fixture.Session.CurrentView.Fault is null ? initial : faultWindow;
        var confirmations = 0;
        fixture.Session.ConfirmRecordingStartCall = (_, _) => { confirmations++; return new([], []); };
        var writes = new List<(RecordingSnapshot Snapshot, string Path, bool Overwrite)>();
        using var service = new RecordingService(fixture.Host, Store(), write: (snapshot, path, overwrite, _) =>
        {
            writes.Add((snapshot, path, overwrite));
            return Task.FromResult(writes.Count == 2 ? new FileWriteResult(false, "fault-write", "fault disk failure") : new(true));
        });
        service.Process();
        service.Process();
        var command = new CoreCommand.StopTrain(new(1));
        fixture.Session.QueueSubmit(command, CommandSource.Console,
            new SubmissionResult.Completed(new(1, new(0, 0), fault.Position, new CommandDecision.Faulted(fault)), [], null),
            new FixedCoreReadView(fault.Position, HostCoreScenario.Input, fault));
        fixture.Host.Submit(fixture.Host.GetStatus()!.Binding, command, CommandSource.Console);
        service.Process();
        Assert.AreEqual(2, writes.Count);
        Assert.AreNotEqual(service.SnapshotPath, writes[1].Path);
        Assert.AreEqual(service.EvidenceDirectory, Path.GetDirectoryName(writes[1].Path));
        Assert.IsFalse(writes[1].Overwrite);
        Assert.AreEqual(fault.Position, writes[1].Snapshot.Window.EndPosition);
        Assert.AreSame(faultReferences, writes[1].Snapshot.Window.RequiredReferences);
        service.Process();
        Assert.AreEqual(1, confirmations, "故障独立文件不能再次确认录制起点。");
        Assert.IsTrue(service.Status!.Frozen);
        Assert.IsTrue(service.Status.Incomplete);
        StringAssert.Contains(service.LastMessage!, "fault disk failure");
        Assert.IsNull(service.Export(Path.Combine(service.EvidenceDirectory, "player-export.zip")));
        service.Close(() => { });
        Assert.AreEqual(3, writes.Count);
        Assert.AreEqual(1, confirmations);
        StringAssert.Contains(service.LastMessage!, "fault disk failure", "后续导出成功不能抹去独立故障证据失败的原因。");
        fixture.AssertNoUnexpectedFailures();
    }

    [TestMethod]
    [TestCategory("integration")]
    [OSCondition(OperatingSystems.Windows)]
    public void Shared_file_publication_preserves_old_target_on_validation_and_locked_target_failures()
    {
        WithDirectory(root =>
        {
            using var fixture = new HostFixture();
            var package = RecordingPackage.Capture(HostRecordingTests.Window(fixture),
                new FixedContentReplayCapture([], "opaque content"), HostFixture.Options.Runtime);
            var store = new FileSaveStore(root);
            var target = Path.Combine(root, "snapshot.zip");
            byte[] old = [4, 3, 2, 1];
            File.WriteAllBytes(target, old);
            var validation = store.Write(target, path => File.WriteAllText(path, "broken ZIP"), package.Validate, true);
            Assert.IsFalse(validation.Published);
            CollectionAssert.AreEqual(old, File.ReadAllBytes(target));
            using (var locked = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var publication = store.Write(target, package.Write, package.Validate, true);
                Assert.IsFalse(publication.Published);
            }
            CollectionAssert.AreEqual(old, File.ReadAllBytes(target));
            CollectionAssert.AreEqual(new[] { target }, Directory.GetFiles(root));
            fixture.AssertNoUnexpectedFailures();
        });
    }

    private static void Configure(HostFixture fixture)
    {
        fixture.Session.ReadRecordingCall = () => HostRecordingTests.Window(fixture);
        fixture.ReplayCapture = (history, _) =>
            new ContentResult<IContentReplayCapture>.Success(new FixedContentReplayCapture(history, "opaque content"));
    }

    private static FileSaveStore Store() => new(Path.Combine(Path.GetTempPath(), $"train-recording-{Guid.NewGuid():N}"));

    private static void Advance(HostFixture fixture, ulong position)
    {
        fixture.Session.QueueAdvance(new(1, new(position, 0), [], null, null),
            new FixedCoreReadView(new(position, 0), HostCoreScenario.Input));
        Assert.IsNull(fixture.Host.Step(1).Problem);
    }

    private static void WithDirectory(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), $"train-recording-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
