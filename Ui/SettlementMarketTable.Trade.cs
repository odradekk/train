using System.Collections.Immutable;
using System.Globalization;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal sealed partial class SettlementMarketTable
{
    private void RequestQuote()
    {
        _quote = null;
        if (_good is not { } good) return;
        if (!canAct()) { _description.Text = "当前操作不可用，请重新打开聚居地。"; return; }
        if (_train is not { } train || !TrainPresent)
        {
            _description.Text = _trains.All(item => item is null) ? "没有列车在站。列车到站后才能交易。" : "请选择在站列车。";
            return;
        }
        if (Local is not { } local) { _description.Text = Owned ? "聚居地库存已不可用，请重新打开聚居地。" : "请选择租用仓库。"; return; }
        if (!long.TryParse(_quantity.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var quantity) || quantity <= 0)
        { _description.Text = "请输入正整数数量。"; return; }
        _quote = Read(new(train, _settlement.Id, local.Id, Action, good, quantity));
        if (_quote is not null) ShowQuote();
    }

    private TradeQuote? Read(TradeQuoteRequest request)
    {
        switch (_view.QuoteTrade(request))
        {
            case CoreResult<TradeQuote>.Success success: return success.Value;
            case CoreResult<TradeQuote>.Failure failure: _description.Text = Describe(failure.Problems); break;
            case CoreResult<TradeQuote>.Faulted fault: _description.Text = CommandDiagnostics.Describe(fault.Fault); break;
        }
        return null;
    }

    private void Maximum()
    {
        if (!canAct() || _train is not { } train || _good is not { } good || Local is not { } local || !TrainPresent) return;
        var quantity = Action == TradeAction.Buy
            ? _settlement.Market.Goods.FirstOrDefault(item => item.Good == good)?.SellableQuantity ?? 0
            : (Action == TradeAction.Load ? local : TrainInventory(train))?.Stacks.FirstOrDefault(item => item.Good == good)?.Quantity ?? 0;
        _result.Text = "";
        if (quantity == 0) { Changed(); return; }
        while (quantity > 0)
        {
            var quote = Read(new(train, _settlement.Id, local.Id, Action, good, quantity));
            if (quote is null) { Changed(); return; }
            if (quote.Blockers.IsEmpty && quote.CurrentlyExecutableQuantity >= quantity)
            {
                _quantity.Text = quantity.ToString(CultureInfo.InvariantCulture);
                _quote = quote; ShowQuote(); UpdateActions(); return;
            }
            if (quote.CurrentlyExecutableQuantity <= 0 || quote.CurrentlyExecutableQuantity >= quantity)
            {
                Changed(); return;
            }
            quantity = quote.CurrentlyExecutableQuantity;
        }
    }

    private void ShowQuote()
    {
        var quote = _quote!;
        var free = quote.Request.Action is TradeAction.Load or TradeAction.Unload;
        var total = quote.Total?.Value;
        var amount = total?.ToString(CultureInfo.InvariantCulture) ?? (free ? "0" : "无法报价");
        _description.Text = $"报价不锁定\n当前可执行数量：{quote.CurrentlyExecutableQuantity}\n总额：{amount}";
        if (free || total is not null)
        {
            var balance = (decimal)_view.Economy.Wallet.Value + (free ? 0 : quote.Request.Action == TradeAction.Buy ? -total!.Value : total!.Value);
            _description.Text += $"\n交易后资金：{balance.ToString(CultureInfo.InvariantCulture)}";
        }
        if (!quote.Blockers.IsEmpty) _description.Text += "\n" + Describe(quote.Blockers);
        if (!quote.ShortageWarnings.IsDefaultOrEmpty) _description.Text += "\n短缺风险：\n" + Describe(quote.ShortageWarnings);
        if (quote.CurrentlyExecutableQuantity < quote.Request.Quantity)
            _description.Text += "\n可执行数量不足，请减少数量或点“最大”。";
        if (!free && total is null) _description.Text += "\n当前无法取得交易金额。";
        _description.AddThemeColorOverride("font_color", !quote.Blockers.IsEmpty ? UiStyle.Error :
            !quote.ShortageWarnings.IsDefaultOrEmpty || quote.CurrentlyExecutableQuantity < quote.Request.Quantity ? UiStyle.Warning : UiStyle.Foreground);
        _confirm.Text = $"{ActionName(quote.Request.Action)} {quote.Request.Quantity} 件 · {amount}";
    }

    internal void UpdateActions()
    {
        var available = canAct();
        foreach (var row in _items.Values) row.Select.Disabled = !available;
        _trainChoice.Disabled = _action.Disabled = _leaseChoice.Disabled = !available;
        _quantity.Editable = available;
        _maximum.Disabled = !available || !TrainPresent || Local is null;
        _leaseChoice.Visible = _leaseLabel.Visible = !Owned && Transfer && _leases.Count(item => item is not null) > 1;
        _confirm.Disabled = !available || !TrainPresent || _quote is not { Blockers.IsEmpty: true } quote ||
            quote.CurrentlyExecutableQuantity < quote.Request.Quantity ||
            (quote.Request.Action is TradeAction.Buy or TradeAction.Sell && quote.Total is null);
        if (_quote is null) _confirm.Text = $"{ActionName(Action)} · 暂不可交易";
        if (_train is { } train && _good is { } good && TrainInventory(train) is { } inventory)
            _cargo.Text = $"车上{SettlementOverviewPage.GoodName(good, _content)}：{inventory.Stacks.FirstOrDefault(item => item.Good == good)?.Quantity ?? 0} · 剩余载量 {inventory.Available}";
        else _cargo.Text = "";
    }

    private bool TrainPresent => _train is { } train && _view.FindTrain(train) is Lookup<TrainView>.Found
        { Value.Position: TrainPosition.Node node } && node.Id == _settlement.Node;

    private void Confirm()
    {
        var quote = _pressedQuote ?? _quote;
        _pressedQuote = null;
        if (!canAct() || _confirm.Disabled || quote is null) return;
        var request = quote.Request;
        CoreCommand command = request.Action switch
        {
            TradeAction.Buy => new CoreCommand.BuyGoods(request.Train, request.Settlement, request.LocalInventory, request.Good, request.Quantity, quote.Total!.Value),
            TradeAction.Sell => new CoreCommand.SellGoods(request.Train, request.Settlement, request.LocalInventory, request.Good, request.Quantity, quote.Total!.Value),
            TradeAction.Load => new CoreCommand.LoadGoods(request.Train, request.Settlement, request.LocalInventory, request.Good, request.Quantity),
            _ => new CoreCommand.UnloadGoods(request.Train, request.Settlement, request.LocalInventory, request.Good, request.Quantity)
        };
        var result = submit(_target, command);
        _result.Text = result switch
        {
            SubmissionResult.NotAccepted rejected => Describe([rejected.Reason]),
            SubmissionResult.Completed { Receipt.Decision: CommandDecision.Rejected rejected } => Describe(rejected.Problems),
            SubmissionResult.Completed { Receipt.Decision: CommandDecision.Faulted fault } => CommandDiagnostics.Describe(fault.Fault),
            SubmissionResult.Completed accepted => Transferred(accepted.Events, request),
            _ => "交易未完成。"
        };
    }

    private string Transferred(ImmutableArray<EventEnvelope> events, TradeQuoteRequest request)
    {
        var transferred = events.Select(item => item.Event).OfType<CoreEvent.GoodsTransferred>()
            .FirstOrDefault(item => item.Train == request.Train && item.Settlement == request.Settlement && item.Good == request.Good && item.Action == request.Action);
        return transferred is null ? "交易已受理。" :
            $"已{ActionName(transferred.Action)}{SettlementOverviewPage.GoodName(transferred.Good, _content)} {transferred.Quantity} 件 · 实际金额 {transferred.ActualAmount.Value}";
    }

    private string Describe(ImmutableArray<CoreProblem> problems) =>
        string.Join("\n", problems.Select(problem => CommandDiagnostics.Describe(problem with { Subject = null }, _content)));
}
