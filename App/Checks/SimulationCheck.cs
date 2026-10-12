
using System.Collections.Immutable;
using System.Text.Json;
using Godot;
using Train.App.Logging;
using Train.Core.Simulation;
using Train.Ui;

namespace Train.App.Checks;

internal sealed class SimulationCheck
{
    private readonly App _app;
    private readonly GameConsole _console;
    private readonly RingBufferLogSink _buffer;
    private readonly TrainLogger _logger;
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
    private bool _checkingFaults;
    private SimulationState? _automaticFaultState;
    private int _faultLogsAtStart;

    internal SimulationCheck(App app, GameConsole console, TrainLogger logger, RingBufferLogSink buffer)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(buffer);
        _app = app;
        _console = console;
        _buffer = buffer;
        _logger = logger;

        Require(_app.GetSimulationStatus() is null, "普通启动状态不应隐式创建模拟会话。");
        _console.SubmitLine("sim");
        Require(HasLog(SimulationConsoleCommands.SessionUnavailableEventId), "无会话查询没有经过真实控制台反馈。");
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

    internal bool? ObserveFrame(double delta)
    {
        if (_finished)
            return true;
        if (_checkingFaults)
            return ObserveFaults();
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
                Require(paused.IsPaused && HasLog(SimulationConsoleCommands.PausedEventId), "暂停命令没有更新同一宿主会话。");
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
                HasLog(SimulationConsoleCommands.SteppedEventId), "控制台手动步没有修改同一暂停会话。");
            Mark("SIMULATION_CONSOLE_STEP");

            _console.SubmitLine("sim checkpoint");
            var checkpoint = _app.CreateSimulationCheckpoint();
            var checkpointLog = _buffer.Snapshot().LastOrDefault(item =>
                item.Category == SimulationConsoleCommands.Category &&
                item.EventId == SimulationConsoleCommands.CheckpointEventId);
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
            _faultLogsAtStart = CountLogs(App.SimulationFaultEventId);
            _app.AttachSession(CreateValidatedSession(new ProbeState(94, "simulation-fault-check-v1"),
                new ProbeCalculations(failStep: true)), _states);
            _checkingFaults = true;
            return null;
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

    private void VerifyRunningConsoleCommands(SimulationStatus running)
    {
        var authoritative = running.State;
        var checkpoint = _app.CreateSimulationCheckpoint()!;
        var requiresPause = CountLogs(SimulationConsoleCommands.RequiresPauseEventId);
        _console.SubmitLine("sim step");
        var afterStep = RequireStatus();
        Require(CountLogs(SimulationConsoleCommands.RequiresPauseEventId) == requiresPause + 1 &&
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
            var usage = CountLogs(SimulationConsoleCommands.UsageEventId);
            _console.SubmitLine(input);
            Require(CountLogs(SimulationConsoleCommands.UsageEventId) == usage + 1 && RequireStatus() == unchanged &&
                _app.CreateSimulationCheckpoint() == checkpoint,
                $"非法控制台输入改变了模拟，或没有报告用法：{input}");
        }
    }

    private void VerifyResumeAndPause(SimulationState expected)
    {
        _console.SubmitLine("sim resume");
        var resumed = RequireStatus();
        Require(!resumed.IsPaused && resumed.State == expected && HasLog(SimulationConsoleCommands.ResumedEventId),
            "sim resume 没有取消暂停，或改变了完整状态。");
        _console.SubmitLine("sim pause");
        var paused = RequireStatus();
        Require(paused.IsPaused && paused.State == expected && HasLog(SimulationConsoleCommands.PausedEventId),
            "再次 sim pause 没有保持同一完整状态。");
    }

    private SimulationSession CreateValidatedSession(ProbeState initial, ProbeCalculations? calculations = null)
    {
        var restored = _states.Restore(_states.Capture(initial), calculations ?? new ProbeCalculations(), new ProbeCommandCodec());
        Require(restored is DeterminismResult<SimulationSession>.Success, "重新绑定的测试会话起点无效。");
        return ((DeterminismResult<SimulationSession>.Success)restored).Value;
    }

    private bool HasLog(string eventId) => _buffer.Snapshot().Any(item =>
        item.Category == SimulationConsoleCommands.Category && item.EventId == eventId);

    private int CountLogs(string eventId) => _buffer.Snapshot().Count(item =>
        item.Category == SimulationConsoleCommands.Category && item.EventId == eventId);

