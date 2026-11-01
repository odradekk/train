using System.Globalization;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class TrainPlanWorkspace
{
    private sealed record RuleControls(int Index, OptionButton Action, List<TradeAction> Actions,
        OptionButton Good, List<GoodId?> Goods, OptionButton Inventory, List<InventoryId?> Inventories,
        VBoxContainer PriceColumn, Label PriceLabel, Label Summary, Label Problems);
    private readonly List<RuleControls> _ruleControls = [];

    private static GoodsRuleInput RuleInput(AutomaticGoodsRule rule) => new(rule.Action switch
    {
        AutomaticGoodsAction.Buy => TradeAction.Buy, AutomaticGoodsAction.Sell => TradeAction.Sell,
        AutomaticGoodsAction.Load => TradeAction.Load, _ => TradeAction.Unload
    }, rule.Good, rule.LocalInventory, rule.TargetQuantity.ToString(CultureInfo.InvariantCulture),
        (rule.Action switch { AutomaticGoodsAction.Buy buy => buy.MaximumUnitPrice,
            AutomaticGoodsAction.Sell sell => sell.MinimumUnitPrice, _ => null })?.Value.ToString(CultureInfo.InvariantCulture) ?? "");

    private void RenderEditor()
    {
        Clear(_editor); _ruleControls.Clear();
        if (_selected is not { } row) { _editor.AddChild(Wrap("在地图上添加地点，或选择左侧的一站。")); return; }
        _editor.AddChild(UiStyle.Label($"{PlaceName(row.Input.Node)}", true));
        if (row.Notice is { } notice) { var label = Wrap(notice); label.Name = "StopNotice"; _editor.AddChild(label); }
        _editor.AddChild(UiStyle.Label("类型"));
        var kind = new OptionButton { Name = "Kind" }; kind.AddItem("停靠站"); kind.AddItem("途经点");
        kind.Select(row.Input.Station ? 0 : 1); kind.SetItemDisabled(1, row.Input.GoodsRules is { Count: > 0 }); _editor.AddChild(kind);
        kind.TooltipText = row.Input.GoodsRules is { Count: > 0 } ? "请先删除交易规则，再改为途经点。" : "途经点只经过，不停车。";
        kind.ItemSelected += value => { row.Input = row.Input with { Station = value == 0 }; RenderEditor(); Changed(); };
        if (!row.Input.Station) return;
        _editor.AddChild(UiStyle.Label("停留时间（秒）"));
        var seconds = new LineEdit { Name = "DwellSeconds", Text = row.Input.DwellSeconds };
        _editor.AddChild(seconds);
        seconds.TextChanged += text =>
        { row.Input = row.Input with { DwellSeconds = text, UnchangedDwellSteps = text == row.InitialSeconds ? row.InitialSteps : null }; Changed(); };
        _editor.AddChild(UiStyle.Label("交易规则", true));
        var rules = new VBoxContainer { Name = "Rules" }; _editor.AddChild(rules);
        for (var index = 0; index < (row.Input.GoodsRules?.Count ?? 0); index++) CreateRule(row, index, rules);
        var add = UiStyle.Button("AddRule", "添加交易规则", () =>
        {
            var choices = TrainPlanInput.Choices(_view, row.Input.Node);
            if (choices.Count == 0) return;
            var first = choices[0];
            row.Input = row.Input with { GoodsRules = [.. row.Input.GoodsRules ?? [], new(first.Action, null, first.Inventory, "0", "")] };
            RenderEditor(); Changed();
        });
        _editor.AddChild(add);
        var available = TrainPlanInput.Choices(_view, row.Input.Node).Count > 0; add.Disabled = !available;
        if (!available) _editor.AddChild(Wrap("这个地点没有可用的交易动作或本地库存。"));
        RefreshChoices();
    }

    private void CreateRule(Row stop, int index, VBoxContainer parent)
    {
        var input = stop.Input.GoodsRules![index];
        var box = new VBoxContainer { Name = $"Rule{index}" }; parent.AddChild(box);
        var good = new OptionButton { Name = "Good", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        Field(box, "货物", good, "GoodLabel");
        var action = new OptionButton { Name = "Action", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        Field(box, "动作", action, "ActionLabel");
        var inventory = new OptionButton { Name = "Inventory", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        Field(box, "本地库存", inventory, "InventoryLabel");
        var target = new LineEdit { Name = "TargetQuantity", Text = input.TargetQuantity };
        Field(box, "目标持有量", target, "TargetQuantityLabel");
        var priceColumn = new VBoxContainer(); box.AddChild(priceColumn);
        var priceLabel = UiStyle.Label(""); priceLabel.Name = "UnitPriceLabel"; priceColumn.AddChild(priceLabel);
        var price = new LineEdit { Name = "UnitPrice", Text = input.UnitPrice, PlaceholderText = "留空为不限价" }; priceColumn.AddChild(price);
        price.TextChanged += value => EditRule(stop, index, rule => rule with { UnitPrice = value });
        var summary = Wrap(""); summary.Name = "RuleSummary"; box.AddChild(summary);
        var problems = Wrap(""); problems.Name = "RuleProblems"; problems.AddThemeColorOverride("font_color", UiStyle.Error); box.AddChild(problems);
        var controls = new RuleControls(index, action, [], good, [], inventory, [], priceColumn, priceLabel, summary, problems); _ruleControls.Add(controls);
        good.ItemSelected += value =>
        { if (!_bindingChoices) EditRule(stop, index, rule => rule with { Good = controls.Goods[(int)value] }); };
        action.ItemSelected += value =>
        {
            if (_bindingChoices) return;
            var selected = controls.Actions[(int)value];
            EditRule(stop, index, rule => rule with { Action = selected,
                Inventory = TrainPlanInput.Choices(_view, stop.Input.Node).Where(item => item.Action == selected)
                    .Select(item => (InventoryId?)item.Inventory).FirstOrDefault() });
            RefreshChoices(); Validate();
        };
        inventory.ItemSelected += value =>
        { if (!_bindingChoices) EditRule(stop, index, rule => rule with { Inventory = controls.Inventories[(int)value] }); };
        target.TextChanged += value => EditRule(stop, index, rule => rule with { TargetQuantity = value });
        var buttons = new HFlowContainer(); box.AddChild(buttons);
        var up = UiStyle.Button("RuleUp", "规则上移", () => MoveRule(stop, index, -1)); up.Disabled = index == 0; buttons.AddChild(up);
        var down = UiStyle.Button("RuleDown", "规则下移", () => MoveRule(stop, index, 1));
        down.Disabled = index == stop.Input.GoodsRules!.Count - 1; buttons.AddChild(down);
        buttons.AddChild(UiStyle.Button("RemoveRule", "删除规则", () =>
        {
            stop.Input = stop.Input with { GoodsRules = stop.Input.GoodsRules!.Where((_, at) => at != index).ToArray() };
            RenderEditor(); Changed();
        }));
        box.AddChild(new HSeparator());
    }

    private static void Field(VBoxContainer box, string text, Control control, string name)
    {
        var label = UiStyle.Label(text); label.Name = name; box.AddChild(label); box.AddChild(control);
        control.TooltipText = text;
    }
    private void EditRule(Row stop, int index, Func<GoodsRuleInput, GoodsRuleInput> edit)
    {
        var rules = stop.Input.GoodsRules!.ToArray(); rules[index] = edit(rules[index]);
        stop.Input = stop.Input with { GoodsRules = rules }; Changed();
    }
    private void MoveRule(Row stop, int index, int offset)
    {
        var next = index + offset; var rules = stop.Input.GoodsRules!.ToArray();
        if (next < 0 || next >= rules.Length) return;
        (rules[index], rules[next]) = (rules[next], rules[index]); stop.Input = stop.Input with { GoodsRules = rules };
        RenderEditor(); Changed();
    }

    private void RefreshChoices()
    {
        if (_selected is not { } stop) return;
        _bindingChoices = true;
        try
        {
            var choices = TrainPlanInput.Choices(_view, stop.Input.Node);
            if (_editor.GetNodeOrNull<Button>("AddRule") is { } add) add.Disabled = choices.Count == 0;
            foreach (var controls in _ruleControls)
            {
                var rule = stop.Input.GoodsRules![controls.Index];
                controls.PriceColumn.Visible = rule.Action is TradeAction.Buy or TradeAction.Sell;
                controls.PriceLabel.Text = rule.Action == TradeAction.Buy ? "最高单价" : "最低单价";
                TradeAction[] actions = choices.Select(item => item.Action).Distinct().ToArray();
                BindChoices(controls.Action, controls.Actions, actions, actions.Select(ActionName).ToArray(), rule.Action);
                if (controls.Action.Selected < 0) controls.Action.Text = ActionName(rule.Action) + "（本站不可用）";
                GoodId?[] goods = [null, .. _content.Core.Goods.Concat(_content.Core.DesignGoods).Select(item => item.Good)
                    .Concat(rule.Good is { } good ? [good] : []).Distinct()];
                BindChoices(controls.Good, controls.Goods, goods, goods.Select(GoodName).ToArray(), rule.Good);
                InventoryId?[] inventories = [null, .. choices.Where(item => item.Action == rule.Action).Select(item => (InventoryId?)item.Inventory).Distinct()];
                BindChoices(controls.Inventory, controls.Inventories, inventories,
                    inventories.Select(id => id is { } value ? InventoryName(value) : "请选择本地库存").ToArray(), rule.Inventory);
                if (controls.Inventory.Selected < 0) controls.Inventory.Text = "库存已失效或不在本站";
            }
        }
        finally { _bindingChoices = false; }
    }
    private static void BindChoices<T>(OptionButton button, List<T> current, T[] next, string[] labels, T selected)
    {
        if (!current.SequenceEqual(next))
        {
            button.Clear(); current.Clear(); current.AddRange(next);
            for (var index = 0; index < labels.Length; index++) button.AddItem(labels[index], next[index] is TradeAction action ? (int)action : index);
        }
        else for (var index = 0; index < labels.Length; index++) button.SetItemText(index, labels[index]);
        button.Select(current.IndexOf(selected));
    }
    private void UpdateRuleMessages()
    {
        if (_selected is not { } selected || _evaluation is null) return;
        var stop = _rows.IndexOf(selected);
        foreach (var controls in _ruleControls)
        {
            controls.Summary.Text = RuleSentence(selected.Input.GoodsRules![controls.Index]);
            controls.Problems.Text = string.Join("\n", _evaluation.Problems.Where(problem => problem.Stop == stop && problem.Rule == controls.Index).Select(problem => problem.Message));
            controls.Problems.Visible = controls.Problems.Text.Length > 0;
        }
    }
    private static string ActionName(TradeAction action) => action switch
    { TradeAction.Buy => "买入", TradeAction.Sell => "卖出", TradeAction.Load => "装载", TradeAction.Unload => "卸载", _ => "动作已不可用" };
    private string GoodName(GoodId? good) => good is null ? "请选择货物" :
        _content.FindGood(good) is ContentLookup<GoodPresentation>.Found { Value.Missing: false } found
            ? found.Value.Display.Text.Name : "货物内容缺失";
    private string InventoryName(InventoryId id)
    {
        if (_view.FindInventory(id) is not Lookup<InventoryView>.Found inventory) return "库存已不存在";
        return inventory.Value.Owner is InventoryOwner.Lease ? "租用仓库" : "聚居地库存";
    }
    private string RuleSentence(GoodsRuleInput rule)
    {
        var good = GoodName(rule.Good); var quantity = rule.TargetQuantity;
        var store = rule.Inventory is { } inventory && _view.FindInventory(inventory) is Lookup<InventoryView>.Found { Value.Owner: InventoryOwner.Lease }
            ? "租用仓库" : "库存";
        var text = rule.Action switch
        {
            TradeAction.Buy => $"买入{good}，直到车上有 {quantity}",
            TradeAction.Sell => $"卖出{good}，直到车上剩 {quantity}",
            TradeAction.Load => $"从{store}装载{good}，直到车上有 {quantity}",
            TradeAction.Unload => $"卸载{good}到{store}，直到车上剩 {quantity}",
            _ => "动作已不可用，请重新选择。"
        };
        if (rule.UnitPrice.Length > 0)
            text += rule.Action switch
            { TradeAction.Buy => $"；单价高于 {rule.UnitPrice} 不买", TradeAction.Sell => $"；单价低于 {rule.UnitPrice} 不卖", _ => "" };
        return text;
    }
}
