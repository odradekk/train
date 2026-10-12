using System.Collections.Immutable;
using System.Text.Json;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.Core.Simulation;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class SimulationTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task Automatic_frames_and_console_controls_share_the_session()
    {
        var fixture = new Fixture(TestScene.GetTree());
        try
        {
            var app = await fixture.LoadAndAddScene<Train.App.App>("res://App/App.tscn");
            var console = app.GetChildren().OfType<GameConsole>().Single();
            Require(app.GetSimulationStatus() is null, "普通启动隐式创建了模拟会话。");
            console.SubmitLine("sim");
            Require(Logs(app, SimulationConsoleCommands.SessionUnavailableEventId).Length == 1, "无会话查询没有控制台反馈。");
            var calculations = new ProbeCalculations();
            var states = new ProbeStateCodec();
            var hostThread = System.Environment.CurrentManagedThreadId;
            app.AttachSession(CreateSession(states, new(73, "simulation-host-check-v1"), calculations), states);
            Require(Status(app).State == new ProbeState(73, "simulation-host-check-v1"), "挂接未发布完整初始状态。");
            var advances = new List<AdvanceResult>();
            app.SimulationAdvanced += advances.Add;
            await WaitForFrames(() => advances.Count >= 2 && advances.Sum(result => (long)result.StepsAdvanced) >= 2);
            var running = Status(app);
            var state = (ProbeState)running.State;
            Require(calculations.ThreadIds.Count > 0 && calculations.ThreadIds.All(id => id == hostThread), "领域计算离开 Godot 主线程。");
            Require(state.Counter == state.CompletedSteps && state.TransactionCount == state.CompletedSteps, "自动帧业务计数与事务数不一致。");
            Require(advances.All(result => result.Events.All(item => item is ProbeStepEvent)) &&
                advances.Sum(result => result.Events.Length) == advances.Sum(result => (long)result.StepsAdvanced), "步事件与已提交步数不一致。");
            var checkpoint = app.CreateSimulationCheckpoint();
            console.SubmitLine("sim step");
            Require(Logs(app, SimulationConsoleCommands.RequiresPauseEventId).Length == 1 && Status(app) == running && app.CreateSimulationCheckpoint() == checkpoint, "运行中手动步修改了会话或缺少暂停反馈。");
            foreach (var speed in new[] { SimulationSpeed.One, SimulationSpeed.Two, SimulationSpeed.Four, SimulationSpeed.Eight, SimulationSpeed.One })
            {
                console.SubmitLine($"sim speed {(int)speed}");
                Require(!Status(app).IsPaused && Status(app).Speed == speed && Status(app).State == state && app.CreateSimulationCheckpoint() == checkpoint, "倍速命令修改了权威状态或未设置速度。");
            }
            var unchanged = Status(app);
            foreach (var input in new[] { "sim speed 3", "sim step 0", "sim step -1", "sim step 18446744073709551616", "sim speed 1 extra", "sim unknown" })
            {
                var usage = Logs(app, SimulationConsoleCommands.UsageEventId).Length;
                console.SubmitLine(input);
                Require(Logs(app, SimulationConsoleCommands.UsageEventId).Length == usage + 1 && Status(app) == unchanged && app.CreateSimulationCheckpoint() == checkpoint, $"非法输入改变了会话或缺少用法反馈：{input}");
            }
            console.SubmitLine("sim pause");
            Require(Status(app).IsPaused && Logs(app, SimulationConsoleCommands.PausedEventId).Length == 1, "暂停命令未更新会话。");
            advances.Clear();
            await WaitForFrames(() => advances.Count >= 2);
            Require(advances.All(result => result.StepsAdvanced == 0 && result.Events.IsEmpty && result.StateAfter == state) && Status(app).State == state, "暂停后真实帧仍推进或改变完整状态。");
            console.SubmitLine("sim resume");
            Require(!Status(app).IsPaused && Status(app).State == state && Logs(app, SimulationConsoleCommands.ResumedEventId).Length == 1, "恢复命令修改了状态或未取消暂停。");
            console.SubmitLine("sim pause");
            Require(Status(app).IsPaused && Status(app).State == state && Logs(app, SimulationConsoleCommands.PausedEventId).Length == 2, "再次暂停未保持完整状态。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Commands_steps_checkpoints_and_rebinding_preserve_snapshots()
    {
        var fixture = new Fixture(TestScene.GetTree());
        try
        {
            var app = await fixture.LoadAndAddScene<Train.App.App>("res://App/App.tscn");
            var console = app.GetChildren().OfType<GameConsole>().Single();
            var states = new ProbeStateCodec();
            app.AttachSession(CreateSession(states, new(73, "simulation-host-check-v1")), states);
            var snapshot = (ProbeState)Status(app).State;
            app.SetSimulationPaused(true);
            SubmissionResult.Completed? observed = null;
            app.SimulationCommandCompleted += result => observed = result;
            var submitted = app.SubmitCommand(new AddCommand(10), CommandSource.Automation);
            Require(submitted is SubmissionResult.Completed && submitted == observed, "宿主未发布命令完整结果。");
            var completed = (SubmissionResult.Completed)submitted;
            var commanded = (ProbeState)completed.StateAfter;
            Require(completed.Execution.Source == CommandSource.Automation && completed.Execution.CommandId == snapshot.NextCommandId && commanded.Counter == snapshot.Counter + 10 && completed.Events is [ProbeCommandEvent { Amount: 10 }], "命令来源、编号或业务结果错误。");
            console.SubmitLine("sim step 2");
            var stepped = (ProbeState)Status(app).State;
            Require(Status(app).IsPaused && stepped.CompletedSteps == snapshot.CompletedSteps + 2 && stepped.Counter == commanded.Counter + 2 && stepped.TransactionCount == snapshot.TransactionCount + 3 && Logs(app, SimulationConsoleCommands.SteppedEventId).Length == 1, "手动步未修改同一暂停会话。");
            console.SubmitLine("sim checkpoint");
            var checkpoint = app.CreateSimulationCheckpoint();
            var checkpointLog = Logs(app, SimulationConsoleCommands.CheckpointEventId).Single();
            Require(checkpoint is not null && checkpoint.Position == new SimulationPosition(stepped.CompletedSteps, stepped.BoundarySequence) && checkpointLog.GetProperty("attributes").GetProperty("sha256").GetString() == checkpoint.Sha256, "控制台与宿主检查点不一致。");
            Require(snapshot.CompletedSteps == 0 && snapshot.Counter == 0 && snapshot.NextCommandId == 1, "旧快照被修改。");
            app.SetSimulationSpeed(SimulationSpeed.Eight);
            app.AttachSession(CreateSession(states, new(91, "simulation-host-rebind-v1")), states);
            var rebound = Status(app);
            Require(!rebound.IsPaused && rebound.Speed == SimulationSpeed.One && rebound.PendingSteps == 0 && rebound.Remainder == 0 && rebound.ActualSpeed == 0 && rebound.State is ProbeState { WorldSeed: 91, CompletedSteps: 0, Counter: 0 } && snapshot.CompletedSteps == 0 && snapshot.Counter == 0, "重绑定未重置宿主设置或破坏旧快照。");
            app.DetachSession();
            Require(app.GetSimulationStatus() is null, "解绑后仍存在会话。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    public async Task Faults_roll_back_freeze_and_persist_one_diagnostic_per_failure()
    {
        var fixture = new Fixture(TestScene.GetTree());
        try
        {
            var app = await fixture.LoadAndAddScene<Train.App.App>("res://App/App.tscn");
            var states = new ProbeStateCodec();
            app.AttachSession(CreateSession(states, new(94, "simulation-fault-check-v1"), new(failStep: true)), states);
            await WaitForFrames(() => Status(app).State.Fault is not null);
            var failed = Status(app).State;
            Require(failed.CompletedSteps == 0 && failed.TransactionCount == 0, "自动故障未保留失败前完整边界。");
            VerifyFaultLog(app, 1, SimulationPhase.Evaluate, SimulationOperation.Step, "自检模拟步异常。");
            var frames = 0;
            app.SimulationAdvanced += _ => frames++;
            await WaitForFrames(() => frames >= 2);
            Require(Status(app).State == failed && Logs(app, Train.App.App.SimulationFaultEventId).Length == 1, "冻结帧改变状态或重复记录故障。");
            app.AttachSession(CreateSession(states, new(95, "simulation-fault-check-v1"), new(failStep: true)), states);
            app.SetSimulationPaused(true);
            var stepped = app.StepSimulation(2)!;
            Require(stepped.StepsAdvanced == 0 && stepped.Diagnostic is not null && stepped.Events.IsEmpty && stepped.StateAfter.CompletedSteps == 0 && stepped.StateAfter.TransactionCount == 0, "手动故障未回滚并返回诊断。");
            VerifyFaultLog(app, 2, SimulationPhase.Evaluate, SimulationOperation.Step, "自检模拟步异常。");
            Require(app.StepSimulation(1)!.Diagnostic is null && Logs(app, Train.App.App.SimulationFaultEventId).Length == 2, "冻结手动步重复返回或记录诊断。");
            app.AttachSession(CreateSession(states, new(96, "simulation-fault-check-v1"), new(failCommand: true)), states);
            app.SetSimulationPaused(true);
            var submitted = (SubmissionResult.Completed)app.SubmitCommand(new AddCommand(1), CommandSource.Console);
            Require(submitted.Execution.Outcome == CommandOutcome.Faulted && submitted.Diagnostic is not null && submitted.StateAfter.CompletedSteps == 0 && submitted.StateAfter.TransactionCount == 0 && submitted.Events.IsEmpty, "命令故障未回滚并返回诊断。");
            VerifyFaultLog(app, 3, SimulationPhase.Command, SimulationOperation.Command, "自检命令异常。");
            Require(app.SubmitCommand(new AddCommand(1), CommandSource.Console) is SubmissionResult.NotAccepted && Logs(app, Train.App.App.SimulationFaultEventId).Length == 3, "冻结拒收重复记录故障。");
            Require(Logs(app, Train.App.App.SimulationFaultEventId).All(item => !string.IsNullOrEmpty(item.GetProperty("exception").GetProperty("stack_trace").GetString())), "持久诊断丢失调用栈。");
        }
        finally { await fixture.Cleanup(); }
    }

    private async Task WaitForFrames(Func<bool> completed)
    {
        for (var frame = 0; frame < 600 && !completed(); frame++)
            await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
        Require(completed(), "真实 Godot 帧未在 600 帧内完成预期行为。");
    }

    private static SimulationStatus Status(Train.App.App app) => app.GetSimulationStatus() ?? throw new InvalidOperationException("预期存在模拟会话。");

    private static SimulationSession CreateSession(ProbeStateCodec states, ProbeState initial, ProbeCalculations? calculations = null)
    {
        var restored = states.Restore(states.Capture(initial), calculations ?? new(), new ProbeCommandCodec());
        Require(restored is DeterminismResult<SimulationSession>.Success, "测试会话未通过领域校验。");
        return ((DeterminismResult<SimulationSession>.Success)restored).Value;
    }

    private static JsonElement[] Logs(Train.App.App app, string eventId)
    {
        Require(app.Logger.JsonlPath is not null, "没有持久日志文件。");
        // Windows 读取正在写入的 JSONL 时，读取句柄也需允许现有写入。
        using var stream = new FileStream(app.Logger.JsonlPath!, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).Where(item => item.GetProperty("category").GetString() == SimulationConsoleCommands.Category && item.GetProperty("event_id").GetString() == eventId).ToArray();
    }

    private static void VerifyFaultLog(Train.App.App app, int count, SimulationPhase phase, SimulationOperation operation, string message)
    {
        var errors = Logs(app, Train.App.App.SimulationFaultEventId);
        Require(errors.Length == count, "故障未在发生调用中恰好记录一次。");
        var error = errors[^1];
        var attributes = error.GetProperty("attributes");
        Require(error.GetProperty("level").GetString() == "error" && attributes.GetProperty("fault_phase").GetString() == phase.ToString() && attributes.GetProperty("operation").GetString() == operation.ToString() && attributes.GetProperty("attempted_transaction_id").GetUInt64() == 1 && attributes.GetProperty("attempted_completed_steps").GetUInt64() == (operation == SimulationOperation.Step ? 1UL : 0UL) && attributes.GetProperty("attempted_sequence").GetUInt64() == (operation == SimulationOperation.Step ? 0UL : 1UL), "故障日志丢失阶段或失败事务位置。");
        var exception = error.GetProperty("exception");
        var details = new[] { exception }.Concat(exception.GetProperty("inner_exceptions").EnumerateArray()).ToArray();
        Require(details.Any(item => item.GetProperty("message").GetString() == message && !string.IsNullOrEmpty(item.GetProperty("stack_trace").GetString())) && details.Any(item => item.GetProperty("message").GetString() == "自检内部异常。"), "故障日志丢失消息、调用栈或内部异常链。");
    }

    private sealed record ProbeState(ulong Seed, string Version) : SimulationState(Seed, Version)
    {
        internal ulong Counter { get; init; }
    }

    private sealed record ProbeRequests : StepRequests;
    private sealed record ProbePlan : ResolvedStep;
    private sealed record AddCommand(ulong Amount) : CoreCommand;
    private sealed record ProbeStepEvent(ulong CompletedStep) : DomainEvent;
    private sealed record ProbeCommandEvent(ulong Amount) : DomainEvent;

    private sealed class ProbeCalculations(bool failStep = false, bool failCommand = false) : SimulationCalculations
    {
        internal List<int> ThreadIds { get; } = [];

        public override Transition ComputeStep(SimulationState before, TransactionContext context)
        {
            var computed = base.ComputeStep(before, context);
            return computed with
            {
                Events = ImmutableArray.Create<DomainEvent>(new ProbeStepEvent(context.Position.CompletedSteps))
            };
        }

        public override StepRequests Evaluate(SimulationState before, TransactionContext context)
        {
            ThreadIds.Add(System.Environment.CurrentManagedThreadId);
            if (failStep)
                throw new InvalidOperationException("自检模拟步异常。", new IOException("自检内部异常。"));
            return new ProbeRequests();
        }

        public override ResolvedStep Resolve(
            SimulationState before, StepRequests requests, TransactionContext context)
        {
            ThreadIds.Add(System.Environment.CurrentManagedThreadId);
            return new ProbePlan();
        }

        public override SimulationState Commit(
            SimulationState before, ResolvedStep plan, TransactionContext context)
        {
            ThreadIds.Add(System.Environment.CurrentManagedThreadId);
            var state = (ProbeState)before;
            return state with { Counter = checked(state.Counter + 1) };
        }

        public override CommandDecision ComputeCommand(
            SimulationState before, CoreCommand command, TransactionContext context)
        {
            ThreadIds.Add(System.Environment.CurrentManagedThreadId);
            if (failCommand)
                throw new InvalidOperationException("自检命令异常。", new IOException("自检内部异常。"));
            if (command is not AddCommand add)
                throw new InvalidOperationException("未知自检命令。");
            var state = (ProbeState)before;
            return new CommandDecision.Execute(new Transition(state with
            {
                Counter = checked(state.Counter + add.Amount),
                NextCommandId = checked(context.CommandId!.Value + 1),
                BoundarySequence = context.Position.Sequence,
                TransactionCount = context.TransactionId
            }, ImmutableArray.Create<DomainEvent>(new ProbeCommandEvent(add.Amount))));
        }
    }

    private sealed class ProbeCommandCodec : SimulationCommandCodec
    {
        protected override string GetKind(CoreCommand command) => command is AddCommand
            ? "check.add"
            : throw new ArgumentException("命令不属于自检格式。", nameof(command));

        protected override void WriteFields(Utf8JsonWriter writer, CoreCommand command) =>
            writer.WriteNumber("amount", ((AddCommand)command).Amount);

        protected override DeterminismResult<CoreCommand> ReadFields(string kind, JsonElement fields)
        {
            if (kind != "check.add")
                return Failure(DeterminismErrorKind.UnknownFormat, "unknown_command_kind", "/kind");
            var properties = fields.EnumerateObject().ToArray();
            if (properties.Length != 1 || properties[0].Name != "amount" ||
                !properties[0].Value.TryGetUInt64(out var amount) || amount == 0)
                return Failure(DeterminismErrorKind.InvalidData, "invalid_amount", "/amount");
            return new DeterminismResult<CoreCommand>.Success(new AddCommand(amount));
        }

        private static DeterminismResult<CoreCommand> Failure(
            DeterminismErrorKind kind, string code, string path) =>
            new DeterminismResult<CoreCommand>.Failure(new(kind, code, path));
    }

    private sealed class ProbeStateCodec : SimulationStateCodec
    {
        public override string StateKind => "check.counter";
        public override uint StateVersion => 1;

        protected override void WriteDomain(Utf8JsonWriter writer, SimulationState state) =>
            writer.WriteNumber("counter", ((ProbeState)state).Counter);

        protected override DeterminismResult<SimulationState> ReadDomain(
            SimulationState boundary, JsonElement fields)
        {
            var properties = fields.EnumerateObject().ToArray();
            if (properties.Length != 1 || properties[0].Name != "counter" ||
                !properties[0].Value.TryGetUInt64(out var counter))
                return new DeterminismResult<SimulationState>.Failure(new(
                    DeterminismErrorKind.InvalidData, "invalid_counter", "/counter"));
            return new DeterminismResult<SimulationState>.Success(new ProbeState(
                boundary.WorldSeed, boundary.GeneratorVersion)
            {
                CompletedSteps = boundary.CompletedSteps,
                BoundarySequence = boundary.BoundarySequence,
                NextCommandId = boundary.NextCommandId,
                NextEntityId = boundary.NextEntityId,
                TransactionCount = boundary.TransactionCount,
                Fault = boundary.Fault,
                Counter = counter
            });
        }

        protected override ImmutableArray<CheckpointObject> ProjectDomain(SimulationState state)
        {
            using var document = JsonDocument.Parse($"{{\"counter\":{((ProbeState)state).Counter}}}");
            return [new CheckpointObject(new("check.counter", 0), document.RootElement.Clone())];
        }
    }
}
