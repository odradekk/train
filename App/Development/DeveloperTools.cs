using System.Collections.Immutable;
using System.Diagnostics;
using Train.App.Saves;
using Train.Core.Contracts;

namespace Train.App.Development;

internal abstract record DeveloperCondition
{
    internal sealed record AtStep(ulong Step) : DeveloperCondition;
    internal sealed record Rejected(string Kind, WorldObjectId? Subject) : DeveloperCondition;
    internal sealed record ObjectFault(WorldObjectId Subject) : DeveloperCondition;
    internal sealed record Event(string EventType) : DeveloperCondition;
}
internal sealed record DeveloperCheckpoint(string Label, SimulationPosition Position, ulong WorldSeed,
    RuntimeIdentity Runtime, CoreInputId InputIdentity, CoreCheckpoint Checkpoint);
internal sealed record DeveloperMark(string Text, SimulationPosition Position, HostBinding Binding);
internal sealed record DeveloperCommandWindow(SimulationPosition From, SimulationPosition Until,
    ImmutableArray<RecordedCommand> Commands, ImmutableArray<RecordingGap> Gaps, bool Replay);

internal sealed class DeveloperTools : IDisposable
{
    private readonly HostCoordinator _host;
    private readonly SaveService _saves;
    private readonly Func<double> _seconds;
    private readonly List<DeveloperCheckpoint> _checkpoints = [];
    private readonly List<DeveloperMark> _marks = [];
    private readonly List<string> _stops = [];
    private Run? _run;
    private long? _replayOperation;
    private bool _exporting;
    private Action? _openReplay;
    private bool _frozenStop;
    private bool _disposed;

    internal DeveloperTools(HostCoordinator host, SaveService saves, Func<double>? seconds = null)
    {
        _host = host; _saves = saves;
        _seconds = seconds ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        host.BoundaryCompleted += OnBoundary;
        host.ReplacementCompleted += OnReplacement;
        saves.Changed += OnSaveChanged;
    }
    internal HostStatus? Status => _host.GetStatus();
    internal bool Running => _run is not null;
    internal bool IsBusy => _host.IsBusy || _saves.IsBusy || _exporting || _replayOperation is not null;
    internal string? LastMessage { get; private set; }
    internal IReadOnlyList<string> StopReasons => _stops;
    internal IReadOnlyList<DeveloperCheckpoint> Checkpoints => _checkpoints;
    internal IReadOnlyList<DeveloperMark> Marks => _marks;
    internal ObjectInspection? Inspect(WorldObjectId id) => Status is { } status ? InspectorData.Inspect(status.View, id) : null;

    internal DeveloperCommandWindow? ReadCommands()
    {
        if (Status is not { } status) return null;
        if (_host.ReplayRecording is { } replay)
            return new(replay.From, replay.Until, replay.Commands, replay.Gaps, true);
        var window = _host.ReadRecording(status.Binding);
        return window is null ? null : new(window.Starts.FirstOrDefault()?.State.Position ?? window.EndPosition,
            window.EndPosition, window.Commands, window.Gaps, false);
    }

