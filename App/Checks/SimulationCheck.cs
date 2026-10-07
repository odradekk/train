// Train.App.Checks 在真实 Godot 主线程验证模拟宿主与控制台接线。
// 文件内的计数领域只属于自检，不作为玩家世界或生产内容入口。

using System.Collections.Immutable;
using System.Text.Json;
using Godot;
using Train.Core.Logging;
using Train.Core.Simulation;
using Train.Ui;

namespace Train.App.Checks;

/// <summary>跨真实引擎帧验证一个临时模拟会话的自动推进、控制台操作和解绑。</summary>
internal sealed class SimulationCheck
{
    private readonly App _app;
    private readonly GameConsole _console;
    private readonly RingBufferLogSink _buffer;
    private readonly int _hostThreadId = System.Environment.CurrentManagedThreadId;
    private readonly ProbeCalculations _calculations = new();
    private readonly ProbeStateCodec _states = new();
    private readonly SimulationState _initialSnapshot;
    private int _frameNotifications;
    private ulong _automaticallyAdvanced;
    private ulong _publishedStepEvents;
    private SimulationState? _pausedState;
    private AdvanceResult? _pausedFrame;
    private bool _finished;

    /// <summary>验证无会话查询后，建立并挂接只供本次检查使用的已校验会话。</summary>
    internal SimulationCheck(App app, GameConsole console, TrainLogger logger, RingBufferLogSink buffer)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(buffer);
        _app = app;
        _console = console;
        _buffer = buffer;

        Require(_app.GetSimulationStatus() is null, "普通启动状态不应隐式创建模拟会话。");
        _console.SubmitLine("sim");
        Require(HasLog("simulation_session_unavailable"), "无会话查询没有经过真实控制台反馈。");
        Mark("SIMULATION_NO_SESSION");

