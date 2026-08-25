namespace TrainMvp.Domain;

public sealed class LoopPlayback
{
    public const int Speed1 = 1;
    public const int Speed4 = 4;
    public const int Speed16 = 16;

    private readonly LoopRunResult _run;
    private readonly SimulationState _display;
    private readonly List<SimEvent> _applied = [];
    private readonly List<IReadOnlyList<string>> _presentedHops = [];
    private double _clock;
    private bool _paused;
    private int _speed = Speed1;
    private bool _finished;
    private SimEvent? _activeLeg;
    private IReadOnlyList<string> _routePath = [];
    private int _hopIndex;
    private bool _transitDwell;

    public LoopPlayback(LoopRunResult run, SimulationState stateBefore)
    {
        _run = run;
        _display = stateBefore.Clone();
    }

    public LoopRunResult Result => _run;

    public SimulationState DisplayState => _display;

    public IReadOnlyList<SimEvent> AppliedEvents => _applied;

    public bool IsPaused => _paused;

    public int Speed => _speed;

    public bool IsFinished => _finished;

    public bool CanControl => !_finished;

    public double Clock => _clock;

    public int AppliedEventCount => _applied.Count;

    public SimEvent? CurrentEvent => _applied.Count == 0 ? null : _applied[^1];

    public SimEvent? PresentationEvent => IsMovingHop ? _activeLeg : CurrentEvent;

    public IReadOnlyList<string> RoutePath => _routePath;

    public IReadOnlyList<IReadOnlyList<string>> PresentedMotionHops => _presentedHops;

    public IReadOnlyList<string> MotionPath
    {
        get
        {
            if (!IsMovingHop || _hopIndex + 1 >= _routePath.Count)
            {
                return [];
            }

            return [_routePath[_hopIndex], _routePath[_hopIndex + 1]];
        }
    }

    public string? NextTradeStopId
    {
        get
        {
            for (var i = _applied.Count; i < _run.Events.Count; i++)
            {
                if (_run.Events[i] is TradeEvent trade)
                {
                    return trade.StationId;
                }
            }

            return null;
        }
    }

    public double LoopProgress
    {
        get
        {
            if (_run.Events.Count == 0)
            {
                return 1;
            }

            return Math.Clamp((AppliedEventCount + EventProgress) / _run.Events.Count, 0, 1);
        }
    }

    public double EventProgress
    {
        get
        {
            var duration = CurrentWindowDuration();
            return duration <= 0 ? 1 : Math.Clamp(_clock / duration, 0, 1);
        }
    }

    public void Start()
    {
        if (_finished || _applied.Count > 0)
        {
            return;
        }

        ApplyNext();
        _clock = 0;
        if (_applied.Count >= _run.Events.Count)
        {
            _finished = true;
        }
    }

    public void Pause()
    {
        if (CanControl)
        {
            _paused = true;
        }
    }

    public void Resume()
    {
        if (CanControl)
        {
            _paused = false;
        }
    }

    public void SetSpeed(int speed)
    {
        if (!CanControl)
        {
            return;
        }

        if (speed is not (Speed1 or Speed4 or Speed16))
        {
            throw new ArgumentOutOfRangeException(nameof(speed), "Speed must be 1, 4, or 16.");
        }

        _speed = speed;
    }

    public void Tick(double delta)
    {
        if (_paused || _finished || _applied.Count == 0)
        {
            return;
        }

        _clock += delta * _speed;
        while (!_finished)
        {
            var duration = CurrentWindowDuration();
            if (duration > 0 && _clock < duration)
            {
                return;
            }

            if (duration > 0)
            {
                _clock -= duration;
            }

            if (!AdvanceWindow())
            {
                _finished = true;
                _clock = 0;
                return;
            }
        }
    }

    public void SkipToNextTradeStop()
    {
        if (!CanControl)
        {
            return;
        }

        var target = -1;
        for (var i = _applied.Count; i < _run.Events.Count; i++)
        {
            if (_run.Events[i] is TradeEvent)
            {
                target = i;
                break;
            }
        }

        if (target < 0)
        {
            FastComplete();
            return;
        }

        while (_applied.Count <= target)
        {
            ApplyNext();
        }

        FinishDiscreteJump();
    }

    public void FastComplete()
    {
        if (_finished)
        {
            return;
        }

        if (_applied.Count == 0)
        {
            Start();
        }

        while (ApplyNext())
        {
        }

        _finished = true;
        FinishDiscreteJump();
        _paused = false;
    }

    public void AdvanceEvent()
    {
        if (_finished)
        {
            return;
        }

        if (_applied.Count == 0)
        {
            Start();
            return;
        }

        _clock = 0;
        if (!AdvanceWindow())
        {
            _finished = true;
        }
    }

