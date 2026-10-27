using System.Collections.Immutable;
using System.Text.Json;
using Godot;
using Train.App.Recording;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.App.Measurement;

// 窗口长测按段循环。每段先把标准世界重置到配方保存点；主体内不读档，让测量设定的 5 分钟自动存档和
// 每 18,000 步的录制起点都能在同一会话里累积到轮换；段末反复保存并读取玩家自己的存档。
internal sealed partial class MeasurementRun
{
    private const int SoakBodySeconds = 720;
    private const int SoakRoundTrips = 8;
    private const double SoakSampleSeconds = 10;
    private const string SoakFullLoad = "full";
    private const string SoakLoadWithoutLineBattle = "all-but-line-battle";
    private const string SoakLoadBelow = "below-recipe";
    private static readonly WorldObjectId.Node SoakSettlement = new(new(2));
    private static readonly WorldObjectId.Node SoakNodeBattle = new(new(3));
    private static readonly WorldObjectId.Edge SoakLineBattle = new(new(15));
    private static readonly TrainId SoakTrain = new(1);
    // 线路战斗在保存点后约 1,500 步（约 25 游戏秒）结束，所以另设一档不含线路战斗的负载。
    private static readonly string[] SoakLineBattleKeys = ["armed_combats", "armed_line_combats"];

    private readonly List<object> _soakSamples = [];
    private readonly Dictionary<string, SoakTotals> _soakTotals = new(StringComparer.Ordinal);
    private readonly List<SoakOperation> _soakOperations = [];
    private readonly List<(double At, double Private, double Working, double Video)> _soakMap = [];
    private readonly List<(int Segment, string Phase, double At, long LiveBytes)> _soakHeaps = [];
    private readonly Dictionary<int, DateTimeOffset> _soakSlots = [];
    private readonly List<object> _soakRejectedLoads = [];
    private readonly List<object> _soakDecay = [];
    private HostOperationResult? _soakLastRestore;
    private double? _soakBody;
    private double _soakResetAt, _soakLastDecayAt;
    private ulong _soakResetSteps;
    private int _soakRoundTripsAttempted;
    private (string Key, double? Minimum, double? Maximum)[] _soakExpectations = [];
    private ImmutableSortedDictionary<string, double>? _soakLoad;
    private Task<(ImmutableSortedDictionary<string, double> Counts, double At, double Seconds)>? _soakCounting;
    private string _soakLoadClass = SoakLoadBelow;
    private double _soakLoadAt, _soakLoadTakenAt;
    private SoakSample? _soakSample;
    private SoakPeak? _soakPeak;
    private (double Private, double Working, double Video) _soakMapSteady;
    private (HostBinding Binding, ulong From)? _soakRecording;
    private Guid _soakSaveId;
    private int _soakSegment, _soakAutosaves, _soakAutosaveOverwrites, _soakTrims, _soakEncounters, _soakLoads, _soakLoadAttempts;
    private double _soakIncompleteSeconds;

    private sealed record SoakOperation(string Type, string Name, int Segment, double At, double? Seconds,
        double? ReadySeconds, double PrivatePeakOverMap, double WorkingPeakOverMap, double VideoPeakOverMap,
        double PrivatePeakOverBefore, double WorkingPeakOverBefore, double VideoPeakOverBefore);

    private sealed class SoakSample(string view, SimulationSpeed speed, double started, ulong steps)
    {
        internal string View { get; } = view;
        internal SimulationSpeed Speed { get; } = speed;
        internal FrameMeasurements Frames { get; } = new(started, steps);
        internal Dictionary<string, int> Classes { get; } = new(StringComparer.Ordinal);
    }

    private sealed class SoakTotals
    {
        internal List<double> Intervals { get; } = [];
        internal Dictionary<string, int> Classes { get; } = new(StringComparer.Ordinal);
        internal double Seconds { get; set; }
        internal ulong Steps { get; set; }
    }