    internal SaveFailure? StartSteps(ulong steps) => steps == 0 ? Fail("invalid-steps", "步数必须大于零。")
        : Start(null, steps, null);
    internal SaveFailure? AdvanceTo(SimulationPosition position)
    {
        if (Status is not { IsReplay: true } status) return Fail("no-replay", "推进到位置只用于重放。");
        if (HostCoordinator.ComparePosition(position, status.View.Position) <= 0)
            return Fail("invalid-position", "目标位置必须晚于当前边界。");
        if (_host.ReplayRecording is { } recording && HostCoordinator.ComparePosition(position, recording.Until) > 0)
            return Fail("invalid-position", "目标位置超过录制截止边界。");
        if (position.Sequence != 0 && !_host.ReplayRecording!.Commands.Any(command => command.Result.After == position))
            return Fail("invalid-position", "目标序号不是已录制命令后的完整边界。");
        return Start(null, null, position);
    }
    internal SaveFailure? StartCondition(DeveloperCondition condition, ulong? maximumSteps = null)
    {
        if (Status is not { IsReplay: false } status) return Fail("no-session", "条件运行只用于普通会话。");
        if (condition is DeveloperCondition.AtStep step && step.Step <= status.View.Position.CompletedSteps)
            return Fail("invalid-condition", "目标步已经到达或过去。");
        if (condition is DeveloperCondition.Rejected rejected && string.IsNullOrWhiteSpace(rejected.Kind))
            return Fail("invalid-condition", "请输入命令负载的 kind。");
        if (condition is DeveloperCondition.Event eventCondition &&
            typeof(CoreEvent).GetNestedType(eventCondition.EventType) is null)
            return Fail("invalid-condition", "请输入已声明的 CoreEvent 类型名。");
        if (Target(condition) is { } target && !InspectorData.Exists(status.View, target))
            return Fail("invalid-target", $"目标无效：{target}");
        return Start(condition, maximumSteps, null);
    }

    private SaveFailure? Start(DeveloperCondition? condition, ulong? limit, SimulationPosition? stopAt)
    {
        if (Running || _host.IsBusy || _saves.IsBusy || _exporting) return Fail("busy", "正在处理其他操作。");
        if (Status is not { } status) return Fail("no-session", "当前没有会话。");
        if (status.IsFrozen) return Fail("frozen", "核心已冻结，不能继续推进。");
        if (limit == 0) return Fail("invalid-steps", "最大步数必须大于零，或留空。");
        if (_host.SetPaused(true) is { } problem) return Fail(problem.Code, "无法暂停当前会话。");
        _stops.Clear();
        _frozenStop = false;
        _run = new(status.Binding, status.View.Position, condition, limit, stopAt, status.IsReplay);
        LastMessage = "正在逐步推进，可取消。";
        return null;
    }

    internal void Cancel()
    {
        if (_run is not null) Stop("已取消");
        if (_replayOperation is { } operation) _host.Cancel(operation);
        if (_openReplay is not null) { _saves.ChooseLeave(LeaveChoice.Cancel); _openReplay = null; }
        if (_exporting) _saves.Cancel();
    }

    internal SaveFailure? Pause()
    {
        if (_run is not null) Stop("已暂停");
        return _host.SetPaused(true) is { } problem ? Fail(problem.Code, "当前无法暂停。") : null;
    }

    internal void Process()
    {
        if (_run is not { } run) return;
        if (Status is not { } status || status.Binding != run.Binding) { Stop("会话已切换，条件已清除"); return; }
        if (_stops.Count > 0) { Stop(); return; }
        if (_host.IsBusy || status.OperationLocked) return;
        var started = _seconds();
        do
        {
            if (run.Replay)
            {
                var before = status.View.Position;
                ReplayAdvanceResult? result;
                try { result = _host.AdvanceReplay(1, run.StopAt); }
                catch (ArgumentOutOfRangeException) { Stop("目标重放位置无效"); return; }
                if (result is null) { Stop("重放无法继续推进"); return; }
                status = Status!;
                if (status.IsFrozen) AddFrozenStop();
                if (result.Stop != ReplayStop.BudgetReached) AddStop(ReplayStopMessage(result));
                if (_host.LastNotification == NotificationAction.Pause) AddStop("通知要求暂停");
                if (run.Limit is { } maximum && status.View.Position.CompletedSteps - run.Start.CompletedSteps >= maximum)
                    AddStop("已完成指定步数");
                if (_stops.Count > 0) { Stop(); return; }
                if (before == status.View.Position) { Stop("重放未推进"); return; }
            }
            else
            {
                var result = _host.Step(1);
                if (_stops.Count > 0) { Stop(); return; }
                if (result.Problem is { } problem) { Stop(problem.Code); return; }
                if (result.Completed == 0) { Stop("模拟未推进"); return; }
            }
        } while (_seconds() - started < SimulationFrameDriver.FrameBudgetSeconds);
    }