    public static double DurationOf(SimEvent item)
    {
        return item switch
        {
            TradeEvent => 0.6,
            TransitEvent => 0.25,
            ReturnArrivalEvent => 0.4,
            TravelEvent travel => Math.Max(0.35, 0.2 * travel.Ticks),
            RepositionEvent reposition => Math.Max(0.35, 0.2 * reposition.Ticks),
            LoopFinishedEvent => 0,
            _ => 0.3
        };
    }

    public static IReadOnlyList<string> ConcatMotionHops(IEnumerable<IReadOnlyList<string>> hops)
    {
        var stations = new List<string>();
        foreach (var hop in hops)
        {
            if (hop.Count < 2)
            {
                continue;
            }

            if (stations.Count == 0)
            {
                stations.Add(hop[0]);
            }

            stations.Add(hop[1]);
        }

        return stations;
    }

    private bool IsMovingHop => !_transitDwell && _activeLeg is not null && _hopIndex + 1 < _routePath.Count;

    private double CurrentWindowDuration()
    {
        if (IsMovingHop && _activeLeg is not null)
        {
            var hops = Math.Max(1, _routePath.Count - 1);
            return DurationOf(_activeLeg) / hops;
        }

        if (_transitDwell && CurrentEvent is TransitEvent transit)
        {
            return DurationOf(transit);
        }

        return CurrentEvent is null ? 0 : DurationOf(CurrentEvent);
    }

    private bool AdvanceWindow()
    {
        if (IsMovingHop)
        {
            return CompleteHop();
        }

        if (_transitDwell)
        {
            return ContinueLegAfterTransit();
        }

        return ApplyNext();
    }

    private bool CompleteHop()
    {
        var arrived = _routePath[_hopIndex + 1];
        var lastHop = _hopIndex + 1 >= _routePath.Count - 1;
        if (!lastHop && PeekNext() is TransitEvent transit && transit.StationId == arrived)
        {
            ApplyNext();
            _transitDwell = true;
            return true;
        }

        if (lastHop)
        {
            ClearLeg();
            return ApplyNext();
        }

        _display.LocationId = arrived;
        BeginHop(_hopIndex + 1);
        return true;
    }

    private bool ContinueLegAfterTransit()
    {
        _transitDwell = false;
        if (_hopIndex + 2 < _routePath.Count)
        {
            BeginHop(_hopIndex + 1);
            return true;
        }

        ClearLeg();
        return ApplyNext();
    }

    private bool ApplyNext()
    {
        if (_applied.Count >= _run.Events.Count)
        {
            return false;
        }

        var item = _run.Events[_applied.Count];
        ApplyToDisplay(item);
        _applied.Add(item);
        if (item is TravelEvent travel)
        {
            BeginLeg(travel, travel.Path);
        }
        else if (item is RepositionEvent reposition)
        {
            BeginLeg(reposition, reposition.Path);
        }
        else if (item is not TransitEvent)
        {
            ClearLeg();
        }

        return _applied.Count < _run.Events.Count;
    }

    private SimEvent? PeekNext()
    {
        return _applied.Count >= _run.Events.Count ? null : _run.Events[_applied.Count];
    }

    private void BeginLeg(SimEvent leg, IReadOnlyList<string> path)
    {
        _activeLeg = leg;
        _routePath = path;
        _transitDwell = false;
        if (path.Count >= 2)
        {
            BeginHop(0);
        }
        else
        {
            _hopIndex = 0;
        }
    }

    private void BeginHop(int hopIndex)
    {
        _hopIndex = hopIndex;
        _transitDwell = false;
        if (hopIndex + 1 < _routePath.Count)
        {
            _presentedHops.Add([_routePath[hopIndex], _routePath[hopIndex + 1]]);
        }
    }

    private void ClearLeg()
    {
        _activeLeg = null;
        _routePath = [];
        _hopIndex = 0;
        _transitDwell = false;
    }

    private void FinishDiscreteJump()
    {
        ClearLeg();
        _clock = 0;
        if (_applied.Count >= _run.Events.Count)
        {
            _finished = true;
        }
    }

    private void ApplyToDisplay(SimEvent item)
    {
        switch (item)
        {
            case TradeEvent trade:
                foreach (var rule in trade.Rules)
                {
                    _display.Cash += rule.CashDelta;
                    var holding = _display.Holding(rule.GoodId);
                    _display.Cargo[rule.GoodId] = rule.Action == TradeAction.Buy
                        ? holding + rule.ActualQuantity
                        : holding - rule.ActualQuantity;
                }

                _display.LocationId = trade.StationId;
                break;
            case TravelEvent:
            case RepositionEvent:
                break;
            case TransitEvent transit:
                _display.LocationId = transit.StationId;
                break;
            case ReturnArrivalEvent arrival:
                _display.LocationId = arrival.StationId;
                break;
        }
    }
}
