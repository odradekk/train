// Train.Core.Simulation 将宿主时间换算为同一会话的完整模拟步。
// 暂停、倍速、工作预算和速度测量属于宿主，不进入权威状态或检查点。

using System.Collections.Immutable;
using System.Diagnostics;

namespace Train.Core.Simulation;

/// <summary>表示主机每秒真实时间希望推进的模拟秒数，不改变 1/60 秒步长。</summary>
/// <example>Two 将 1/30 秒真实间隔换算为四个完整模拟步。</example>
public enum SimulationSpeed
{
    /// <summary>每秒真实时间推进一个模拟秒。</summary>
    One = 1,
    /// <summary>每秒真实时间推进两个模拟秒。</summary>
    Two = 2,
    /// <summary>每秒真实时间推进四个模拟秒。</summary>
    Four = 4,
    /// <summary>每秒真实时间推进八个模拟秒；实际速度仍取决于工作预算。</summary>
    Eight = 8
}

/// <summary>表示同一会话在完整边界上的只读核心状态及宿主观察值。</summary>
/// <param name="State">最后完整的不可变权威状态；故障保留在该状态中。</param>
/// <param name="IsPaused">主机是否暂停自动推进，不禁止核心命令。</param>
/// <param name="Speed">目标倍速，保持 1/2/4/8 的固定档位。</param>
/// <param name="ActualSpeed">最近采样窗口内自动完成的模拟秒除以真实秒；暂停或冻结时为零。</param>
/// <param name="PendingSteps">时间已累计但工作预算内尚未完成的整步数。</param>
/// <param name="Remainder">不足一个模拟步的剩余模拟秒，不受暂停或切换倍速清除。</param>
/// <example>完成 120 步的状态对应 2 个模拟秒；目标 8× 不保证测得速度为 8×。</example>
public sealed record SimulationStatus(
    SimulationState State, bool IsPaused, SimulationSpeed Speed,
    double ActualSpeed, ulong PendingSteps, double Remainder)
{
    /// <summary>由已完成步数换算的模拟秒，不使用墙钟推算权威进度。</summary>
    public double SimulationSeconds => State.CompletedSteps / SimulationClock.StepsPerSecond;
}

/// <summary>在创建它的宿主线程上驱动一个已验证会话，并返回完整结果。</summary>
/// <remarks>只拥有主机设置、调度余量和测量值；会话与状态格式必须属于同一领域。调用方负责消费返回的已提交事件。</remarks>
/// <example>应推进四步，每步耗时 3 ms、帧预算 5 ms 时完成两步，剩余两步留给下一帧。</example>
public sealed class SimulationDriver
{
    /// <summary>实际速度采样满一秒后在帧结束时重置；显示平滑方式可在 #223 调整，不改变模拟。</summary>
    private const double SpeedSampleSeconds = 1;
    private readonly SimulationSession _session;
    private readonly SimulationStateCodec _states;
    private readonly SimulationScheduler _scheduler = new();
    private readonly Func<double> _getRealSeconds;
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private bool _paused;
    private SimulationSpeed _speed = SimulationSpeed.One;
    private ulong _pendingSteps;
    private double _sampleStartedAt;
    private ulong _sampleSteps;
    private double _actualSpeed;
    private bool _isExecuting;

    /// <summary>为已经创建或恢复成功的完整会话建立新的主机驱动。</summary>
    /// <param name="session">已检查起点不变量的会话，不在此重建或清空命令日志。</param>
    /// <param name="states">该会话领域的状态和检查点格式。</param>
    /// <param name="getRealSeconds">可选的有限单调真实秒来源；默认使用 Stopwatch，测试可提供可控时间。</param>
    /// <exception cref="ArgumentNullException">会话或格式为空。</exception>
    public SimulationDriver(SimulationSession session, SimulationStateCodec states, Func<double>? getRealSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(states);
        _session = session;
        _states = states;
        _getRealSeconds = getRealSeconds ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        _sampleStartedAt = _getRealSeconds();
    }

    /// <summary>读取完整核心状态与主机观察值，不推进模拟或修改采样。</summary>
    /// <returns>同一完整边界的状态快照与当前宿主设置、余量和测量值。</returns>
    public SimulationStatus GetStatus()
    {
        EnsureOwnerThread();
        var state = _session.GetState();
        return new(state, _paused, _speed, _paused || IsStopped(state) ? 0 : _actualSpeed,
            _pendingSteps, _scheduler.Remainder);
    }

    /// <summary>设置主机暂停状态，保留已有整步和小数余量；切换时重新开始速度采样。</summary>
    /// <param name="paused">true 停止自动推进，核心命令仍可以执行。</param>
    public void SetPaused(bool paused)
    {
        EnsureMutable();
        if (_paused == paused)
            return;
        _paused = paused;
        ResetSpeedSample();
    }

    /// <summary>设置目标倍速并重新采样，不改变已有权威状态或累计余量。</summary>
    /// <param name="speed">One、Two、Four 或 Eight。</param>
    /// <exception cref="ArgumentOutOfRangeException">不是约定档位。</exception>
    public void SetSpeed(SimulationSpeed speed)
    {
        EnsureMutable();
        if (!Enum.IsDefined(speed))
            throw new ArgumentOutOfRangeException(nameof(speed));
        if (_speed == speed)
            return;
        _speed = speed;
        ResetSpeedSample();
    }

