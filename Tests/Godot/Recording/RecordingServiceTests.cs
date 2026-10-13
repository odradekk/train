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
        var writes = new List<RecordingSnapshot>();
        var confirms = new List<(RecordingPointId, RecordingWriteOutcome, int)>();
        fixture.Session.ConfirmRecordingStartCall = (point, outcome) =>
        {
            Assert.IsTrue(writes[confirms.Count].Window.Starts.Any(start => start.Id == point),
                "确认的起点必须已包含在对应写入窗口中。");
            confirms.Add((point, outcome, Environment.CurrentManagedThreadId)); return new([], []);
        };
        var captures = 0;
        var window = HostRecordingTests.Window(fixture);
        fixture.Session.ReadRecordingCall = () => window;
        fixture.Session.CaptureRecordingStartCall = () =>
        {
            captures++;
            window = HostRecordingTests.Window(fixture, point: 2, step: 2);
            return new CoreResult<RecordingStart>.Success(window.Starts[0]);
        };
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
        CollectionAssert.AreEqual(new[]
        {
            (new RecordingPointId(1), RecordingWriteOutcome.PublishedWithDependencies, Environment.CurrentManagedThreadId),
            (new RecordingPointId(2), RecordingWriteOutcome.PublishedWithDependencies, Environment.CurrentManagedThreadId)
        }, confirms);
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
        var snapshots = new List<RecordingSnapshot>();
        fixture.Session.ReadRecordingCall = () => window;
        var confirms = new List<RecordingWriteOutcome>();
        fixture.Session.ConfirmRecordingStartCall = (point, outcome) =>
        {
            Assert.IsTrue(snapshots[confirms.Count].Window.Starts.Any(start => start.Id == point));
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
            write: (snapshot, _, _, _) =>
            {
                snapshots.Add(snapshot);
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
        CollectionAssert.AreEqual(new[] { RecordingWriteOutcome.Failed, RecordingWriteOutcome.PublishedWithDependencies }, confirms);
        fixture.AssertNoUnexpectedFailures();
    }

    [TestMethod]
    [TestCategory("fast")]
    public void Late_publication_finishes_output_after_old_session_disposal_without_confirming_either_session()
    {
        using var fixture = new HostFixture();
        Configure(fixture);
        fixture.ReadyHost();
        var completion = new TaskCompletionSource<FileWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextCompletion = new TaskCompletionSource<FileWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldConfirms = 0;
        fixture.Session.ConfirmRecordingStartCall = (_, outcome) =>
        {
            oldConfirms++;
            throw new AssertFailedException("离开的旧核心不能再确认。");
        };
        var snapshots = new List<RecordingSnapshot>();
        var writes = 0;
        using var service = new RecordingService(fixture.Host, Store(), write: (snapshot, _, _, _) =>
        {
            snapshots.Add(snapshot);
            writes++;
            return writes == 1 ? completion.Task : nextCompletion.Task;
        });
        service.Process();
        var oldBinding = fixture.Host.GetStatus()!.Binding;
        var next = new ScriptedCoreSession(new FixedCoreReadView(new(5, 0), HostCoreScenario.Input));
        next.ReadRecordingCall = () => HostRecordingTests.Window(fixture, point: 7, step: 5);
        var newConfirms = 0;
        next.ConfirmRecordingStartCall = (point, outcome) =>
        {
            Assert.AreEqual(RecordingWriteOutcome.PublishedWithDependencies, outcome);
            Assert.IsTrue(snapshots[1].Window.Starts.Any(start => start.Id == point));
            newConfirms++; return new([], []);
        };
        fixture.Core.PrepareRestoreCall = (_, _, _) =>
            new CoreResult<ICoreSessionCandidate>.Success(new ScriptedCoreCandidate(next, HostCoreScenario.References));
        fixture.Load = _ => new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(fixture.Content));
        Assert.IsNull(fixture.Host.BeginRestore("core-state", "content-state", HostFixture.Options).Problem);
        fixture.PumpUntil(() => !fixture.Host.IsBusy);
        Assert.AreNotEqual(oldBinding, fixture.Host.GetStatus()!.Binding);
        Assert.AreEqual(1, fixture.Session.DisposeCount);
        service.Process();
        completion.SetResult(new(true));
        service.Process();
        Assert.AreEqual(0, oldConfirms);
        Assert.AreEqual(1, fixture.Session.DisposeCount);
        Assert.AreEqual(0, newConfirms);
        Assert.IsFalse(service.Status!.HasUsableStart, "旧局发布结果不能使新局已有可用起点。");
        Assert.AreEqual(2, writes);
        nextCompletion.SetResult(new(true));
        service.Close(() => { });
        Assert.AreEqual(0, oldConfirms);
        Assert.AreEqual(1, newConfirms);
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
            using var service = new RecordingService(fixture.Host, store);
            var target = Path.Combine(root, "export.zip");
            byte[] old = [1, 2, 3, 4];
            File.WriteAllBytes(target, old);
            Assert.AreEqual("overwrite-confirmation", service.Export(target)?.Code);
            CollectionAssert.AreEqual(old, File.ReadAllBytes(target));
            Assert.IsFalse(service.IsBusy);
            Assert.IsNull(service.Export(target, true));
            Drain(service);
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
    public void Fault_entire_window_has_its_own_file_and_failed_evidence_keeps_frozen_export_available()
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
            Starts = [initial.Starts[0], new(new(2), fixture.Capture(new(0, 1)))],
            FaultSegments = [new(initial.Starts[0], [], [], fault, faultReferences)]
        };
        fixture.Session.ReadRecordingCall = () => fixture.Session.CurrentView.Fault is null ? initial : faultWindow;
        var confirmations = 0;
        var writes = new List<(RecordingSnapshot Snapshot, string Path, bool Overwrite)>();
        fixture.Session.ConfirmRecordingStartCall = (point, outcome) =>
        {
            Assert.AreEqual(RecordingWriteOutcome.PublishedWithDependencies, outcome);
            Assert.IsTrue(writes[0].Snapshot.Window.Starts.Any(start => start.Id == point));
            confirmations++; return new([], []);
        };
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
        Assert.AreSame(faultWindow, writes[1].Snapshot.Window, "故障包必须捕获整个核心窗口，不能宿主重造片段。");
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

    [TestMethod]
    [TestCategory("fast")]
    public void Replay_capture_failure_confirms_failed_once_and_does_not_block_the_next_period()
    {
        using var fixture = new HostFixture();
        Configure(fixture);
        fixture.ReadyHost();
        var window = HostRecordingTests.Window(fixture);
        fixture.Session.ReadRecordingCall = () => window;
        var replayCalls = 0;
        fixture.ReplayCapture = (history, _) => ++replayCalls == 1 ?
            new ContentResult<IContentReplayCapture>.Failure([new("capture-denied", ContentDiagnosticSeverity.Error,
                "capture", null, null, "history", null, "fixed capture failure")]) :
            new ContentResult<IContentReplayCapture>.Success(new FixedContentReplayCapture(history, "opaque"));
        var writes = new List<RecordingSnapshot>();
        var confirmations = new List<(RecordingPointId, RecordingWriteOutcome)>();
        fixture.Session.ConfirmRecordingStartCall = (point, outcome) =>
        {
            if (outcome == RecordingWriteOutcome.PublishedWithDependencies)
                Assert.IsTrue(writes.Single().Window.Starts.Any(start => start.Id == point));
            confirmations.Add((point, outcome)); return new([], []);
        };
        fixture.Session.CaptureRecordingStartCall = () =>
        {
            window = HostRecordingTests.Window(fixture, point: 2, step: 2);
            return new CoreResult<RecordingStart>.Success(window.Starts[0]);
        };
        using var service = new RecordingService(fixture.Host, Store(), intervalSteps: 2,
            write: (snapshot, _, _, _) => { writes.Add(snapshot); return Task.FromResult(new FileWriteResult(true)); });
        service.Process();
        service.Process();
        Assert.AreEqual(0, writes.Count);
        CollectionAssert.AreEqual(new[] { (new RecordingPointId(1), RecordingWriteOutcome.Failed) }, confirmations);
        StringAssert.Contains(service.LastMessage!, "fixed capture failure");
        Advance(fixture, 1); Advance(fixture, 2);
        service.Process();
        Drain(service);
        CollectionAssert.AreEqual(new[]
        {
            (new RecordingPointId(1), RecordingWriteOutcome.Failed),
            (new RecordingPointId(2), RecordingWriteOutcome.PublishedWithDependencies)
        }, confirmations);
        Assert.AreEqual(1, writes.Count);
        Assert.IsTrue(service.Status!.HasUsableStart);
        fixture.AssertNoUnexpectedFailures();
    }

    [TestMethod]
    [TestCategory("fast")]
    public void Queued_fault_window_is_written_after_replacement_disposes_its_core()
    {
        using var fixture = new HostFixture();
        Configure(fixture);
        fixture.ReadyHost();
        var initial = HostRecordingTests.Window(fixture);
        var fault = new CoreFault("fault", new(0, 1), "submit", "field", null);
        var faultWindow = initial with { EndPosition = fault.Position,
            FaultSegments = [new(initial.Starts[0], [], [], fault, initial.RequiredReferences)] };
        fixture.Session.ReadRecordingCall = () => fixture.Session.CurrentView.Fault is null ? initial : faultWindow;
        fixture.Session.ConfirmRecordingStartCall = (_, _) => throw new AssertFailedException("旧核心不可确认。");
        var first = new TaskCompletionSource<FileWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var faultWrite = new TaskCompletionSource<FileWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<(RecordingSnapshot Snapshot, string Path)>();
        using var service = new RecordingService(fixture.Host, Store(), write: (snapshot, path, _, _) =>
        {
            writes.Add((snapshot, path));
            return writes.Count switch { 1 => first.Task, 2 => faultWrite.Task, _ => Task.FromResult(new FileWriteResult(true)) };
        });
        service.Process();
        var oldBinding = fixture.Host.GetStatus()!.Binding;
        var command = new CoreCommand.StopTrain(new(1));
        fixture.Session.QueueSubmit(command, CommandSource.Console,
            new SubmissionResult.Completed(new(1, new(0, 0), fault.Position, new CommandDecision.Faulted(fault)), [], null),
            new FixedCoreReadView(fault.Position, HostCoreScenario.Input, fault));
        fixture.Host.Submit(oldBinding, command, CommandSource.Console);
        var next = new ScriptedCoreSession(new FixedCoreReadView(new(5, 0), HostCoreScenario.Input));
        next.ReadRecordingCall = () => HostRecordingTests.Window(fixture, point: 7, step: 5);
        var confirmations = 0;
        next.ConfirmRecordingStartCall = (point, outcome) =>
        {
            Assert.AreEqual(RecordingWriteOutcome.PublishedWithDependencies, outcome);
            Assert.IsTrue(writes[2].Snapshot.Window.Starts.Any(start => start.Id == point));
            confirmations++; return new([], []);
        };
        fixture.Core.PrepareRestoreCall = (_, _, _) => new CoreResult<ICoreSessionCandidate>.Success(
            new ScriptedCoreCandidate(next, HostCoreScenario.References));
        fixture.Load = _ => new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(fixture.Content));
        Assert.IsNull(fixture.Host.BeginRestore("core-state", "content-state", HostFixture.Options).Problem);
        fixture.PumpUntil(() => !fixture.Host.IsBusy);
        Assert.AreEqual(1, fixture.Session.DisposeCount);
        first.SetResult(new(true));
        service.Process();
        Assert.AreEqual(2, writes.Count);
        Assert.AreEqual(oldBinding, writes[1].Snapshot.Binding);
        Assert.AreSame(faultWindow, writes[1].Snapshot.Window);
        Assert.AreNotEqual(service.SnapshotPath, writes[1].Path);
        Assert.AreEqual(0, confirmations);
        faultWrite.SetResult(new(true));
        service.Process();
        Drain(service);
        Assert.AreEqual(3, writes.Count);
        Assert.AreEqual(1, confirmations);
        fixture.AssertNoUnexpectedFailures();
    }

    [TestMethod]
    [TestCategory("integration")]
    [OSCondition(OperatingSystems.Windows)]
    public void Successful_snapshot_remains_usable_and_unchanged_when_the_next_publication_fails()
    {
        WithDirectory(root =>
        {
            using var fixture = new HostFixture();
            Configure(fixture);
            fixture.ReadyHost();
            var window = HostRecordingTests.Window(fixture);
            fixture.Session.ReadRecordingCall = () => window;
            using var service = new RecordingService(fixture.Host, new FileSaveStore(root), intervalSteps: 2);
            var confirmations = new List<(RecordingPointId, RecordingWriteOutcome)>();
            fixture.Session.ConfirmRecordingStartCall = (point, outcome) =>
            {
                if (outcome == RecordingWriteOutcome.PublishedWithDependencies)
                {
                    Assert.IsTrue(window.Starts.Any(start => start.Id == point));
                    RecordingPackage.Capture(window, new FixedContentReplayCapture([fixture.Content], "opaque content"),
                        HostFixture.Options.Runtime).Validate(service.SnapshotPath);
                }
                else window = window with { Gaps = [new(new(2, 0), null, "write-failed")] };
                confirmations.Add((point, outcome)); return new([], window.Gaps);
            };
            fixture.Session.CaptureRecordingStartCall = () =>
            {
                window = HostRecordingTests.Window(fixture, point: 2, step: 2);
                return new CoreResult<RecordingStart>.Success(window.Starts[0]);
            };
            service.Process();
            Pump(service, () => !service.IsBusy);
            var old = File.ReadAllBytes(service.SnapshotPath);
            using (var locked = new FileStream(service.SnapshotPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Advance(fixture, 1); Advance(fixture, 2);
                service.Process();
                Pump(service, () => !service.IsBusy);
            }
            Assert.IsTrue(service.Status!.HasUsableStart);
            Assert.IsTrue(service.Status.Incomplete);
            CollectionAssert.AreEqual(old, File.ReadAllBytes(service.SnapshotPath));
            Drain(service);
            CollectionAssert.AreEqual(new[]
            {
                (new RecordingPointId(1), RecordingWriteOutcome.PublishedWithDependencies),
                (new RecordingPointId(2), RecordingWriteOutcome.Failed)
            }, confirmations);
            CollectionAssert.AreEqual(new[] { service.SnapshotPath }, Directory.GetFiles(service.EvidenceDirectory));
            fixture.AssertNoUnexpectedFailures();
        });
    }

    [TestMethod]
    [TestCategory("integration")]
    [OSCondition(OperatingSystems.Windows)]
    public void Confirmed_export_publication_failure_preserves_existing_file()
    {
        WithDirectory(root =>
        {
            using var fixture = new HostFixture();
            Configure(fixture);
            fixture.ReadyHost();
            using var service = new RecordingService(fixture.Host, new FileSaveStore(root));
            var target = Path.Combine(root, "export.zip");
            byte[] original = [11, 22, 33];
            File.WriteAllBytes(target, original);
            Assert.AreEqual("overwrite-confirmation", service.Export(target)?.Code);
            using (var locked = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.IsNull(service.Export(target, true));
                Drain(service);
            }
            StringAssert.Contains(service.LastMessage!, "失败");
            CollectionAssert.AreEqual(original, File.ReadAllBytes(target));
            CollectionAssert.AreEqual(new[] { target }, Directory.GetFiles(root));
            fixture.AssertNoUnexpectedFailures();
        });
    }

    private static void Drain(RecordingService service)
    {
        var closed = false;
        service.Close(() => closed = true);
        Pump(service, () => closed);
    }

    private static void Pump(RecordingService service, Func<bool> completed)
        => Assert.IsTrue(SpinWait.SpinUntil(() => { service.Process(); return completed(); }, HostFixture.HangTimeout),
            "挂起保护超时：录制后台任务未完成。");

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
