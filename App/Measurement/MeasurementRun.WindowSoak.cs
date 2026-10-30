using System.Collections.Immutable;
using System.Text.Json;
using Godot;
using Train.App.Recording;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.App.Measurement;

// 场景计划：发布的 ExportRelease、真实 1920×1080 窗口、原样示例存档；每份重负载正式跑 3×9000 秒。
// 每段恢复保存点，按最近一组配方期望（node 指定计数节点）分类实际帧。standard-world 保留原顺序与结束条件；
// 重负载暂停读档后先开主视图，1×／8×各 10 秒，再循环地图、节点、线路、跟随及编辑器。
// Stopwatch 记录墙钟与逐帧实际步数；内存读进程私有字节、工作集、Godot 缓冲区和纹理，地图主动 GC 单列。
// 主体内不读档，让 5 分钟自动存档、每 18,000 步录制起点累积至轮换；段末八次存读。中位数及范围按轮报告，
// 负载下降或没有完整小时的数据保持未知。功能回归用宿主、引擎测试，真实窗口证据在 runner；不改产品接口。
// 反向检查：SoakExpectationsTests 拒绝旧会话／采样前计数；SoakTargetsTests 固定默认目标和速度顺序。
internal sealed partial class MeasurementRun
{
    private const int SoakBodySeconds = 720;
    private const int SoakRoundTrips = 8;
    private const double SoakSampleSeconds = 10;
    private const string SoakFullLoad = "full";
    private const string SoakLoadWithoutLineBattle = "all-but-line-battle";
    private const string SoakLoadBelow = "below-recipe";
    private readonly WorldObjectId.Node SoakSettlement;
    private static readonly WorldObjectId.Node SoakNodeBattle = new(new(3));
    private readonly WorldObjectId.Edge SoakLineBattle;
    private TrainId SoakTrain => _soakTargets.Train;
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
    private SoakExpectations? _soakExpectations;
    private ImmutableSortedDictionary<string, double>? _soakLoad;
    private Task<SoakCount>? _soakCounting;
    private readonly SoakLoadWindow _soakWindow = new();
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

    private async Task ResetSoakSegment()
    {
        await SoakOperationTimed("reset", "reset", async () =>
        {
            await LoadSceneInput(restoreInput: true, keepPaused: !_soakTargets.Standard);
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
        if (!_soakTargets.Standard)
        {
            await SoakOpen("reset-" + _soakTargets.PrimaryView, _soakTargets.PrimaryTarget, _sceneLoadButton);
            foreach (var speed in _soakTargets.Speeds(0)) await SampleSoakView(_soakTargets.PrimaryView, speed);
            await ReturnSceneMap();
            return;
        }
        await SoakOpen("reset-line", _soakTargets.PrimaryTarget, _sceneLoadButton);
        // 只认采样开始后取得的计数；上一段末尾的计数里线路战斗早已结束。
        var sampling = Seconds();
        await SampleSoakView(_soakTargets.PrimaryView, SimulationSpeed.Eight,
            () => _soakLoadTakenAt > sampling && _soakLoad?.GetValueOrDefault("armed_line_combats") == 0);
        await ReturnSceneMap();
    }

    private void AssertSoakTargets()
    {
        var view = _app.Host!.GetStatus()!.View;
        var content = _app.Host.Content!;
        if (!_soakTargets.Standard)
        {
            _soakTargets.ValidateHeavy(view);
            var counts = _soakExpectations!.Count(view, content);
            var failed = _soakExpectations.Failed(counts);
            if (failed.Length > 0) throw new InvalidDataException($"长测负载不成立：{_soakTargets.Profile}，" + string.Join("；", failed.Select(item =>
                $"{item.Counter} 期望 {item.Minimum}–{item.Maximum}，实际 {counts.GetValueOrDefault(item.Counter)}")));
            return;
        }
        var node = view.World.Nodes.Single(item => item.Id == SoakSettlement.Id);
        if (node.Occupant is not NodeOccupant.Settlement settlement ||
            ((Lookup<SettlementView>.Found)view.FindSettlement(settlement.Id)).Value.Relation != SettlementRelation.PlayerOwned)
            throw new InvalidDataException($"长测目标不成立：节点 {SoakSettlement.Id.Value} 不是玩家聚居地。");
        if (WorldCounts.Read(view, content, SoakNodeBattle.Id)["armed_combats"] < 1)
            throw new InvalidDataException("长测目标不成立：节点 3 没有玩家武装参战的战斗。");
        if (WorldCounts.Read(view, content)["armed_line_combats"] < 1 ||
            !view.World.Combats.Any(combat => combat.Location is WorldLocation.Edge { Id: var edge } && edge == SoakLineBattle.Id))
            throw new InvalidDataException($"长测目标不成立：线路 {SoakLineBattle.Id.Value} 没有玩家武装参战的线路战斗。");
        var train = ((Lookup<TrainView>.Found)view.FindTrain(SoakTrain)).Value;
        if (train.Activity != TrainActivity.Running)
            throw new InvalidDataException($"长测目标不成立：列车 {SoakTrain.Value} 不在运行，当前为 {train.Activity}。");
    }

    private async Task RunSoakRound(int round)
    {
        // 每段首轮另取各视图的 1× 样本。
        var speeds = _soakTargets.Speeds(round);
        foreach (var speed in speeds) await SampleSoakView("map", speed);
        await HoldSoakMap(1);
        await SoakOpen(_sceneOpenedTargets.Contains(SoakSettlement.ToString()) ? "node-reopen" : "node-first", SoakSettlement);
        foreach (var speed in speeds) await SampleSoakView(_soakTargets.NodeView, speed);
        await SoakOpen("node-to-line", SoakLineBattle);
        foreach (var speed in speeds) await SampleSoakView("line", speed);
        if (_soakTargets.Standard)
        {
            await SoakOpen("line-to-node", SoakNodeBattle);
            foreach (var speed in speeds) await SampleSoakView("node-battle", speed);
            await SoakReturnMap();
            await SoakOpen("line-reopen", SoakLineBattle);
        }
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

}
