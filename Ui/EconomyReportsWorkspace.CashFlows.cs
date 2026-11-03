using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Ui.EconomyPanelControls;

namespace Train.Ui;

internal sealed partial class EconomyReportsWorkspace
{
    private const int CashFlowBatchSize = EconomyReportData.CashFlowBatchSize;
    private int _cashFlowLimit = CashFlowBatchSize;
    private VBoxContainer _cashFlowBody = null!;
    private Label _emptyCashFlows = null!;
    private Button _earlierCashFlows = null!;
    private ICoreReadView _cashFlowView = null!;
    private IContentView _cashFlowContent = null!;
    private readonly Dictionary<int, Label> _cashFlowRows = [];

    private void BuildCashFlows(VBoxContainer page)
    {
        _emptyCashFlows = Text(page, "EmptyCashFlows", "本局尚无收支记录。");
        _cashFlowBody = new VBoxContainer { Name = "CashFlowRows" }; page.AddChild(_cashFlowBody);
        _earlierCashFlows = UiStyle.Button("ShowEarlierCashFlows", "显示更早的 200 笔", () =>
        {
            if (host.IsBusy || host.GetStatus() is not { } status || !Current(host, status.Binding, _binding, _generation)) return;
            _cashFlowLimit += CashFlowBatchSize;
            RefreshCashFlowRows();
        });
        page.AddChild(_earlierCashFlows);
    }

    private void RefreshCashFlows(ICoreReadView view, IContentView content, bool changed)
    {
        _cashFlowView = view; _cashFlowContent = content;
        if (changed) RefreshCashFlowRows();
        _earlierCashFlows.Disabled = host.IsBusy;
    }

    private void RefreshCashFlowRows()
    {
        _cashFlowLimit = EconomyReportData.CashFlowLimit(_cashFlowLimit,
            _cashFlowRows.Keys.DefaultIfEmpty(-1).Max() + 1, _cashFlows.Length);
        var first = Math.Max(0, _cashFlows.Length - _cashFlowLimit);
        foreach (var index in _cashFlowRows.Keys.Where(index => index < first).ToArray())
        { RemoveControl(_cashFlowRows[index]); _cashFlowRows.Remove(index); }
        for (var index = _cashFlows.Length - 1; index >= first; index--)
        {
            if (!_cashFlowRows.TryGetValue(index, out var row))
            {
                var entry = _cashFlows[index];
                var location = entry.Settlement is { } settlement ? " · " + SettlementName(settlement, _cashFlowView, _cashFlowContent) : "";
                var train = entry.Train is { } id ? $" · 列车 {id.Value}" : "";
                var goods = entry.Good is { } good ? $" · {SettlementOverviewPage.GoodName(good, _cashFlowContent)} ×{entry.Quantity}" : "";
                row = Text(_cashFlowBody, $"CashFlow{index}",
                    $"{UiInput.FormatTime(entry.Position.CompletedSteps)} · {EconomyReportData.KindText(entry.Kind)}{location}{train}{goods} · {Signed(entry.Amount)}");
                row.AddThemeColorOverride("font_color", CashFlowColor(entry.Amount));
                _cashFlowRows.Add(index, row);
            }
            _cashFlowBody.MoveChild(row, _cashFlows.Length - 1 - index);
        }
        _emptyCashFlows.Visible = _cashFlows.IsEmpty;
        _earlierCashFlows.Visible = first > 0;
    }

    private static string SettlementName(SettlementId settlement, ICoreReadView view, IContentView content) =>
        view.World.Nodes.FirstOrDefault(node => node.Occupant is NodeOccupant.Settlement occupant && occupant.Id == settlement) is { } node
            ? WorldPanels.NodeName(node.Id, view, content) : "聚居地已不存在";
}