    private void OnBoundary(HostBinding binding, ICoreReadView view, Train.Content.Contracts.IContentView content,
        ImmutableArray<EventEnvelope> events)
    {
        if (_run is not { } run) return;
        if (binding != run.Binding) { Stop("会话已切换，条件已清除"); return; }
        if (run.Replay) return;
        if (view.Fault is { } fault)
        {
            if (run.Condition is DeveloperCondition.ObjectFault target && fault.Subject == target.Subject) AddStop("目标对象发生内部故障");
            AddFrozenStop();
        }
        if (run.Condition is DeveloperCondition.AtStep step && view.Position.CompletedSteps >= step.Step)
            AddStop("已到达目标步");
        if (run.Condition is DeveloperCondition.Event eventCondition && events.Any(item => item.Event.GetType().Name == eventCondition.EventType))
            AddStop("已发生领域事件：" + eventCondition.EventType);
        if (run.Condition is DeveloperCondition.Rejected rejected && _host.ReadRecording(binding) is { } recording &&
            InspectorData.ReadCommands(recording.Commands.Where(command =>
                HostCoordinator.ComparePosition(command.Result.After, run.Start) > 0).ToImmutableArray(),
                rejected.Kind, "rejected", rejected.Subject).Length > 0)
            AddStop("匹配命令被拒绝");
        if (Target(run.Condition) is { } subject && !InspectorData.Exists(view, subject))
            AddStop("目标失效：" + subject);
        if (run.Limit is { } limit && view.Position.CompletedSteps - run.Start.CompletedSteps >= limit)
            AddStop(run.Condition is null ? "已完成指定步数" : "已达到最大步数");
        if (_host.LastNotification == NotificationAction.Pause) AddStop("通知要求暂停");
        if (_stops.Count > 0) _host.RequestPauseAfterBoundary();
    }
    private static WorldObjectId? Target(DeveloperCondition? condition) => condition switch
    { DeveloperCondition.ObjectFault fault => fault.Subject, DeveloperCondition.Rejected rejected => rejected.Subject, _ => null };
    private void AddStop(string reason) { if (!_stops.Contains(reason)) _stops.Add(reason); }
    private void AddFrozenStop()
    {
        if (_frozenStop) return;
        _frozenStop = true;
        _stops.Insert(0, "核心已冻结");
    }
    private void Stop(string? reason = null)
    {
        if (reason is not null) AddStop(reason);
        _run = null;
        LastMessage = string.Join("；", _stops);
    }

    internal SaveFailure? CaptureCheckpoint(string label)
    {
        if (Status is not { } status || _host.Runtime is not { } runtime) return Fail("no-session", "当前没有会话。");
        var result = _host.CaptureCheckpoint();
        if (result is not CoreResult<ICoreCapture>.Success capture)
            return result switch
            {
                CoreResult<ICoreCapture>.Failure failure => Fail(failure.Problems[0].Code,
                    "无法捕获检查点：" + string.Join("；", failure.Problems.Select(problem => $"{problem.Code}/{problem.Field}"))),
                CoreResult<ICoreCapture>.Faulted fault => Fail(fault.Fault.Code,
                    $"无法捕获检查点：{fault.Fault.Code}/{fault.Fault.Operation}/{fault.Fault.Field}" +
                    (fault.Diagnostic is { } diagnostic ? $" · {diagnostic.Message}" : "")),
                _ => throw new InvalidOperationException("未知检查点捕获结果。")
            };
        _checkpoints.Add(new(label, capture.Value.Position, status.View.World.WorldSeed,
            runtime, capture.Value.InputIdentity, capture.Value.Checkpoint));
        LastMessage = $"检查点已保存在内存：{label} · {capture.Value.Position}";
        return null;
    }
    internal SaveFailure? AddMark(string text)
    {
        if (Status is not { } status) return Fail("no-session", "当前没有会话。");
        if (string.IsNullOrWhiteSpace(text)) return Fail("invalid-mark", "说明不能为空。");
        _marks.Add(new(text.Trim(), status.View.Position, status.Binding));
        LastMessage = "说明标记已保存在本进程内存。";
        return null;
    }
    internal string Compare(int expected, int actual)
    {
        var left = _checkpoints[expected]; var right = _checkpoints[actual];
        if (left.Position != right.Position) return LastMessage = "不可比：模拟位置不同。";
        if (left.WorldSeed != right.WorldSeed) return LastMessage = "不可比：世界来源不同。";
        if (left.Runtime != right.Runtime) return LastMessage = "不可比：运行环境不同。";
        if (left.InputIdentity != right.InputIdentity) return LastMessage = "内容适配：核心输入不同。";
        return LastMessage = _host.CompareCheckpoints(left.Checkpoint, right.Checkpoint) switch
        {
            CoreResult<CheckpointComparison>.Success { Value: CheckpointComparison.Equal } => "检查点一致。",
            CoreResult<CheckpointComparison>.Success { Value: CheckpointComparison.Different difference } =>
                $"首个差异：{difference.Difference.Position} · {difference.Difference.Field} · " +
                $"{difference.Difference.Expected} → {difference.Difference.Actual}",
            CoreResult<CheckpointComparison>.Failure failure => $"检查点比较被拒绝：{failure.Problems[0].Code}",
            CoreResult<CheckpointComparison>.Faulted fault => $"检查点比较失败：{fault.Fault.Code}",
            _ => throw new InvalidOperationException("未知检查点比较结果。")
        };
    }

