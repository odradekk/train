using System.Collections.Immutable;
using Godot;
using Train.Core.Contracts;
using static Train.Ui.EconomyPanelControls;

namespace Train.Ui;

internal sealed partial class EconomyReportsWorkspace
{
    private const float ChartHalfHeight = 32;
    private ImmutableArray<CashFlowEntry> _cashFlows;
    private ulong? _cashFlowDay;
    private Label _netCashFlow = null!, _income = null!, _expenses = null!;
    private HBoxContainer _dailyCashFlow = null!;

    private void BuildCashFlowSummary(VBoxContainer totals)
    {
        _netCashFlow = Text(totals, "NetCashFlow", "本局净现金流：—");
        _income = Text(totals, "Income", "收入 0");
        _expenses = Text(totals, "Expenses", "支出 0");
        Text(_body, "DailyCashFlowTitle", "最近 7 个游戏日净现金流");
        _dailyCashFlow = new HBoxContainer { Name = "DailyCashFlow" }; _body.AddChild(_dailyCashFlow);
    }

    private void RefreshCashFlowSummary(ICoreReadView view, bool changed)
    {
        if (changed)
        {
            var (income, expenses) = EconomyReportData.Totals(_cashFlows);
            var net = checked(income + expenses);
            _netCashFlow.Text = $"本局净现金流：{Signed(net)}";
            _netCashFlow.AddThemeColorOverride("font_color", CashFlowColor(net));
            _income.Text = $"收入 {Signed(income)}";
            _income.AddThemeColorOverride("font_color", CashFlowColor(income));
            _expenses.Text = $"支出 {Signed(expenses)}";
            _expenses.AddThemeColorOverride("font_color", CashFlowColor(expenses));
        }
        var today = view.Position.CompletedSteps / UiInput.StepsPerDay;
        if (!changed && _cashFlowDay == today) return;
        _cashFlowDay = today;
        Clear(_dailyCashFlow);
        var days = EconomyReportData.Days(_cashFlows, view.Position.CompletedSteps);
        var maximum = Math.Max(1, days.Max(day => Math.Abs((double)day.Amount)));
        foreach (var (day, amount) in days)
        {
            var column = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _dailyCashFlow.AddChild(column);
            var value = Text(column, $"Amount{day}", Signed(amount));
            value.HorizontalAlignment = HorizontalAlignment.Center;
            value.AddThemeColorOverride("font_color", CashFlowColor(amount));
            var chart = new Control { CustomMinimumSize = new(0, ChartHalfHeight * 2) }; column.AddChild(chart);
            chart.Draw += () =>
            {
                chart.DrawLine(new Vector2(0, ChartHalfHeight), new Vector2(chart.Size.X, ChartHalfHeight), UiStyle.Info);
                var height = (float)(Math.Abs((double)amount) / maximum * ChartHalfHeight);
                chart.DrawRect(new Rect2(chart.Size.X * .2f, amount >= 0 ? ChartHalfHeight - height : ChartHalfHeight,
                    chart.Size.X * .6f, height), CashFlowColor(amount));
            };
            chart.Resized += chart.QueueRedraw;
            var label = Text(column, $"Day{day}", $"第{day + 1}天" + (day == today ? "（至今）" : ""));
            label.HorizontalAlignment = HorizontalAlignment.Center;
        }
    }

    private static string Signed(long amount) => amount.ToString("+0;-0;0", System.Globalization.CultureInfo.InvariantCulture);
    private static Color CashFlowColor(long amount) => amount > 0 ? UiStyle.Success : amount < 0 ? UiStyle.Error : UiStyle.Foreground;
}
