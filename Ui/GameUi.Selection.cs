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

    private void RefreshSelection()
    {
        if (_shell.SelectedObject is WorldObjectId.Node node &&
            _view!.World.Nodes.FirstOrDefault(item => item.Id == node.Id)?.Occupant is NodeOccupant.Settlement settlement)
            SelectObject(new WorldObjectId.Settlement(settlement.Id));
    }

    private void SelectObject(WorldObjectId id, Control? content = null)
    {
        if (_host?.GetStatus() is not { } status || _content is null ||
            !new PanelTarget(_binding, _content.Generation, id).IsAvailable(status.Binding, status.View,
                _host.PanelGenerationCurrent(_binding, _content.Generation)))
        { ReportUiError("对象已不存在或所属对局已更换。"); return; }
        if (content is not null) { _shell.Inspect(id, content); RefreshObjectNavigation(); return; }
        if (id is WorldObjectId.Node node && status.View.World.Nodes.First(item => item.Id == node.Id).Occupant
            is NodeOccupant.Settlement occupied)
        { SelectObject(new WorldObjectId.Settlement(occupied.Id)); return; }
        switch (id)
        {
            case WorldObjectId.Settlement settlement:
                _economy!.OpenSettlement(new(_binding, _content.Generation, id), settlement.Id);
                break;
            case WorldObjectId.Combat combat:
                _combat!.Close(); _combat.OpenCombat(combat.Id);
                _shell.Inspect(id, _combat);
                break;
            default:
                _panels!.Refresh(status.Binding, status.View, _content);
                _panels.OpenObject(id);
                break;
        }
    }

    private void OpenScene(WorldObjectId id)
    {
        if (_scenes is not null && _content is not null && _host?.GetStatus() is { } status && _host.Content is not null &&
            new PanelTarget(_binding, _content.Generation, id).IsAvailable(status.Binding, status.View,
                _host.PanelGenerationCurrent(_binding, _content.Generation)))
        {
            WorldSceneTarget? target = ObjectLocation(id, status.View) is { } location
                ? new WorldSceneTarget.Location(location) : null;
            if (id is WorldObjectId.Train train)
                target = status.View.FindTrain(train.Id) is Lookup<TrainView>.Found found && !found.Value.Vehicles.IsEmpty
                    ? new WorldSceneTarget.Follow(train.Id, FollowedVehicle.Of(found.Value, status.View, _host.Content)) : null;
            if (target is null) ReportUiError("对象没有可观察的场景目标。");
            else _scenes.Open(target);
        }
        else ReportUiError("对象已不存在或所属对局已更换。");
    }

    private void LocateObject(WorldObjectId id)
    {
        if (_map.ObjectPoint(id) is null) return;
        if (_scenes?.View.Visible == true) _scenes.ReturnToMap();
        _map.CenterOn(id);
    }

    private static WorldLocation? ObjectLocation(WorldObjectId id, ICoreReadView view) => id switch
    {
        WorldObjectId.Node node when view.World.Nodes.Any(item => item.Id == node.Id) => new WorldLocation.Node(node.Id),
        WorldObjectId.Settlement settlement when view.World.Nodes.FirstOrDefault(item =>
            item.Occupant is NodeOccupant.Settlement occupied && occupied.Id == settlement.Id) is { } node =>
            new WorldLocation.Node(node.Id),
        WorldObjectId.Edge edge when view.World.Edges.FirstOrDefault(item => item.Id == edge.Id) is { } map =>
            new WorldLocation.Edge(edge.Id, map.LengthMeters / 2),
        WorldObjectId.Train train when view.World.Trains.FirstOrDefault(item => item.Id == train.Id) is { } map =>
            map.Position switch { TrainPosition.Node node => new WorldLocation.Node(node.Id),
                TrainPosition.Edge edge => new WorldLocation.Edge(edge.Id, edge.DistanceMeters), _ => null },
        WorldObjectId.Combat combat => view.World.Combats.FirstOrDefault(item => item.Id == combat.Id)?.Location,
        _ => null
    };

    private void RefreshObjectNavigation()
    {
        var id = _shell.SelectedObject;
        var supported = id is WorldObjectId.Node or WorldObjectId.Settlement or WorldObjectId.Edge or WorldObjectId.Train or WorldObjectId.Combat;
        if (!supported) { _shell.SetObjectNavigation(false, null, null); return; }
        var locateReason = _map.ObjectPoint(id!) is null ? "对象没有地图位置。" : null;
        var sceneReason = _host?.GetStatus() is not { } status || ObjectLocation(id!, status.View) is null
            ? "对象没有可观察的场景目标。"
            : id is WorldObjectId.Train train && status.View.FindTrain(train.Id) is Lookup<TrainView>.Found { Value.Vehicles.IsEmpty: true }
                ? "列车没有车辆。" : null;
        _shell.SetObjectNavigation(true, locateReason, sceneReason);
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
        SelectObject(id);
    }
}