    /// <summary>累计帧间隔，在工作预算内发布完整模拟步并返回全部已提交事件。</summary>
    /// <param name="realDelta">真实帧间隔，单位秒；非正或非有限值不新增时间，仍处理此前待完成的整步。</param>
    /// <param name="budgetSeconds">正且有限的工作预算，单位真实秒；只在完整步之间检查。</param>
    /// <returns>实际完成步数、最后完整状态和按步骤排列的已提交事件。</returns>
    /// <remarks>有待完成步骤时至少尝试一个完整步；不截断事务。暂停、冻结及计数耗尽时不继续累计。预算不足保留剩余步数。</remarks>
    /// <exception cref="ArgumentOutOfRangeException">预算非正或非有限。</exception>
    public AdvanceResult AdvanceFrame(double realDelta, double budgetSeconds)
    {
        EnsureMutable();
        if (!double.IsFinite(budgetSeconds) || budgetSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(budgetSeconds));
        if (_paused || IsStopped(_session.GetState()))
            return NoAdvance();
        var scaledDelta = double.IsFinite(realDelta) && realDelta > 0 ? realDelta * (int)_speed : 0;
        if (scaledDelta == 0 && _pendingSteps == 0)
            return NoAdvance();
        if (!double.IsFinite(scaledDelta) ||
            (scaledDelta + _scheduler.Remainder) * SimulationClock.StepsPerSecond >= (double)ulong.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(realDelta), "帧间隔超出可表示的模拟步数。");

        _isExecuting = true;
        try
        {
            var startedAt = _getRealSeconds();
            _pendingSteps = checked(_pendingSteps + _scheduler.CollectSteps(scaledDelta, paused: false));
            var advanced = 0UL;
            var events = ImmutableArray.CreateBuilder<DomainEvent>();
            while (_pendingSteps > 0)
            {
                // 预算只能在完整事务之间检查；一个较慢的步骤仍必须完整提交或回滚。
                var step = _session.Advance(1);
                advanced += step.StepsAdvanced;
                _pendingSteps -= step.StepsAdvanced;
                events.AddRange(step.Events);
                if (step.StepsAdvanced == 0 || IsStopped(step.StateAfter) ||
                    _getRealSeconds() - startedAt >= budgetSeconds)
                    break;
            }
            ObserveSpeed(advanced, _getRealSeconds());
            return new(advanced, _session.GetState(), events.ToImmutable());
        }
        finally
        {
            _isExecuting = false;
        }
    }

    /// <summary>在主机暂停时同步推进指定完整步数，不消费自动推进余量或计入实际速度。</summary>
    /// <param name="steps">请求完整步数；零沿用会话的无事务行为。</param>
    /// <returns>同一会话的实际完成结果，故障或计数耗尽时可以少于请求步数。</returns>
    /// <exception cref="InvalidOperationException">主机尚未暂停。</exception>
    public AdvanceResult Step(ulong steps)
    {
        EnsureMutable();
        if (!_paused)
            throw new InvalidOperationException("手动推进要求主机暂停。");
        _isExecuting = true;
        try { return _session.Advance(steps); }
        finally { _isExecuting = false; }
    }

    /// <summary>在同一会话当前边界提交一条核心命令，主机暂停不改变接受规则。</summary>
    /// <param name="command">已经解析的领域命令。</param>
    /// <param name="source">原始输入来源，保留在执行日志中。</param>
    /// <returns>同一会话的入口拒收或完整执行记录，保留命令的执行、拒绝和故障语义。</returns>
    public SubmissionResult SubmitCommand(CoreCommand command, CommandSource source)
    {
        EnsureMutable();
        _isExecuting = true;
        try { return _session.Submit(command, source); }
        finally { _isExecuting = false; }
    }

    /// <summary>从同一会话最后完整状态生成权威检查点，不包含主机设置或速度采样。</summary>
    /// <returns>由领域格式编码的规范权威文档、位置和 SHA-256 摘要。</returns>
    public AuthoritativeCheckpoint CreateCheckpoint()
    {
        EnsureOwnerThread();
        return _states.CreateCheckpoint(_session.GetState());
    }

    /// <summary>将自动完成的模拟时间除以单调真实耗时，更新当前观察段的实际速度。</summary>
    private void ObserveSpeed(ulong steps, double now)
    {
        _sampleSteps = checked(_sampleSteps + steps);
        var elapsed = now - _sampleStartedAt;
        _actualSpeed = elapsed > 0 ? _sampleSteps / SimulationClock.StepsPerSecond / elapsed : 0;
        if (elapsed >= SpeedSampleSeconds)
        {
            _sampleStartedAt = now;
            _sampleSteps = 0;
        }
    }

    /// <summary>从设置切换后的真实时刻开始观察，不混入暂停期间或旧档位的耗时。</summary>
    private void ResetSpeedSample()
    {
        _sampleStartedAt = _getRealSeconds();
        _sampleSteps = 0;
        _actualSpeed = 0;
    }

    /// <summary>返回当前完整状态，表示本次调用没有发布模拟步或事件。</summary>
    private AdvanceResult NoAdvance() => new(0, _session.GetState(), ImmutableArray<DomainEvent>.Empty);

    /// <summary>判断冻结或计数耗尽是否已使该会话无法继续自动推进。</summary>
    private static bool IsStopped(SimulationState state) =>
        state.Fault is not null || state.CompletedSteps == ulong.MaxValue || state.TransactionCount == ulong.MaxValue;

    /// <summary>禁止另一线程读取或改变该驱动的主机设置和测量值。</summary>
    private void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("模拟驱动只能由创建它的宿主线程调用。");
    }

    /// <summary>禁止计算期间重入修改主机设置或再次推进，查询仍可读取最后完整状态。</summary>
    private void EnsureMutable()
    {
        EnsureOwnerThread();
        if (_isExecuting)
            throw new InvalidOperationException("模拟计算期间不能重入主机修改入口。");
    }
}