    private sealed class SoakPeak(double privateBytes, double working, double video)
    {
        internal double Private { get; private set; } = privateBytes;
        internal double Working { get; private set; } = working;
        internal double Video { get; private set; } = video;
        internal void Observe(double privateBytes, double working, double video)
        { Private = Math.Max(Private, privateBytes); Working = Math.Max(Working, working); Video = Math.Max(Video, video); }
    }

    private async Task RunWindowSoak()
    {
        var host = _app.Host!;
        var duration = _sceneDuration > 0 ? _sceneDuration : 7200;
        ReadSoakExpectations();
        _soakSaveId = SaveHeaderId();
        // 产品默认 1 小时；每段主体约 12 分钟，改用 5 分钟才能在同一会话里观察到 3 槽轮换。
        _app.Saves!.SetAutoSaveInterval(TimeSpan.FromMinutes(5));
        host.BoundaryCompleted += CountSoakEncounters;
        host.OperationEnded += CaptureSoakRestore;
        try
        {
            await HoldSceneMap("initial-map");
            _soakMapSteady = SoakSteady();
            var started = Seconds();
            do
            {
                _soakSegment++;
                await ResetSoakSegment();
                var bodyStarted = Seconds();
                var round = 0;
                do
                {
                    var roundStarted = Seconds();
                    var roundSteps = host.GetStatus()!.View.Position.CompletedSteps;
                    await RunSoakRound(round++);
                    // 录制要在同一会话里攒满保留数加一个起点才会裁剪。按首轮实测的每真实秒步数（含编辑器暂停）
                    // 把每段主体定为 1.3 倍所需时长，所有段等长；短预演最多用总时长的 60%。
                    _soakBody ??= Math.Min(duration * 0.6, Math.Max(SoakBodySeconds, 1.3 * RecordingService.StartIntervalSteps *
                        host.SessionOptions!.RetainedRecordingStarts / Math.Max(1e-9,
                        (host.GetStatus()!.View.Position.CompletedSteps - roundSteps) / (Seconds() - roundStarted))));
                } while (Seconds() - bodyStarted < _soakBody);
                for (var trip = 0; trip < SoakRoundTrips; trip++) await RunSoakRoundTrip(trip % 2 == 0 ? SoakSettlement : SoakLineBattle);
            } while (Seconds() - started < duration);
            _soakHeaps.Add((_soakSegment, "final", Seconds(), CollectSceneHeap(_soakSegment, "final-map")));
            _sceneEvidence["soak"] = SummarizeSoak(Seconds() - started, _soakBody ?? 0);
        }
        finally
        {
            host.BoundaryCompleted -= CountSoakEncounters;
            host.OperationEnded -= CaptureSoakRestore;
            _soakSample = null;
            _soakPeak = null;
        }
    }

    private void ReadSoakExpectations()
    {
        // 阈值取配方在保存点的期望，与制作和检查标准世界时使用同一组计数。
        var recipe = Path.Combine(Path.GetDirectoryName(_input)!, "recipe.json");
        using var document = JsonDocument.Parse(File.ReadAllText(recipe));
        var target = document.RootElement.GetProperty("target").GetProperty("completed_steps").GetUInt64();
        _soakExpectations = document.RootElement.GetProperty("expectations").EnumerateArray()
            .Where(item => item.GetProperty("at").GetUInt64() == target)
            .Select(item => (item.GetProperty("key").GetString()!,
                item.TryGetProperty("minimum", out var minimum) ? (double?)minimum.GetDouble() : null,
                item.TryGetProperty("maximum", out var maximum) ? (double?)maximum.GetDouble() : null)).ToArray();
        if (_soakExpectations.Length == 0) throw new InvalidDataException("配方没有保存点的负载期望。");
        _sceneEvidence["load_expectations"] = new { recipe = Path.GetFileName(recipe), at = target,
            keys = _soakExpectations.Select(item => new { key = item.Key, minimum = item.Minimum, maximum = item.Maximum }) };
    }