        var initial = new ProbeState(73, "simulation-host-check-v1");
        var commands = new ProbeCommandCodec();
        var restored = _states.Restore(_states.Capture(initial), _calculations, commands);
        Require(restored is DeterminismResult<SimulationSession>.Success, "测试会话起点未通过领域校验。");
        var session = ((DeterminismResult<SimulationSession>.Success)restored).Value;
        _initialSnapshot = session.GetState();
        _app.SimulationAdvanced += ObserveAdvance;
        _app.AttachSession(session, _states);
        Require(_app.GetSimulationStatus()?.State == initial, "宿主没有发布刚挂接的完整状态。");
        Mark("SIMULATION_SESSION_ATTACHED");
    }

    /// <summary>先验证自动推进并暂停，下一帧确认零步与状态不变，再验证命令、手动步和解绑。</summary>
    /// <returns>尚需下一帧时为 null；完成时返回全部断言是否通过。</returns>
    internal bool? ObserveFrame(double delta)
    {
        if (_finished)
            return true;
        if (_pausedState is null && (_frameNotifications < 2 || _automaticallyAdvanced < 2))
            return null;

        try
        {
            if (_pausedState is null)
            {
                Require(_calculations.ThreadIds.Count > 0 &&
                    _calculations.ThreadIds.All(id => id == _hostThreadId),
                    "领域计算没有始终运行在创建 App 的托管线程。");
                Mark("SIMULATION_MAIN_THREAD");

                var running = RequireStatus();
                var runningState = (ProbeState)running.State;
                Require(runningState.Counter == runningState.CompletedSteps,
                    "自动推进的业务计数与完整步数不一致。");
                Require(runningState.TransactionCount == runningState.CompletedSteps,
                    "自动推进的事务计数与完整步数不一致。");
                Require(_publishedStepEvents == _automaticallyAdvanced,
                    "宿主返回的已提交步事件与实际推进数不一致。");
                Mark("SIMULATION_AUTO_ADVANCED");

                VerifyRunningConsoleCommands(running);
                _console.SubmitLine("sim pause");
                var paused = RequireStatus();
                Require(paused.IsPaused && HasLog("simulation_paused"), "暂停命令没有更新同一宿主会话。");
                _pausedState = paused.State;
                Mark("SIMULATION_PAUSED");
                return null;
            }

            if (_pausedFrame is null)
                return null;
            Require(_pausedFrame.StepsAdvanced == 0 && _pausedFrame.Events.IsEmpty &&
                _pausedFrame.StateAfter == _pausedState && RequireStatus().State == _pausedState,
                "暂停后的实际宿主帧仍推进了模拟或改变了完整状态。");
            var pausedState = _pausedState;
            VerifyResumeAndPause(pausedState);

            SubmissionResult.Completed? observedCommand = null;
            void OnCommand(SubmissionResult.Completed result) => observedCommand = result;
            _app.SimulationCommandCompleted += OnCommand;
            var submitted = _app.SubmitCommand(new AddCommand(10), CommandSource.Automation);
            _app.SimulationCommandCompleted -= OnCommand;
            Require(submitted is SubmissionResult.Completed completed && completed == observedCommand,
                "宿主没有发布命令的完整结果。");
            completed = (SubmissionResult.Completed)submitted;
            var commanded = (ProbeState)completed.StateAfter;
            Require(completed.Execution.Source == CommandSource.Automation &&
                completed.Execution.CommandId == pausedState.NextCommandId &&
                commanded.Counter == ((ProbeState)pausedState).Counter + 10 &&
                completed.Events is [ProbeCommandEvent { Amount: 10 }],
                "自动化命令的来源、编号或业务结果错误。");
            Mark("SIMULATION_COMMAND_COMPLETED");

            _console.SubmitLine("sim step 2");
            var stepped = RequireStatus();
            var steppedState = (ProbeState)stepped.State;
            Require(stepped.IsPaused && steppedState.CompletedSteps == pausedState.CompletedSteps + 2 &&
                steppedState.Counter == commanded.Counter + 2 &&
                steppedState.TransactionCount == pausedState.TransactionCount + 3 &&
                HasLog("simulation_stepped"), "控制台手动步没有修改同一暂停会话。");
            Mark("SIMULATION_CONSOLE_STEP");

            _console.SubmitLine("sim checkpoint");
            var checkpoint = _app.CreateSimulationCheckpoint();
            var checkpointLog = _buffer.Snapshot().LastOrDefault(item =>
                item.Category == "simulation" && item.EventId == "simulation_checkpoint");
            Require(checkpoint is not null && checkpoint.Position ==
                new SimulationPosition(steppedState.CompletedSteps, steppedState.BoundarySequence) &&
                checkpointLog is not null &&
                checkpointLog.Attributes.TryGetValue("sha256", out var loggedSha256) &&
                loggedSha256 is string sha256 && sha256 == checkpoint.Sha256,
                "控制台与宿主没有观察到同一检查点摘要。");
            Mark("SIMULATION_CHECKPOINT");

            var snapshot = (ProbeState)_initialSnapshot;
            Require(snapshot.CompletedSteps == 0 &&
                snapshot.Counter == 0 && snapshot.NextCommandId == 1,
                "会话发布修改了已保留的旧状态快照。");
            Mark("SIMULATION_SNAPSHOT_STABLE");

            _app.SimulationAdvanced -= ObserveAdvance;
            var replacement = CreateValidatedSession(new ProbeState(91, "simulation-host-rebind-v1"));
            _app.AttachSession(replacement, _states);
            var rebound = RequireStatus();
            Require(!rebound.IsPaused && rebound.Speed == SimulationSpeed.One &&
                rebound.PendingSteps == 0 && rebound.Remainder == 0 && rebound.ActualSpeed == 0 &&
                rebound.State is ProbeState { WorldSeed: 91, CompletedSteps: 0, Counter: 0 } &&
                snapshot.CompletedSteps == 0 && snapshot.Counter == 0,
                "重新绑定没有恢复默认宿主设置，或旧完整快照失效。");
            _app.DetachSession();
            Require(_app.GetSimulationStatus() is null, "解绑后仍可查询到模拟会话。");
            Mark("SIMULATION_SESSION_DETACHED");
            _finished = true;
            return true;
        }
        catch
        {
            _app.SimulationAdvanced -= ObserveAdvance;
            throw;
        }
    }

    private void ObserveAdvance(AdvanceResult result)
    {
        if (_pausedState is not null)
        {
            if (_pausedFrame is null)
                _pausedFrame = result;
            return;
        }
        _frameNotifications++;
        _automaticallyAdvanced += result.StepsAdvanced;
        Require(result.Events.All(item => item is ProbeStepEvent), "宿主返回了不属于测试领域的步事件。");
        _publishedStepEvents += (ulong)result.Events.Length;
    }

    private SimulationStatus RequireStatus() =>
        _app.GetSimulationStatus() ?? throw new InvalidOperationException("预期存在活动模拟会话。");

    /// <summary>经真实控制台验证运行中手动步、倍速和非法参数不会越过各自边界。</summary>
    private void VerifyRunningConsoleCommands(SimulationStatus running)
    {
        var authoritative = running.State;
        var checkpoint = _app.CreateSimulationCheckpoint()!;
        var requiresPause = CountLogs("simulation_requires_pause");
        _console.SubmitLine("sim step");
        var afterStep = RequireStatus();
        Require(CountLogs("simulation_requires_pause") == requiresPause + 1 &&
            afterStep == running && _app.CreateSimulationCheckpoint() == checkpoint,
            "运行中的 sim step 隐式暂停、修改状态，或没有报告必须暂停。");

        foreach (var (text, speed) in new[]
        {
            ("1", SimulationSpeed.One),
            ("2", SimulationSpeed.Two),
            ("4", SimulationSpeed.Four),
            ("8", SimulationSpeed.Eight),
            ("1", SimulationSpeed.One)
        })
        {
            _console.SubmitLine($"sim speed {text}");
            var changed = RequireStatus();
            Require(!changed.IsPaused && changed.Speed == speed && changed.State == authoritative &&
                _app.CreateSimulationCheckpoint() == checkpoint,
                $"sim speed {text} 没有准确设置倍速，或修改了权威状态与检查点。");
        }

        var unchanged = RequireStatus();
        foreach (var input in new[]
        {
            "sim speed 3",
            "sim step 0",
            "sim step -1",
            "sim step 18446744073709551616",
            "sim speed 1 extra",
            "sim unknown"
        })
        {
            var usage = CountLogs("simulation_usage");
            _console.SubmitLine(input);
            Require(CountLogs("simulation_usage") == usage + 1 && RequireStatus() == unchanged &&
                _app.CreateSimulationCheckpoint() == checkpoint,
                $"非法控制台输入改变了模拟，或没有报告用法：{input}");
        }
    }

    /// <summary>在已证明暂停帧为零步后，经真实控制台继续并再次暂停同一完整状态。</summary>
    private void VerifyResumeAndPause(SimulationState expected)
    {
        _console.SubmitLine("sim resume");
        var resumed = RequireStatus();
        Require(!resumed.IsPaused && resumed.State == expected && HasLog("simulation_resumed"),
            "sim resume 没有取消暂停，或改变了完整状态。");
        _console.SubmitLine("sim pause");
        var paused = RequireStatus();
        Require(paused.IsPaused && paused.State == expected && HasLog("simulation_paused"),
            "再次 sim pause 没有保持同一完整状态。");
    }

    /// <summary>用独立领域计算验证起点，并返回可整体重新挂接的新会话。</summary>
    private SimulationSession CreateValidatedSession(ProbeState initial)
    {
        var restored = _states.Restore(_states.Capture(initial), new ProbeCalculations(), new ProbeCommandCodec());
        Require(restored is DeterminismResult<SimulationSession>.Success, "重新绑定的测试会话起点无效。");
        return ((DeterminismResult<SimulationSession>.Success)restored).Value;
    }

    private bool HasLog(string eventId) => _buffer.Snapshot().Any(item =>
        item.Category == "simulation" && item.EventId == eventId);

    private int CountLogs(string eventId) => _buffer.Snapshot().Count(item =>
        item.Category == "simulation" && item.EventId == eventId);

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Mark(string marker) => GD.Print(marker);

    private sealed record ProbeState(ulong Seed, string Version) : SimulationState(Seed, Version)
    {
        internal ulong Counter { get; init; }
    }

    private sealed record ProbeRequests : StepRequests;
    private sealed record ProbePlan : ResolvedStep;
    private sealed record AddCommand(ulong Amount) : CoreCommand;
    private sealed record ProbeStepEvent(ulong CompletedStep) : DomainEvent;
    private sealed record ProbeCommandEvent(ulong Amount) : DomainEvent;

    private sealed class ProbeCalculations : SimulationCalculations
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
