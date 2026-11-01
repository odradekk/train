using System.Text;
using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal sealed class EdgeDetailPanel
{
    private readonly PanelTarget _target;
    private readonly Func<bool> _available;
    private readonly Action<PanelTarget, EdgeId> _work;
    internal DetailPanelControls Controls { get; }

    internal EdgeDetailPanel(PanelContainer panel, VBoxContainer body, PanelTarget target,
        Func<bool> available,
        Action<PanelTarget, EdgeId> work)
    {
        _target = target;
        _available = available;
        _work = work;
        Controls = new(panel, body, target, Describe, Actions);
    }

    private List<(string Name, string Text, bool Primary, Action Press)> Actions(ICoreReadView view)
    {
        var bound = _target;
        var target = bound.Object;
        var actions = new List<(string Name, string Text, bool Primary, Action Press)>();
        if (target is WorldObjectId.Edge edge)
            actions.Add(("RailwayWork", "铁路施工…", false, () =>
            {
                if (_available()) _work(bound, edge.Id);
            }));
        return actions;
    }

    private string Describe(ICoreReadView view, IContentView content)
    {
        var target = _target.Object;
        var text = new StringBuilder();
        if (target is WorldObjectId.Edge edge && view.FindEdge(edge.Id) is Lookup<EdgeView>.Found foundEdge)
        {
            var map = foundEdge.Value.Map;
            text.AppendLine($"起点 / 终点：{WorldPanels.NodeName(map.Start, view, content)} / {WorldPanels.NodeName(map.End, view, content)}\n线路长度：{map.LengthMeters:0.##} m\n铁轨：{map.Tracks} / 上限 {map.TrackLimit}\n地貌：{map.Terrain.TypeId.Value}\n");
            foreach (var track in foundEdge.Value.Tracks)
                text.AppendLine($"{track.Number} 号轨：{(track.Trains.IsEmpty ? "空闲" : string.Join("、", track.Trains.Select(id => $"列车 {id.Value}")))}");
        }
        else return "已不存在";
        return text.ToString().ReplaceLineEndings("\n");
    }
}