    private Guid SaveHeaderId()
    {
        using var input = File.OpenRead(_input);
        return SaveContainer.ReadHeader(input).Id;
    }

    private async Task ResetSoakSegment()
    {
        await SoakOperationTimed("reset", "reset", async () =>
        {
            await LoadSceneInput(restoreInput: true);
            return (_sceneLoadCompleted - _sceneLoadButton, null);
        });
        _soakLoadAttempts++;
        _soakLoads++;
        // 读档换了会话，场景会话随之重建；之后的首次打开是冷打开。
        _sceneOpenedTargets.Clear();
        var host = _app.Host!;
        host.SetPaused(true);
        AssertSoakTargets();
        _soakResetAt = Seconds();
        _soakLastDecayAt = 0;
        _soakResetSteps = host.GetStatus()!.View.Position.CompletedSteps;
        // 每段都在刚读入的同一保存点、暂停的地图上强制回收，作为相同状态下的存活堆读数。
        _soakHeaps.Add((_soakSegment, "reset", Seconds(), CollectSceneHeap(_soakSegment, "reset-map")));
        await HoldSoakMap(2);
        await SoakOpen("reset-line", SoakLineBattle, _sceneLoadButton);
        // 只认采样开始后取得的计数；上一段末尾的计数里线路战斗早已结束。
        var sampling = Seconds();
        await SampleSoakView("line-battle", SimulationSpeed.Eight,
            () => _soakLoadTakenAt > sampling && _soakLoad?.GetValueOrDefault("armed_line_combats") == 0);
        await ReturnSceneMap();
    }

    private void AssertSoakTargets()
    {
        var view = _app.Host!.GetStatus()!.View;
        var content = _app.Host.Content!;
        var node = view.World.Nodes.Single(item => item.Id == SoakSettlement.Id);
        if (node.Occupant is not NodeOccupant.Settlement settlement ||
            ((Lookup<SettlementView>.Found)view.FindSettlement(settlement.Id)).Value.Relation != SettlementRelation.PlayerOwned)
            throw new InvalidDataException("长测目标不成立：节点 2 不是玩家聚居地。");
        if (WorldCounts.Read(view, content, SoakNodeBattle.Id)["armed_combats"] < 1)
            throw new InvalidDataException("长测目标不成立：节点 3 没有玩家武装参战的战斗。");
        if (WorldCounts.Read(view, content)["armed_line_combats"] < 1 ||
            !view.World.Combats.Any(combat => combat.Location is WorldLocation.Edge { Id: var edge } && edge == SoakLineBattle.Id))
            throw new InvalidDataException("长测目标不成立：边 15 没有玩家武装参战的线路战斗。");
        var train = ((Lookup<TrainView>.Found)view.FindTrain(SoakTrain)).Value;
        if (train.Activity != TrainActivity.Running)
            throw new InvalidDataException($"长测目标不成立：列车 1 不在运行，当前为 {train.Activity}。");
    }

    private async Task RunSoakRound(int round)
    {
        // 每段首轮另取各视图的 1× 样本。
        var speeds = round == 0 ? new[] { SimulationSpeed.Eight, SimulationSpeed.One } : [SimulationSpeed.Eight];
        foreach (var speed in speeds) await SampleSoakView("map", speed);
        await HoldSoakMap(1);
        await SoakOpen(_sceneOpenedTargets.Contains(SoakSettlement.ToString()) ? "node-reopen" : "node-first", SoakSettlement);
        foreach (var speed in speeds) await SampleSoakView("settlement", speed);
        await SoakOpen("node-to-line", SoakLineBattle);
        foreach (var speed in speeds) await SampleSoakView("line", speed);
        await SoakOpen("line-to-node", SoakNodeBattle);
        foreach (var speed in speeds) await SampleSoakView("node-battle", speed);
        await SoakReturnMap();
        await SoakOpen("line-reopen", SoakLineBattle);
        await SoakReturnMap();
        await SoakOpen("follow", new WorldObjectId.Train(SoakTrain));
        foreach (var speed in speeds) await SampleSoakView("follow", speed);
        await SoakReturnMap();
        await RunSoakEditor();
        await HoldSoakMap(1);
        _soakMap.Add((Seconds(), _soakMapSteady.Private, _soakMapSteady.Working, _soakMapSteady.Video));
        _sceneOperationTrace!.WriteLine(JsonSerializer.Serialize(new { kind = "natural-map", segment = _soakSegment, round,
            at = Seconds(), memory = SceneSteady(), load = _soakLoad, load_class = _soakLoadClass }, SceneJson));
    }

