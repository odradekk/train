using System.Collections.Immutable;
using System.Globalization;
using Godot;
using Train.App;
using Train.App.Editing;
using Train.App.Authoring;
using Train.App.Logging;
using Train.App.Notifications;
using Train.App.Recording;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Ui;

public partial class GameUi
{
    private WorldSceneController? _scenes;
    private Button? _returnToMap;

    private CommandDecision.Accepted? Submit(PanelTarget? target, CoreCommand command)
        => Submit(target, command, null);

    private CommandDecision.Accepted? Submit(PanelTarget? target, CoreCommand command, IContentCandidate? candidate)
    {
        var status = _host?.GetStatus();
        if (status is null || _host!.Content is not { } content || target is null ||
            !target.IsAvailable(status.Binding, status.View, _host.PanelGenerationCurrent(target.Binding, target.Generation)) || _host.IsBusy)
        {
            ReportUiError("当前操作不可提交：对象已失效或宿主正在操作。");
            return null;
        }
        SubmissionResult result;
        try { result = _host.Submit(target.Binding, command, CommandSource.UserInterface, candidate); }
        catch (NotImplementedException)
        {
            _logger.Warning("ui", "command_not_implemented", command.GetType().Name);
            ShowError("该操作尚未实现。");
            return null;
        }
        if (CommandDiagnostics.Report(_logger, result, content) is { } problem) ShowError(problem);
        return (result as SubmissionResult.Completed)?.Receipt.Decision as CommandDecision.Accepted;
    }

    private void OpenScene(WorldObjectId id)
    {
        if (_scenes is null || _content is null || _host?.GetStatus() is not { } status || _host.Content is null ||
            !new PanelTarget(_binding, _content.Generation, id).IsAvailable(status.Binding, status.View,
                _host.PanelGenerationCurrent(_binding, _content.Generation)))
            return;
        WorldSceneTarget? target = id switch
        {
            WorldObjectId.Node node => new WorldSceneTarget.Location(new WorldLocation.Node(node.Id)),
            WorldObjectId.Edge edge when status.View.FindEdge(edge.Id) is Lookup<EdgeView>.Found found =>
                new WorldSceneTarget.Location(new WorldLocation.Edge(edge.Id, found.Value.Map.LengthMeters / 2)),
            WorldObjectId.Train train when status.View.FindTrain(train.Id) is Lookup<TrainView>.Found found &&
                !found.Value.Vehicles.IsEmpty => new WorldSceneTarget.Follow(train.Id, FollowedVehicle.Of(found.Value, status.View, _host.Content)),
            _ => null
        };
        if (target is null) { ReportUiError("对象没有可观察的场景目标。"); return; }
        _scenes.Open(target);
        var openDetail = id is WorldObjectId.Edge || id is WorldObjectId.Node clicked &&
            status.View.World.Nodes.FirstOrDefault(item => item.Id == clicked.Id)?.Occupant is NodeOccupant.Empty;
        if (openDetail)
        {
            _panels?.Refresh(_binding, status.View, _content!);
            _panels?.OpenObject(id);
        }
    }

    private void OpenCombat(CombatId id)
    {
        if (_host?.GetStatus() is not { } status) return;
        var item = status.View.World.Combats.FirstOrDefault(combat => combat.Id == id);
        if (item is null) { ReportUiError("已不存在"); return; }
        OpenLocation(item.Location);
    }

    private void OpenLocation(WorldLocation location)
    {
        if (_scenes is null || _host?.GetStatus() is not { } status || !LocationExists(status.View, location))
        { ReportUiError("已不存在"); return; }
        _scenes.Open(new WorldSceneTarget.Location(location));
    }

    private static bool LocationExists(ICoreReadView view, WorldLocation location) => location switch
    {
        WorldLocation.Node node => view.World.Nodes.Any(item => item.Id == node.Id),
        WorldLocation.Edge edge => view.World.Edges.Any(item => item.Id == edge.Id),
        _ => false
    };

    private void OnReturnedToMap(SceneProblem? problem)
    {
        if (problem is not null) ShowError($"{problem.Code}：{problem.Detail}");
    }

    private void OnSelected(PickSubject subject)
    {
        if (_scenes?.View.Visible != true || _host?.GetStatus() is not { } status || _host.Content is not { } content)
            return;
        WorldObjectId? id = subject switch
        {
            PickSubject.WorldObject item => item.Object,
            PickSubject.Terrain { Location: WorldLocation.Node node } => new WorldObjectId.Node(node.Id),
            PickSubject.Terrain { Location: WorldLocation.Edge edge } => new WorldObjectId.Edge(edge.Id),
            _ => null
        };
        if (id is null || _content is null ||
            !new PanelTarget(_binding, _content.Generation, id).IsAvailable(status.Binding, status.View,
                _host.PanelGenerationCurrent(_binding, _content.Generation)))
            return;
        _panels!.Refresh(status.Binding, status.View, content);
        _panels.OpenObject(id);
    }
}
