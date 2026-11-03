using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Ui.EconomyPanelControls;
using static Train.Ui.EconomyPanels;

namespace Train.Ui;

internal partial class ConstructionWorkspace
{
    private readonly Dictionary<InventoryId, SourceRow> _sourceRows = [];
    private sealed record SourceRow(HBoxContainer Root, CheckBox Enabled, Button Up, Button Down);

    private void BuildChoices()
    {
        Clear(_choices);
        _edit.Visible = _instance is not null;
        var group = new ButtonGroup();
        if (_instance is { } instance)
        {
            var drafts = _content.LocalDrafts.Where(item => item.Target == new DraftTarget.Instance(instance)
                && item.Payload is DraftPayload.Design).ToArray();
            if (_draft is { } selected && !drafts.Any(item => item.Id == selected)) _draft = null;
            if (_draft is null && !_repairSelected) _draft = drafts.FirstOrDefault()?.Id;
            _repairSelected = _draft is null;
            AddChoice("RepairTarget", "修理当前" + (instance is InstanceId.Vehicle ? "车辆" : "建筑"),
                _repairSelected, () => { _draft = null; _repairSelected = true; Changed(); });
            for (var index = 0; index < drafts.Length; index++)
            {
                var saved = drafts[index];
                var design = ((DraftPayload.Design)saved.Payload).Value;
                AddChoice($"Draft_{index}", design.Display.Name, _draft == saved.Id,
                    () => { _draft = saved.Id; _repairSelected = false; Changed(); });
            }
            _empty.Text = drafts.Length == 0 ? "这个实例没有已保存的改装草稿，可继续编辑并保存。" : "";
        }
        else
        {
            var blueprints = new List<(BlueprintSummary Summary, BlueprintRecord Record)>();
            foreach (var summary in _content.Blueprints)
                if (_content.FindBlueprint(summary.Address) is ContentLookup<BlueprintRecord>.Found found &&
                    (_plot is null ? found.Value.Definition.Specification is DesignSpecification.Vehicle
                        : found.Value.Definition.Specification is DesignSpecification.Building))
                    blueprints.Add((summary, found.Value));
            if (_blueprint is not null && !blueprints.Any(item => item.Record.Address == _blueprint))
            { _blueprint = null; ClearDesign(); }
            for (var index = 0; index < blueprints.Count; index++)
            {
                var (summary, blueprint) = blueprints[index];
                var problem = BlueprintProblem(summary, blueprint) ?? PlotProblem(blueprint);
                var button = AddChoice($"Blueprint_{index}", $"{blueprint.Definition.Display.Name} · {ServiceLibrary(blueprint.Address.Library)}" +
                    (problem is null ? "" : "\n" + problem), _blueprint == blueprint.Address,
                    () => { _blueprint = blueprint.Address; Changed(); });
                button.Disabled = problem is not null;
            }
            _empty.Text = blueprints.Count == 0 ? "没有可用蓝图，请先在蓝图库中保存。" : "";
        }
        _empty.Visible = _empty.Text.Length > 0;

        Button AddChoice(string name, string title, bool selected, Action choose)
        {
            var button = UiStyle.Button(name, title, choose);
            button.ToggleMode = true; button.ButtonGroup = group; button.SetPressedNoSignal(selected);
            button.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            button.Alignment = HorizontalAlignment.Left; _choices.AddChild(button); return button;
        }
    }

    private static string? BlueprintProblem(BlueprintSummary summary, BlueprintRecord blueprint)
    {
        if (summary.Usable && summary.Design is not null &&
            blueprint.Evaluation is DesignEvaluation.Complete { IsValid: true, Core: not null }) return null;
        var diagnostics = blueprint.Evaluation switch
        {
            DesignEvaluation.Complete complete => complete.Diagnostics,
            DesignEvaluation.Partial partial => partial.Diagnostics,
            DesignEvaluation.Failed failed => failed.Diagnostics,
            _ => []
        };
        var messages = summary.Diagnostics.Concat(diagnostics).Select(item => item.Message).Distinct().ToArray();
        return messages.Length > 0 ? string.Join("\n", messages) : "设计内容不完整，暂不能施工。";
    }