    private async Task SoakReturnMap()
    {
        await SoakOperationTimed("return-map", "return-map", async () =>
        {
            var start = Seconds();
            await ReturnSceneMap();
            return (Seconds() - start, null);
        });
        await HoldSoakMap(1);
    }

    private async Task RunSoakEditor()
    {
        var editor = _app.Editor!;
        var view = _app.Host!.GetStatus()!.View;
        var vehicle = ((Lookup<TrainView>.Found)view.FindTrain(SoakTrain)).Value.Vehicles[0];
        await SoakOperationTimed("editor-open", "editor-open", async () =>
        {
            var start = Seconds();
            editor.OpenInstance(new InstanceId.Vehicle(vehicle));
            await Wait(() => editor.Session is not null && !editor.IsBusy && editor.State is not null);
            var draw = _sceneDrawCount;
            await Wait(() => _sceneDrawCount > draw);
            return (Seconds() - start, null);
        });
        // 编辑器打开时宿主暂停模拟（HostCoordinator.BeginEditing）且不接受倍速设置，样本记录的是暂停下的帧。
        await SampleSoakView("editor", _app.Host!.GetStatus()!.Speed, setSpeed: false);
        var cell = editor.State!.Elements.First(element => element.Kind == EditElementKind.Cell && !element.Locked);
        editor.Select(new EditSelection.Elements([cell.Id]));
        if (editor.Preview(new EditOperation.PaintSelection(new PaintColor("#c03030"))) is not SceneResult<IEditPreview>.Success preview)
            throw new InvalidOperationException("长测编辑预览失败：" + editor.Message);
        editor.Apply(preview.Value, false);
        if (editor.State?.CanUndo != true) throw new InvalidOperationException("长测编辑没有生效：" + editor.Message);
        editor.Undo();
        editor.Redo();
        await SoakOperationTimed("editor-close", "editor-close", async () =>
        {
            var start = Seconds();
            var closed = false;
            if (editor.RequestClose(() => closed = true) is { } failure) throw new InvalidOperationException(failure.Message);
            if (!closed) editor.ResolveClose(LeaveChoice.Discard);
            await Wait(() => closed && !editor.IsOpen);
            var draw = _sceneDrawCount;
            await Wait(() => _sceneDrawCount > draw);
            return (Seconds() - start, null);
        });
    }

    private async Task RunSoakRoundTrip(WorldObjectId firstDisplay)
    {
        var saves = _app.Saves!;
        await SoakOperationTimed("save", "save", async () =>
        {
            // 自动存档可能正在写入；玩家会等它写完再保存。
            await Wait(() => !saves.IsBusy);
            var start = Seconds();
            if (saves.Save() is { } failure) throw new IOException(failure.Code + ": " + failure.Message);
            await Wait(() => !saves.IsBusy);
            return (Seconds() - start, null);
        });
        var saved = _app.Host!.GetStatus()!.View.Position;
        var loaded = false;
        _soakRoundTripsAttempted++;
        _soakLastRestore = null;
        await SoakOperationTimed("load", "load", async () =>
        {
            loaded = await LoadSceneInput(allowRejected: true);
            return (loaded ? _sceneLoadCompleted - _sceneLoadButton : null, null);
        });
        _soakLoadAttempts++;
        if (loaded) { _soakLoads++; _sceneOpenedTargets.Clear(); }
        if (!loaded)
        {
            // 被拒绝的读档保留原对局继续；不取读档后首显样本。
            var problem = _soakLastRestore?.Problems.FirstOrDefault();
            _soakRejectedLoads.Add(new { segment = _soakSegment, at = Seconds(), saved_at = saved,
                operation = _soakLastRestore?.Operation, status = _soakLastRestore?.Status.ToString(),
                code = problem?.Code ?? _soakLastRestore?.FaultCode, field = problem?.Field, message = _app.Saves!.LastMessage });
            _sceneOperationTrace!.WriteLine(JsonSerializer.Serialize(new { kind = "rejected-load", at = Seconds(), segment = _soakSegment,
                saved_at = saved, status = _soakLastRestore?.Status.ToString(), problems = _soakLastRestore?.Problems }, SceneJson));
            await HoldSoakMap(1);
            return;
        }
        await HoldSoakMap(1);
        await SoakOpen(firstDisplay is WorldObjectId.Node ? "reload-node" : "reload-line", firstDisplay, _sceneLoadButton);
        await ReturnSceneMap();
    }

