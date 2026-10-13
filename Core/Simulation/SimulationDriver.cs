
using System.Collections.Immutable;
using System.Diagnostics;

namespace Train.Core.Simulation;

public enum SimulationSpeed
{
    One = 1,
    Two = 2,
    Four = 4,
    Eight = 8
}

public sealed record SimulationStatus(
    SimulationState State, bool IsPaused, SimulationSpeed Speed,
    double ActualSpeed, double Remainder)
{
    public double SimulationSeconds => State.CompletedSteps / SimulationClock.StepsPerSecond;
}

public sealed class SimulationDriver
{
    private const double SpeedSampleSeconds = 1;
    private readonly SimulationSession _session;
    private readonly SimulationStateCodec _states;
    private readonly SimulationScheduler _scheduler = new();
    private readonly Func<double> _getRealSeconds;
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private bool _paused;
    private SimulationSpeed _speed = SimulationSpeed.One;
    private double _sampleStartedAt;
    private ulong _sampleSteps;
    private double _actualSpeed;
    private bool _isExecuting;

    public SimulationDriver(SimulationSession session, SimulationStateCodec states, Func<double>? getRealSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(states);
        _session = session;
        _states = states;
        _getRealSeconds = getRealSeconds ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        _sampleStartedAt = _getRealSeconds();
    }

    public SimulationStatus GetStatus()
    {
        EnsureOwnerThread();
        var state = _session.GetState();
        return new(state, _paused, _speed, _paused || IsStopped(state) ? 0 : _actualSpeed,
            _scheduler.Remainder);
    }

    public void SetPaused(bool paused)
    {
        EnsureMutable();
        if (_paused == paused)
            return;
        _paused = paused;
        ResetSpeedSample();
    }

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

    public AdvanceResult AdvanceFrame(double realDelta, double budgetSeconds)
    {
        EnsureMutable();
        if (!double.IsFinite(budgetSeconds) || budgetSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(budgetSeconds));
        if (_paused || IsStopped(_session.GetState()))
            return NoAdvance();
        var scaledDelta = double.IsFinite(realDelta) && realDelta > 0 ? realDelta * (int)_speed : 0;
        if (scaledDelta == 0)
            return NoAdvance();
        if (!double.IsFinite(scaledDelta) ||
            (scaledDelta + _scheduler.Remainder) * SimulationClock.StepsPerSecond >= (double)ulong.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(realDelta), "帧间隔超出可表示的模拟步数。");

        _isExecuting = true;
        try
        {
            var startedAt = _getRealSeconds();
            // 过载只使游戏时间变慢，未执行的整步额度不能令后续帧加速追赶。
            var remainingSteps = _scheduler.CollectSteps(scaledDelta, paused: false);
            var advanced = 0UL;
            var events = ImmutableArray.CreateBuilder<DomainEvent>();
            SimulationFaultDiagnostic? diagnostic = null;
            while (remainingSteps > 0)
            {
                // 预算只能在完整事务之间检查；一个较慢的步骤仍必须完整提交或回滚。
                var step = _session.Advance(1);
                advanced += step.StepsAdvanced;
                remainingSteps -= step.StepsAdvanced;
                events.AddRange(step.Events);
                diagnostic = step.Diagnostic;
                if (step.StepsAdvanced == 0 || IsStopped(step.StateAfter) ||
                    _getRealSeconds() - startedAt >= budgetSeconds)
                    break;
            }
            ObserveSpeed(advanced, _getRealSeconds());
            return new(advanced, _session.GetState(), events.ToImmutable(), diagnostic);
        }
        finally
        {
            _isExecuting = false;
        }
    }

    public AdvanceResult Step(ulong steps)
    {
        EnsureMutable();
        if (!_paused)
            throw new InvalidOperationException("手动推进要求主机暂停。");
        _isExecuting = true;
        try { return _session.Advance(steps); }
        finally { _isExecuting = false; }
    }

    public SubmissionResult SubmitCommand(CoreCommand command, CommandSource source)
    {
        EnsureMutable();
        _isExecuting = true;
        try { return _session.Submit(command, source); }
        finally { _isExecuting = false; }
    }

    public AuthoritativeCheckpoint CreateCheckpoint()
    {
        EnsureOwnerThread();
        return _states.CreateCheckpoint(_session.GetState());
    }

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

    private void ResetSpeedSample()
    {
        _sampleStartedAt = _getRealSeconds();
        _sampleSteps = 0;
        _actualSpeed = 0;
    }

    private AdvanceResult NoAdvance() => new(0, _session.GetState(), ImmutableArray<DomainEvent>.Empty);

    private static bool IsStopped(SimulationState state) =>
        state.Fault is not null || state.CompletedSteps == ulong.MaxValue || state.TransactionCount == ulong.MaxValue;

    private void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("模拟驱动只能由创建它的宿主线程调用。");
    }

    private void EnsureMutable()
    {
        EnsureOwnerThread();
        if (_isExecuting)
            throw new InvalidOperationException("模拟计算期间不能重入主机修改入口。");
    }
}
