using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Ui.EconomyPanelControls;

namespace Train.Ui;

internal sealed partial class EconomyReportsWorkspace
{
    private VBoxContainer _ownedSettlements = null!, _tradeSettlements = null!;
    private Label _emptyOwnedSettlements = null!, _emptyTradeSettlements = null!;
    private readonly Dictionary<SettlementId, Label> _ownedSettlementRows = [];
    private readonly Dictionary<SettlementId, TradeRow> _tradeSettlementRows = [];
    private sealed record TradeRow(VBoxContainer Root, Label Name, Label Sales, Label Purchases, Label Services);

    private void BuildSettlements(VBoxContainer page)
    {
        Text(page, "OwnedSettlementsTitle", "自有聚居地");
        _ownedSettlements = new VBoxContainer(); page.AddChild(_ownedSettlements);
        _emptyOwnedSettlements = Text(page, "EmptyOwnedSettlements", "没有自有聚居地。");
        Text(page, "TradeSettlementsTitle", "其他聚居地的本局收支");
        _tradeSettlements = new VBoxContainer(); page.AddChild(_tradeSettlements);
        _emptyTradeSettlements = Text(page, "EmptyTradeSettlements", "暂无其他聚居地的收支。");
    }

    private void RefreshSettlements(ICoreReadView view, IContentView content, bool cashChanged)
    {
        var owned = view.World.Nodes.Select(node => node.Occupant).OfType<NodeOccupant.Settlement>()
            .Where(item => item.Relation == SettlementRelation.PlayerOwned).Select(item => item.Id).ToArray();
        foreach (var id in _ownedSettlementRows.Keys.Except(owned).ToArray())
        { RemoveControl(_ownedSettlementRows[id]); _ownedSettlementRows.Remove(id); }
        foreach (var id in owned)
        {
            if (view.FindSettlement(id) is not Lookup<SettlementView>.Found found) continue;
            if (!_ownedSettlementRows.TryGetValue(id, out var row))
            { row = Text(_ownedSettlements, $"OwnedSettlement{id.Value}", ""); _ownedSettlementRows.Add(id, row); }
            var settlement = found.Value;
            var period = settlement.Population.Execution switch
            {
                PopulationExecution.InProgress active => active.Period,
                PopulationExecution.Disabled disabled => disabled.RetainedPeriod,
                _ => null
            };
            static string Ratio(double? value) => value is { } ratio ? $"{ratio:P1}" : "暂无统计";
            var production = settlement.TodayProduction.IsEmpty ? "暂无完成的产出" : string.Join("、",
                settlement.TodayProduction.Select(item => $"{SettlementOverviewPage.GoodName(item.Good, content)} {item.Quantity}"));
            var attention = SettlementAttention.Read(view, content, settlement);
            var issues = attention.Buildings.Select(item => item.Text).Concat(attention.Goods.Select(item => item.Text)).Concat(attention.Sites).ToArray();
            row.Text = $"{SettlementName(id, view, content)}\n常住人口 {settlement.Population.Residents}" +
                $" · 基本需求满足率 {Ratio(period?.BasicSatisfaction)} · 改善需求满足率 {Ratio(period?.ImprovementSatisfaction)}" +
                $"\n当天产出：{production}\n需要处理的事项：" + (issues.Length == 0 ? "无" : "\n" + string.Join("\n", issues));
        }
        _emptyOwnedSettlements.Visible = _ownedSettlementRows.Count == 0;
        var trading = _cashFlows.Where(entry => entry.Settlement is not null).Select(entry => entry.Settlement!.Value).Distinct().Except(owned).ToArray();
        foreach (var id in _tradeSettlementRows.Keys.Except(trading).ToArray())
        { RemoveControl(_tradeSettlementRows[id].Root); _tradeSettlementRows.Remove(id); }
        foreach (var id in trading)
        {
            var added = !_tradeSettlementRows.TryGetValue(id, out var row);
            if (added)
            {
                var root = new VBoxContainer { Name = $"TradeSettlement{id.Value}" }; _tradeSettlements.AddChild(root);
                var name = Text(root, "Name", "");
                var amounts = new HFlowContainer(); root.AddChild(amounts);
                Label Amount(string key)
                {
                    var label = Text(amounts, key, ""); label.CustomMinimumSize = new(SummaryColumnWidth, 0);
                    label.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin; return label;
                }
                row = new(root, name, Amount("Sales"), Amount("Purchases"), Amount("ServicesAndRent"));
                _tradeSettlementRows.Add(id, row);
            }
            if (!added && !cashChanged) continue;
            var (sales, purchases, services) = EconomyReportData.SettlementTotals(_cashFlows, id);
            row!.Name.Text = SettlementName(id, view, content);
            row.Sales.Text = $"卖出 {Signed(sales)}"; row.Sales.AddThemeColorOverride("font_color", CashFlowColor(sales));
            row.Purchases.Text = $"买入 {Signed(purchases)}"; row.Purchases.AddThemeColorOverride("font_color", CashFlowColor(purchases));
            row.Services.Text = $"服务与租金 {Signed(services)}"; row.Services.AddThemeColorOverride("font_color", CashFlowColor(services));
        }
        _emptyTradeSettlements.Visible = _tradeSettlementRows.Count == 0;
    }
}