    internal SaveFailure? OpenReplay(string path)
    {
        if (IsBusy) return Fail("busy", "正在处理其他操作。");
        if (_run is not null) Stop("打开重放，条件已清除");
        _openReplay = () =>
        {
            _openReplay = null;
            if (_disposed) return;
            var result = _host.BeginReplay(path);
            if (result.Problem is { } problem) Fail(problem.Code, "无法开始重放准备。");
            else { _replayOperation = result.Operation; LastMessage = "正在准备隔离重放…"; }
        };
        var failure = _saves.RequestLeave(_openReplay);
        if (failure is not null) _openReplay = null;
        return failure;
    }
    internal SaveFailure? ExportReplayBoundary(string path, bool overwriteConfirmed = false)
    {
        if (Running || IsBusy) return Fail("busy", "请先停止推进及其他操作。");
        if (Status is not { IsReplay: true }) return Fail("no-replay", "当前不是重放会话。");
        _exporting = true;
        var failure = _saves.Export(path, overwriteConfirmed);
        LastMessage = _saves.LastMessage;
        if (failure is not null) _exporting = false;
        return failure;
    }
    private void OnSaveChanged()
    {
        if (_openReplay is not null && _saves.Leaving is null) _openReplay = null;
        if (!_exporting) return;
        LastMessage = _saves.LastMessage;
        if (!_saves.HasPendingWork) _exporting = false;
    }
    private void OnReplacement(long operation, bool succeeded)
    {
        if (_replayOperation != operation) return;
        _replayOperation = null;
        LastMessage = succeeded ? $"隔离重放已打开：{_host.ReplayRecording!.Path}"
            : "重放准备失败或已取消，保留原会话。";
    }
    private static string ReplayStopMessage(ReplayAdvanceResult result) =>
        $"重放停止：{result.Stop} · {result.Position.CompletedSteps}:{result.Position.Sequence}" +
        (result.Difference is { } difference ? $" · {difference.Field}: {difference.Expected} → {difference.Actual}" : "") +
        (result.Fault is { } fault ? $" · {fault.Code}" : "");
    private SaveFailure Fail(string code, string message) { LastMessage = message; return new(code, message); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();
        _host.BoundaryCompleted -= OnBoundary;
        _host.ReplacementCompleted -= OnReplacement;
        _saves.Changed -= OnSaveChanged;
    }
    private sealed record Run(HostBinding Binding, SimulationPosition Start, DeveloperCondition? Condition,
        ulong? Limit, SimulationPosition? StopAt, bool Replay);
}
