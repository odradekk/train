using System.Text;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Ui.EconomyPanelControls;

namespace Train.Ui;

internal sealed partial class LineInspector
{
    private static WorkRow CreateRow(Node parent, string name, Action start)
    {
        var body = new VBoxContainer { Name = name + "Row" }; parent.AddChild(body);
        var description = Text(body, "Description", "");
        var button = UiStyle.Button(name, "", start, true);
        button.AutowrapMode = TextServer.AutowrapMode.WordSmart; body.AddChild(button);
        return new(body, description, button);
    }

    private void RefreshBreaks()
    {
        var breaks = _edge.Map.Breaks;
        _noBreaks.Visible = breaks.IsDefaultOrEmpty;
        foreach (var id in _repairs.Keys.Where(id => !breaks.Any(item => item.Id == id)).ToArray())
        { RemoveControl(_repairs[id].Root); _repairs.Remove(id); }
        foreach (var broken in breaks)
        {
            var request = new RailwayWorkRequest.RepairBreak(_edge.Map.Id, broken.Id);
            if (!_repairs.TryGetValue(broken.Id, out var row))
            {
                var id = broken.Id;
                row = CreateRow(_breaks, "RepairBreak" + id.Value, () =>
                {
                    if (ReadCurrent() && _repairs.TryGetValue(id, out var current)) StartWork(request, current);
                });
                _repairs.Add(id, row);
            }
            RefreshPreview(row, request, "修复", $"{broken.Track} 号轨 · 距{Place(_edge.Map.Start)} {broken.DistanceMeters:0.##} 米");
        }
    }

    private void RefreshPreview(WorkRow row, RailwayWorkRequest request, string action, string? location = null)
    {
        var result = _view.PreviewRailwayWork(request, _source);
        row.CanStart = false; row.Button.Text = action; row.Button.Visible = true;
        if (result is not CoreResult<RailwayWorkPreview>.Success success)
        {
            row.Description.Text = "暂时无法读取施工条件。";
            return;
        }
        var preview = success.Value;
        if (request is RailwayWorkRequest.RepairBreak && preview.Blockers.Any(problem => problem is { Code: "busy", Field: "Break" }))
        {
            row.Description.Text = $"{location}\n正在修复。";
            row.Button.Visible = false;
            return;
        }
        if (request is RailwayWorkRequest.AddTrack)
        {
            row.Button.Visible = !preview.Blockers.Any(problem => problem is { Code: "invalid_state", Field: "Edge.Tracks" });
            if (!row.Button.Visible)
            {
                row.Description.Text = _edge.Map.Tracks >= _edge.Map.TrackLimit ? "铁轨数已到上限。" : "加轨施工中，已占用剩余名额。";
                return;
            }
        }
        var text = new StringBuilder();
        if (location is not null) text.AppendLine(location);
        text.AppendLine("需要：" + Materials(preview.RequiredMaterials));
        text.AppendLine(_source is { } source ? "来源：" + SettlementName(source) : "没有自有聚居地可提供材料。");
        foreach (var required in preview.RequiredMaterials)
        {
            if (_source is not { } owner) break;
            var have = Stock(owner, required.Good);
            text.AppendLine($"来源库存：{GoodName(required.Good)} × {have}");
            if (have < required.Quantity) text.AppendLine($"还差 {required.Quantity - have} 件{GoodName(required.Good)}。");
        }
        if (preview.DurationTicks > 0) text.AppendLine("工期：" + Duration(preview.DurationTicks));
        foreach (var problem in preview.Blockers)
            if (problem is not { Code: "insufficient_goods" } and not { Code: "invalid_state", Field: "MaterialSource" or "Edge.Tracks" })
                text.AppendLine(ProblemText(problem));
        row.Description.Text = text.ToString().TrimEnd().ReplaceLineEndings("\n");
        if (!preview.RequiredMaterials.IsDefaultOrEmpty) row.Button.Text += " · " + Materials(preview.RequiredMaterials);
        row.CanStart = _source is not null && preview.Blockers.IsDefaultOrEmpty;
    }

    private long Stock(SettlementId source, GoodId good) =>
        _view.FindSettlement(source) is Lookup<SettlementView>.Found settlement &&
        _view.FindInventory(settlement.Value.Inventory) is Lookup<InventoryView>.Found inventory
            ? inventory.Value.Stacks.Where(item => item.Good == good).Sum(item => item.Quantity) : 0;

    private string GoodName(GoodId good) => _content.FindGood(good) is ContentLookup<GoodPresentation>.Found { Value.Missing: false } found
        ? found.Value.Display.Text.Name : "材料";

    private string Materials(System.Collections.Immutable.ImmutableArray<GoodsQuantity> goods) => goods.IsDefaultOrEmpty
        ? "材料需求不可用" : string.Join("、", goods.Select(item => $"{GoodName(item.Good)} × {item.Quantity}"));

    private string ProblemText(CoreProblem problem) => CommandDiagnostics.Describe(problem with { Subject = null }, _content);

    private void RefreshWorks()
    {
        _works.Text = _edge.Map.Works.IsDefaultOrEmpty ? "没有施工。" : string.Join("\n\n", _edge.Map.Works.Select(work =>
        {
            var action = work.Kind == RailwayWorkKind.AddTrack ? _edge.Map.Tracks == 0 ? "建设线路" : "加轨" : "修复";
            var invested = work.InvestedGoods.IsDefaultOrEmpty ? "等待材料" : "已投入：" + Materials(work.InvestedGoods);
            var blockers = string.Join("\n", work.Blockers.Select(ProblemText));
            return $"{action} · {work.Track} 号轨 · {SettlementName(work.MaterialSource)}\n{invested} · 进度 {work.Progress:0%}" +
                (blockers.Length == 0 ? "" : "\n" + blockers);
        }));
    }

    private void RefreshTrains()
    {
        var trains = _view.World.Trains.Where(item => item.Position is TrainPosition.Edge at && at.Id == _edge.Map.Id).ToArray();
        foreach (var id in _trainRows.Keys.Where(id => !trains.Any(item => item.Id == id)).ToArray())
        { RemoveControl(_trainRows[id]); _trainRows.Remove(id); }
        foreach (var train in trains)
        {
            if (!_trainRows.TryGetValue(train.Id, out var button))
            {
                var id = train.Id;
                button = UiStyle.Button("LineTrain" + id.Value, "", () =>
                {
                    if (ReadCurrent() && _trainRows.ContainsKey(id)) _select(new WorldObjectId.Train(id));
                });
                button.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                _trains.AddChild(button); _trainRows.Add(id, button);
            }
            var at = (TrainPosition.Edge)train.Position;
            button.Text = $"列车 · {WorldPanels.Activity(train.Activity)} · {at.Track} 号轨\n距{Place(_edge.Map.Start)} {at.DistanceMeters:0.##} 米";
        }
        if (_trains.GetNodeOrNull<Label>("NoTrains") is not { } empty)
            empty = Text(_trains, "NoTrains", "没有列车。");
        empty.Visible = trains.Length == 0;
    }
}
