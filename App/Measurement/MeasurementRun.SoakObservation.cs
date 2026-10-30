using System.Text.Json;
using Godot;
using Train.App.Saves;
using Train.Core.Contracts;

namespace Train.App.Measurement;

internal sealed partial class MeasurementRun
{
    // 由场景绘制回调调用：负载计数每秒最多启动一次。标准世界一次计数约 50 ms，放在主线程会形成每秒一次的长帧；
    // 核心读视图和内容世代不可变，可在后台线程读取，帧按最近一次完成的计数分类。
    private void ObserveSoakFrame(double now, HostStatus status, double interval)
    {
        if (_soakCounting is { IsCompleted: true } counting)
        {
            var reading = counting.GetAwaiter().GetResult();
            _soakCounting = null;
            if (_soakTargets.Standard || reading.Binding == status.Binding &&
                _soakExpectations is { } expectations && _soakWindow.Observe(reading, expectations))
            {
                var (counts, _, at, seconds) = reading;
                _soakLoad = counts;
                _soakLoadTakenAt = at;
                if (_soakResetAt > 0 && at >= _soakResetAt && at - _soakLastDecayAt >= 10)
                {
                    _soakDecay.Add(new { segment = _soakSegment, seconds_after_reset = at - _soakResetAt,
                        steps_after_reset = status.View.Position.CompletedSteps - _soakResetSteps, counts });
                    _soakLastDecayAt = at;
                }
                var failed = (_soakTargets.Standard ? _soakExpectations?.Failed(counts) ?? [] : _soakWindow.Failed)
                    .Select(item => item.Counter).ToArray();
                _soakLoadClass = !_soakTargets.Standard ? _soakWindow.LoadClass : failed.Length == 0 ? SoakFullLoad :
                    _soakTargets.Standard && failed.Contains("armed_line_combats") && failed.All(key => SoakLineBattleKeys.Contains(key))
                        ? SoakLoadWithoutLineBattle : SoakLoadBelow;
                _sceneOperationTrace!.WriteLine(JsonSerializer.Serialize(new { kind = "soak-load", at, applied = now, segment = _soakSegment,
                    phase = _scenePhase, load_class = _soakLoadClass, failed, counts, trims = _soakTrims,
                    recording = _app.Recording?.Status }, SceneJson));
                _sceneCostTrace!.WriteLine(JsonSerializer.Serialize(new { kind = "load-count", at, applied = now, draw = _sceneDrawCount,
                    seconds, thread = "background" }, SceneJson));
            }
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
            StartSoakCount(status);
            _soakLoadAt = now;
        }
        if (_soakSample is { } sample && interval > 0)
        {
            sample.Frames.AddFrame(now, status.View.Position.CompletedSteps, _simulationWork, status.Speed, status.IsPaused);
            sample.Classes[_soakLoadClass] = sample.Classes.GetValueOrDefault(_soakLoadClass) + 1;
        }
        _soakPeak?.Observe(_scenePrivate, _sceneWorking, RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed));
    }

    private void StartSoakCount(HostStatus status)
    {
        var view = status.View;
        var binding = status.Binding;
        var content = _app.Host!.Content!;
        var expectations = _soakExpectations;
        var standard = _soakTargets.Standard;
        var taken = Seconds();
        if (!_soakTargets.Standard) _soakLoadAt = taken;
        _soakCounting = Task.Run(() =>
        {
            var started = Seconds();
            var counts = expectations?.Count(view, content) ?? WorldCounts.Read(view, content);
            return new SoakCount(counts, binding, standard ? started : taken, Seconds() - started);
        });
    }

    private void ReadSoakExpectations()
    {
        var recipe = Path.Combine(Path.GetDirectoryName(_input)!, "recipe.json");
        using var document = JsonDocument.Parse(File.ReadAllText(recipe));
        using var input = File.OpenRead(_input);
        _soakExpectations = SoakExpectations.Read(document.RootElement, SaveContainer.ReadHeader(input).Position.CompletedSteps);
        _sceneEvidence["load_expectations"] = new { recipe = Path.GetFileName(recipe), at = _soakExpectations.ExpectedAt,
            saved_at = _soakExpectations.SavedAt,
            keys = _soakExpectations.Items.Select(item => new { key = item.Key, node = item.Node?.Value, minimum = item.Minimum, maximum = item.Maximum }) };
        _sceneEvidence["unavailable_views"] = _soakTargets.Standard ? Array.Empty<string>() :
            _soakTargets.Profile == "busy-combat" ? ["line-battle: 保存点没有线路战斗；节点视图与 node-battle 相同，不重复采样"] :
            ["line-battle: 保存点没有线路战斗", "node-battle: 保存点没有节点战斗"];
    }

    private Guid SaveHeaderId()
    {
        using var input = File.OpenRead(_input);
        return SaveContainer.ReadHeader(input).Id;
    }

    private (double Private, double Working, double Video) SoakSteady()
    {
        var samples = _sceneRecent.Where(sample => sample.At >= _sceneLastDraw - 1).ToArray();
        if (samples.Length == 0) return (_scenePrivate, _sceneWorking, RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed));
        return (FrameMeasurements.Distribution(samples.Select(item => (double)item.PrivateBytes)).P50,
            FrameMeasurements.Distribution(samples.Select(item => (double)item.WorkingSetBytes)).P50,
            FrameMeasurements.Distribution(samples.Select(item => (double)item.VideoBytes)).P50);
    }

}