    private async Task SoakOpen(string type, WorldObjectId target, double? loadButton = null) =>
        await SoakOperationTimed(type, type, async () =>
        {
            await OpenSceneMeasured(type, target, loadButton);
            return (_sceneOperable > 0 ? _sceneOperable - _sceneClick : null, _sceneReady - _sceneClick);
        });

    private async Task SoakOperationTimed(string type, string name, Func<Task<(double? Seconds, double? Ready)>> run)
    {
        var before = SoakSteady();
        var map = _soakMapSteady;
        _soakPeak = new(before.Private, before.Working, before.Video);
        var at = Seconds();
        var (seconds, ready) = await run();
        var peak = _soakPeak;
        _soakPeak = null;
        _soakOperations.Add(new(type, name, _soakSegment, at, seconds, ready,
            peak.Private - map.Private, peak.Working - map.Working, peak.Video - map.Video,
            peak.Private - before.Private, peak.Working - before.Working, peak.Video - before.Video));
    }

    private async Task HoldSoakMap(double seconds)
    {
        _scenePhase = "soak-map";
        var started = _sceneLastDraw;
        await Wait(() => _sceneLastDraw >= started + seconds);
        _soakMapSteady = SoakSteady();
    }

    private async Task SampleSoakView(string view, SimulationSpeed speed, Func<bool>? ended = null, bool setSpeed = true)
    {
        var host = _app.Host!;
        if (setSpeed && (host.SetSpeed(speed) is not null || host.SetPaused(false) is not null))
            throw new InvalidOperationException("不能设置长测倍速。");
        _scenePhase = $"{view}-{(int)speed}x";
        var started = _sceneLastDraw;
        var sample = new SoakSample(view, speed, started, host.GetStatus()!.View.Position.CompletedSteps);
        _soakSample = sample;
        await Wait(() => _sceneLastDraw >= started + SoakSampleSeconds || ended?.Invoke() == true && sample.Classes.Count > 0);
        _soakSample = null;
        var timing = sample.Frames.Summarize();
        var key = $"{view}/{(int)speed}x";
        if (!_soakTotals.TryGetValue(key, out var totals)) _soakTotals[key] = totals = new();
        totals.Intervals.AddRange(timing.Frames.Select(frame => frame.IntervalSeconds));
        foreach (var (name, count) in sample.Classes) totals.Classes[name] = totals.Classes.GetValueOrDefault(name) + count;
        totals.Seconds += timing.ElapsedSeconds;
        totals.Steps += timing.CompletedSteps;
        _soakSamples.Add(new
        {
            segment = _soakSegment, view, speed = (int)speed, started, seconds = timing.ElapsedSeconds,
            frames = timing.Frames.Length, frame_seconds = timing.FrameSeconds,
            over_16_7_ms_share = timing.Frames.Count(frame => frame.IntervalSeconds > 1d / 60) / (double)timing.Frames.Length,
            steps_per_second = timing.CompletedSteps / timing.ElapsedSeconds, target_steps_per_second = (int)speed * SimulationTime.StepsPerSecond,
            paused_frames = timing.Frames.Count(frame => frame.Paused),
            simulation_budget_frames = timing.SimulationBudgetReachedFrames, load_classes = sample.Classes
        });
    }