    private string? PlotProblem(BlueprintRecord blueprint)
    {
        if (_plot is not { } plot || blueprint.Evaluation is not DesignEvaluation.Complete { Core: { } design }) return null;
        var quote = FindQuote(facility => new JobRequest.BuildBuilding(facility, plot, design, [_settlement.Inventory], 0), out _);
        return quote?.Blockers.Any(problem => problem.Field == "Plot" && problem.Code == "conditions_not_met") == true &&
            blueprint.Definition.Specification is DesignSpecification.Building { PlotSize: { } size }
            ? $"需要{PlotSizeName(size)}地块" : null;
    }

    private void RefreshSources()
    {
        _sources.Visible = _settlement.Relation == SettlementRelation.NonHostile;
        if (!_sources.Visible) return;
        var trains = _view.World.Trains.Select(item => _view.FindTrain(item.Id))
            .OfType<Lookup<TrainView>.Found>().Select(item => item.Value)
            .Where(train => train.Position is TrainPosition.Node node && node.Id == _settlement.Node)
            .Where(train => train.Vehicles.All(id => _view.FindInstance(new InstanceId.Vehicle(id))
                is Lookup<InstanceView>.Found { Value.Ownership: SettlementRelation.PlayerOwned })).ToArray();
        var unavailable = trains.Where(train => train.Activity != TrainActivity.Servicing ||
            train.Position is not TrainPosition.Node { Phase: NodeTravelPhase.Docked } || train.SpeedMetersPerSecond != 0)
            .Select(train => train.Inventory).ToHashSet();
        var available = trains.Select(train => train.Inventory)
            .Concat(_settlement.Leases.Select(_view.FindLease).OfType<Lookup<LeaseView>.Found>().Select(item => item.Value.Inventory))
            .Distinct().ToArray();
        foreach (var id in _sourceOrder.Except(available).ToArray())
        {
            _sourceOrder.Remove(id); _enabledSources.Remove(id);
            RemoveControl(_sourceRows[id].Root); _sourceRows.Remove(id);
        }
        foreach (var id in available)
        {
            if (_sourceRows.ContainsKey(id)) continue;
            _sourceOrder.Add(id);
            if (!unavailable.Contains(id)) _enabledSources.Add(id);
            var row = new HBoxContainer(); _sources.AddChild(row);
            var check = new CheckBox { Name = $"Source_{id.Value}", SizeFlagsHorizontal = SizeFlags.ExpandFill,
                AutowrapMode = TextServer.AutowrapMode.WordSmart };
            row.AddChild(check);
            check.Toggled += enabled =>
            {
                if (enabled) _enabledSources.Add(id); else _enabledSources.Remove(id);
                SourcesChanged();
            };
            var up = UiStyle.Button($"MoveSourceUp_{id.Value}", "提前", () => MoveSource(id, -1)); row.AddChild(up);
            var down = UiStyle.Button($"MoveSourceDown_{id.Value}", "推后", () => MoveSource(id, 1)); row.AddChild(down);
            _sourceRows.Add(id, new(row, check, up, down));
        }
        for (var index = 0; index < _sourceOrder.Count; index++)
        {
            var id = _sourceOrder[index]; var row = _sourceRows[id];
            if (unavailable.Contains(id)) _enabledSources.Remove(id);
            row.Enabled.Text = SourceName(id) + (unavailable.Contains(id) ? " · 未停靠站台" : "");
            row.Enabled.Disabled = unavailable.Contains(id);
            row.Enabled.SetPressedNoSignal(_enabledSources.Contains(id));
            row.Up.Disabled = index == 0; row.Down.Disabled = index == _sourceOrder.Count - 1;
            _sources.MoveChild(row.Root, index);
        }
    }

    private void MoveSource(InventoryId id, int offset)
    {
        var index = _sourceOrder.IndexOf(id); var next = index + offset;
        if (index < 0 || next < 0 || next >= _sourceOrder.Count) return;
        (_sourceOrder[index], _sourceOrder[next]) = (_sourceOrder[next], _sourceOrder[index]);
        RefreshSources(); SourcesChanged();
    }

    private void SourcesChanged()
    { _submitted = false; _result.Text = ""; _quote = null; Evaluate(); }

    private string SourceName(InventoryId inventory) => _view.FindInventory(inventory) is Lookup<InventoryView>.Found found
        ? found.Value.Owner switch
        {
            InventoryOwner.Train train => $"列车 {train.Id.Value}",
            InventoryOwner.Lease lease => $"{PlaceName()}的租用仓库 {_settlement.Leases.IndexOf(lease.Id) + 1}",
            InventoryOwner.Settlement => $"{PlaceName()}库存",
            _ => "货物来源不可用"
        } : "货物来源已不存在";
}