    private bool? ObserveFaults()
    {
        var state = RequireStatus().State;
        if (state.Fault is null)
            return null;
        if (_automaticFaultState is null)
        {
            Require(state.CompletedSteps == 0 && state.TransactionCount == 0,
                "自动故障没有保留失败步之前的完整边界。");
            VerifyFaultLog(1, SimulationPhase.Evaluate, SimulationOperation.Step, "自检模拟步异常。");
            _automaticFaultState = state;
            return null;
        }
        Require(state == _automaticFaultState && CountLogs(App.SimulationFaultEventId) == _faultLogsAtStart + 1,
            "冻结后的自动帧改变了状态，或重复记录故障。");
        Mark("SIMULATION_AUTO_FAULT_DIAGNOSTIC");

        _app.AttachSession(CreateValidatedSession(new ProbeState(95, "simulation-fault-check-v1"),
            new ProbeCalculations(failStep: true)), _states);
        _app.SetSimulationPaused(true);
        var stepped = _app.StepSimulation(2)!;
        Require(stepped.StepsAdvanced == 0 && stepped.Diagnostic is not null && stepped.Events.IsEmpty,
            "手动步故障没有回滚或交出诊断。");
        VerifyFaultLog(2, SimulationPhase.Evaluate, SimulationOperation.Step, "自检模拟步异常。");
        Require(_app.StepSimulation(1)!.Diagnostic is null &&
            CountLogs(App.SimulationFaultEventId) == _faultLogsAtStart + 2,
            "冻结后的手动步重复返回或记录诊断。");
        Mark("SIMULATION_MANUAL_FAULT_DIAGNOSTIC");

        _app.AttachSession(CreateValidatedSession(new ProbeState(96, "simulation-fault-check-v1"),
            new ProbeCalculations(failCommand: true)), _states);
        _app.SetSimulationPaused(true);
        var submitted = (SubmissionResult.Completed)_app.SubmitCommand(new AddCommand(1), CommandSource.Console);
        Require(submitted.Execution.Outcome == CommandOutcome.Faulted && submitted.Diagnostic is not null &&
            submitted.StateAfter.CompletedSteps == 0 && submitted.StateAfter.TransactionCount == 0 &&
            submitted.Events.IsEmpty, "命令故障没有回滚并返回诊断。");
        VerifyFaultLog(3, SimulationPhase.Command, SimulationOperation.Command, "自检命令异常。");
        Require(_app.SubmitCommand(new AddCommand(1), CommandSource.Console) is SubmissionResult.NotAccepted &&
            CountLogs(App.SimulationFaultEventId) == _faultLogsAtStart + 3,
            "冻结后的拒收重复记录故障。");

        Require(_logger.JsonlPath is not null, "故障诊断没有持久日志文件。");
        // 日志写句柄尚未关闭；读取句柄也必须允许现有写入，避免 Windows 的双向共享检查拒绝打开。
        using var stream = new FileStream(_logger.JsonlPath!, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var persisted = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).Where(item => item.GetProperty("event_id").GetString() == App.SimulationFaultEventId).ToArray();
        Require(persisted.Length == _faultLogsAtStart + 3 && persisted.All(item =>
            !string.IsNullOrEmpty(item.GetProperty("exception").GetProperty("stack_trace").GetString())),
            "新故障没有分别持久记录一次，或诊断丢失调用栈。");
        Mark("SIMULATION_COMMAND_FAULT_DIAGNOSTIC");
        _app.DetachSession();
        _finished = true;
        return true;
    }

    private void VerifyFaultLog(int added, SimulationPhase phase, SimulationOperation operation, string message)
    {
        var errors = _buffer.Snapshot().Where(item => item.Category == SimulationConsoleCommands.Category &&
            item.EventId == App.SimulationFaultEventId).ToArray();
        Require(errors.Length == _faultLogsAtStart + added, "内部故障没有在发生的那次调用记录一条日志。");
        var error = errors[^1];
        Require(error.Level == LogLevel.Error && error.Exception is not null &&
            Equals(error.Attributes["fault_phase"], phase.ToString()) &&
            Equals(error.Attributes["operation"], operation.ToString()) &&
            Equals(error.Attributes["attempted_transaction_id"], 1UL) &&
            Equals(error.Attributes["attempted_completed_steps"], operation == SimulationOperation.Step ? 1UL : 0UL) &&
            Equals(error.Attributes["attempted_sequence"], operation == SimulationOperation.Step ? 0UL : 1UL),
            "错误日志没有保留故障阶段与失败事务的位置。");
        var details = new[] { error.Exception! }.Concat(error.Exception!.InnerExceptions).ToArray();
        Require(details.Any(item => item.Message == message && !string.IsNullOrEmpty(item.StackTrace)) &&
            details.Any(item => item.Message == "自检内部异常。"),
            "错误日志没有保留原始异常消息、调用栈或内部异常链。");
    }

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