    // 由场景绘制回调调用：负载计数每秒最多启动一次。标准世界一次计数约 50 ms，放在主线程会形成每秒一次的长帧；
    // 核心读视图和内容世代不可变，可在后台线程读取，帧按最近一次完成的计数分类。
    private void ObserveSoakFrame(double now, HostStatus status, double interval)
    {
        if (_soakCounting is { IsCompleted: true } counting)
        {
            var (counts, at, seconds) = counting.GetAwaiter().GetResult();
            _soakCounting = null;
            _soakLoad = counts;
            _soakLoadTakenAt = at;
            if (_soakResetAt > 0 && at >= _soakResetAt && at - _soakLastDecayAt >= 10)
            {
                _soakDecay.Add(new { segment = _soakSegment, seconds_after_reset = at - _soakResetAt,
                    steps_after_reset = status.View.Position.CompletedSteps - _soakResetSteps, counts });
                _soakLastDecayAt = at;
            }
            var failed = _soakExpectations.Where(item => !counts.TryGetValue(item.Key, out var value) ||
                item.Minimum is { } minimum && value < minimum || item.Maximum is { } maximum && value > maximum)
                .Select(item => item.Key).ToArray();
            _soakLoadClass = failed.Length == 0 ? SoakFullLoad :
                failed.Contains("armed_line_combats") && failed.All(key => SoakLineBattleKeys.Contains(key))
                    ? SoakLoadWithoutLineBattle : SoakLoadBelow;
            _sceneOperationTrace!.WriteLine(JsonSerializer.Serialize(new { kind = "soak-load", at, applied = now, segment = _soakSegment,
                phase = _scenePhase, load_class = _soakLoadClass, failed, counts, trims = _soakTrims,
                recording = _app.Recording?.Status }, SceneJson));
            _sceneCostTrace!.WriteLine(JsonSerializer.Serialize(new { kind = "load-count", at, applied = now, draw = _sceneDrawCount,
                seconds, thread = "background" }, SceneJson));
        }
        if (_soakCounting is null && now - _soakLoadAt >= 1)
        {
            if (_app.Recording?.Status is { } recording)
            {
                // 读档后到首个起点写成前，From 退回当前位置；只比较前后两次都有可用起点的读数。
                if (recording.HasUsableStart && _soakRecording is { } last && last.Binding == status.Binding &&
                    recording.From.CompletedSteps > last.From) _soakTrims++;
                _soakRecording = recording.HasUsableStart ? (status.Binding, recording.From.CompletedSteps) : null;
                if (recording.Incomplete) _soakIncompleteSeconds += _soakLoadAt > 0 ? now - _soakLoadAt : 0;
            }
            var view = status.View;
            var content = _app.Host!.Content!;
            _soakCounting = Task.Run(() =>
            {
                var started = Seconds();
                return (WorldCounts.Read(view, content), started, Seconds() - started);
            });
            _soakLoadAt = now;
        }
        if (_soakSample is { } sample && interval > 0)
        {
            sample.Frames.AddFrame(now, status.View.Position.CompletedSteps, _simulationWork, status.Speed, status.IsPaused);
            sample.Classes[_soakLoadClass] = sample.Classes.GetValueOrDefault(_soakLoadClass) + 1;
        }
        _soakPeak?.Observe(_scenePrivate, _sceneWorking, RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed));
    }

    private void CaptureSoakRestore(HostOperationResult result)
    {
        if (result.Kind == OperationKind.Restore) _soakLastRestore = result;
    }

    private void CountSoakEncounters(HostBinding binding, ICoreReadView view, IContentView content, ImmutableArray<EventEnvelope> events) =>
        _soakEncounters += events.Count(envelope => envelope.Event is CoreEvent.CombatStarted or CoreEvent.CombatJoined);

    // 由存档状态变化调用：按自动存档槽文件头的保存时间计数，覆盖已有槽才算轮换。
    private void ObserveSoakAutosaves()
    {
        if (_soakSaveId == Guid.Empty || _app.Saves!.IsBusy) return;
        var store = new FileSaveStore(OS.GetUserDataDir());
        for (var slot = 0; slot < 3; slot++)
        {
            var path = store.AutoPathFor(_soakSaveId, slot);
            if (!File.Exists(path)) continue;
            DateTimeOffset saved;
            try { saved = store.ReadHeader(path).SavedAt; }
            catch (Exception error) when (error is SaveFormatException or IOException) { continue; }
            if (_soakSlots.TryGetValue(slot, out var previous) && previous == saved) continue;
            _soakAutosaves++;
            if (_soakSlots.ContainsKey(slot)) _soakAutosaveOverwrites++;
            _soakSlots[slot] = saved;
        }
    }

    private (double Private, double Working, double Video) SoakSteady()
    {
        var samples = _sceneRecent.Where(sample => sample.At >= _sceneLastDraw - 1).ToArray();
        if (samples.Length == 0) return (_scenePrivate, _sceneWorking, RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed));
        return (FrameMeasurements.Distribution(samples.Select(item => (double)item.PrivateBytes)).P50,
            FrameMeasurements.Distribution(samples.Select(item => (double)item.WorkingSetBytes)).P50,
            FrameMeasurements.Distribution(samples.Select(item => (double)item.VideoBytes)).P50);
    }

    private object SummarizeSoak(double seconds, double body)
    {
        const double Hour = 3600;
        static double? Slope(IReadOnlyList<(double X, double Y)> points)
        {
            if (points.Count < 2) return null;
            var meanX = points.Average(point => point.X);
            var meanY = points.Average(point => point.Y);
            var denominator = points.Sum(point => (point.X - meanX) * (point.X - meanX));
            return denominator == 0 ? null : points.Sum(point => (point.X - meanX) * (point.Y - meanY)) / denominator;
        }
        static double? Median(IEnumerable<double> values)
        {
            var array = values.ToArray();
            return array.Length == 0 ? null : FrameMeasurements.Distribution(array).P50;
        }
        var start = _soakMap.Count > 0 ? _soakMap[0].At : 0;
        object MemorySlope(Func<(double At, double Private, double Working, double Video), double> value) =>
            Slope(_soakMap.Select(item => ((item.At - start) / Hour, value(item))).ToArray()) is { } slope ? slope : "unavailable";
        return new
        {
            seconds, segments = _soakSegment, body_seconds = body, round_trips_per_segment = SoakRoundTrips,
            sample_seconds = SoakSampleSeconds,
            load_classes = new { full = "配方保存点的全部期望成立", all_but_line_battle = "armed_line_combats 不成立，其余不成立的只有 armed_combats",
                below_recipe = "其他期望不成立；这些帧不是标准负载证据", frame_rule = "每帧按最近一次（每秒最多一次）计数分类" },
            views = _soakTotals.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new
            {
                view = pair.Key, frames = pair.Value.Intervals.Count, frame_seconds = FrameMeasurements.Distribution(pair.Value.Intervals),
                over_16_7_ms_share = pair.Value.Intervals.Count(interval => interval > 1d / 60) / (double)pair.Value.Intervals.Count,
                steps_per_second = pair.Value.Steps / pair.Value.Seconds,
                load_class_shares = pair.Value.Classes.ToDictionary(item => item.Key, item => item.Value / (double)pair.Value.Intervals.Count)
            }),
            samples = _soakSamples,
            operations = _soakOperations.GroupBy(operation => operation.Type).OrderBy(group => group.Key, StringComparer.Ordinal).Select(group =>
            {
                var items = group.OrderBy(item => item.At).ToArray();
                var timed = items.Where(item => item.Seconds is not null).ToArray();
                var third = Math.Max(1, timed.Length / 3);
                return new
                {
                    type = group.Key, count = items.Length, measured = timed.Length,
                    latency_seconds = timed.Length == 0 ? null : FrameMeasurements.Distribution(timed.Select(item => item.Seconds!.Value)),
                    ready_seconds = items.Any(item => item.ReadySeconds is not null)
                        ? FrameMeasurements.Distribution(items.Where(item => item.ReadySeconds is not null).Select(item => item.ReadySeconds!.Value)) : null,
                    first_third_median = Median(timed.Take(third).Select(item => item.Seconds!.Value)),
                    last_third_median = Median(timed.TakeLast(third).Select(item => item.Seconds!.Value)),
                    slope_seconds_per_hour = Slope(timed.Select(item => ((item.At - items[0].At) / Hour, item.Seconds!.Value)).ToArray()),
                    peak_over_map = new { private_bytes = items.Max(item => item.PrivatePeakOverMap),
                        working_set_bytes = items.Max(item => item.WorkingPeakOverMap), godot_video_bytes = items.Max(item => item.VideoPeakOverMap) },
                    peak_over_before = new { private_bytes = items.Max(item => item.PrivatePeakOverBefore),
                        working_set_bytes = items.Max(item => item.WorkingPeakOverBefore), godot_video_bytes = items.Max(item => item.VideoPeakOverBefore) },
                    items
                };
            }),
            memory = new
            {
                map_records = _soakMap.Count,
                private_bytes_per_hour = MemorySlope(item => item.Private),
                working_set_bytes_per_hour = MemorySlope(item => item.Working),
                godot_video_bytes_per_hour = MemorySlope(item => item.Video),
                godot_video_label = "Godot 渲染缓冲区、纹理与总分配；集成显卡上是共享的系统内存，不是独立显存",
                private_label = OperatingSystem.IsWindows() ? "Windows 私有字节" : "Linux RssAnon + VmSwap；峰值为 0.2 秒采样最大值",
                reset_heaps = _soakHeaps.Select(item => new { segment = item.Segment, phase = item.Phase, at = item.At,
                    live_bytes = item.LiveBytes, over_first_bytes = item.LiveBytes - _soakHeaps[0].LiveBytes })
            },
            autosave = new { interval_seconds = _app.Saves!.AutoSaveInterval?.TotalSeconds, writes = _soakAutosaves,
                slot_overwrites = _soakAutosaveOverwrites, source = "自动存档槽文件头的保存时间" },
            recording = new { start_interval_steps = RecordingService.StartIntervalSteps,
                retained_starts = _app.Host!.SessionOptions!.RetainedRecordingStarts, trims = _soakTrims,
                incomplete_seconds = _soakIncompleteSeconds, source = "同一会话内前后两次读数都有可用起点时，最早起点位置前移" },
            round_trips = new
            {
                evidence = _soakRejectedLoads.Count > 0 ? "failed" : "passed",
                attempted = _soakRoundTripsAttempted, loaded = _soakRoundTripsAttempted - _soakRejectedLoads.Count,
                rejected = _soakRejectedLoads.Count, rejections = _soakRejectedLoads,
                rule = "手动保存后读同一存档；被拒绝时记录并在原对局继续，不取读档后首显样本"
            },
            load_decay = new { interval_seconds = 10, rule = "每次重置后每 10 秒取一次负载计数", series = _soakDecay },
            body_sizing = new { rule = "首轮实测每真实秒步数（含编辑器暂停）下，1.3 × 起点间隔 × 保留起点数所需时长；不少于 720 秒，不超过总时长的 60%",
                body_seconds = body },
            loads = new { attempted = _soakLoadAttempts, succeeded = _soakLoads }, encounters = _soakEncounters,
            uncovered = new[] { "process_first_open_node", "process_first_open_line", "process_first_follow" }
        };
    }
}
