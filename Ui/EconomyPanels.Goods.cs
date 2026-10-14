using System.Globalization;
using Godot;
using Train.App;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class EconomyPanels
{
    private OptionButton _tradeTrainChoice = null!;
    private readonly List<TrainId> _tradeTrains = [];
    private TrainId? _tradeTrain;
    private GoodId? _tradeGood;
    private VBoxContainer _marketRows = null!;
    private VBoxContainer _selectedGood = null!;
    private OptionButton _tradeAction = null!;
    private LineEdit _quantity = null!;
    private Label _tradeDescription = null!;
    private Button _tradeQuoteButton = null!;
    private Button _tradeConfirm = null!;
    private TradeQuote? _tradeQuote;

    private void BuildTrade(VBoxContainer body)
    {
        _tradeTrain = null; _tradeGood = null; _tradeQuote = null;
        _tradeTrainChoice = new OptionButton { Name = "Train" };
        body.AddChild(_tradeTrainChoice);
        _tradeTrainChoice.ItemSelected += index =>
        {
            _tradeTrain = index > 0 && index <= _tradeTrains.Count ? _tradeTrains[(int)index - 1] : null;
            ClearTradeQuote();
            RenderMarket(); UpdateTradeActions();
        };
        _marketRows = List(body, "Goods", 210);
        _selectedGood = new VBoxContainer { Name = "SelectedGood" }; body.AddChild(_selectedGood);
        _tradeAction = new OptionButton { Name = "Action" };
        foreach (var action in new[] { "买入", "卖出", "装载（本地 → 列车）", "卸载（列车 → 本地）" }) _tradeAction.AddItem(action);
        body.AddChild(_tradeAction);
        Text(body, "QuantityLabel", "货物数量（正整数）");
        _quantity = IntegerInput(body, "Quantity", "货物数量（正整数）", "1");
        _tradeAction.ItemSelected += _ => ClearTradeQuote();
        _quantity.TextChanged += _ => ClearTradeQuote();
        _tradeDescription = Text(body, "Quote", "请选择货物和列车，再获取报价。报价不锁定。");
        var actions = new HBoxContainer(); body.AddChild(actions);
        _tradeQuoteButton = UiStyle.Button("QuoteTrade", "获取报价", QuoteTrade);
        _tradeConfirm = UiStyle.Button("ConfirmTrade", "确认交易", ConfirmTrade, true);
        actions.AddChild(_tradeQuoteButton); actions.AddChild(_tradeConfirm);
    }

    private void RefreshTrade()
    {
        _tradeTrains.Clear(); _tradeTrainChoice.Clear(); _tradeTrainChoice.AddItem("请选择列车");
        foreach (var item in _view.World.Trains)
            if (_view.FindTrain(item.Id) is Lookup<TrainView>.Found { Value.Position: TrainPosition.Node node } && node.Id == _settlement!.Node)
            { _tradeTrains.Add(item.Id); _tradeTrainChoice.AddItem($"列车 {item.Id.Value}"); }
        var selected = _tradeTrain is { } id ? _tradeTrains.IndexOf(id) + 1 : 0;
        if (_tradeTrain is not null && selected == 0)
        { _tradeTrainChoice.AddItem("已不存在：所选列车已离开或解编"); selected = _tradeTrains.Count + 1; }
        _tradeTrainChoice.Select(selected);
        RenderMarket();
        if (_tradeQuote is not null) RequestTradeQuote();
    }

    private void RenderMarket()
    {
        Clear(_marketRows);
        var market = _settlement!.Market;
        if (!market.Rule.Problems.IsEmpty) Text(_marketRows, "RuleProblems", Problems(market.Rule.Problems)).AddThemeColorOverride("font_color", UiStyle.Error);
        var trainGoods = Array.Empty<InventoryStackView>();
        if (_tradeTrain is { } id && _view.FindTrain(id) is Lookup<TrainView>.Found train)
        {
            if (!_queries.TryRead(nameof(ICoreReadView.FindInventory), () => _view.FindInventory(train.Value.Inventory), out var lookup)) return;
            if (lookup is Lookup<InventoryView>.Found inventory) trainGoods = inventory.Value.Stacks.ToArray();
        }
        var goods = market.Goods.Select(item => item.Good)
            .Concat(Local?.Stacks.Select(stack => stack.Good) ?? [])
            .Concat(trainGoods.Select(stack => stack.Good))
            .Concat(_tradeGood is null ? [] : new[] { _tradeGood }).Distinct().ToArray();
        if (goods.Length == 0) Text(_marketRows, "Empty", "没有可显示的货物。");
        foreach (var good in goods)
        {
            var item = market.Goods.FirstOrDefault(item => item.Good == good);
            var row = GoodRow(_marketRows, good, item?.Stock, item is { Blockers.IsEmpty: false }, _tradeGood == good);
            var choose = UiStyle.Button("SelectGood", _tradeGood == good ? "已选择" : "选择", () =>
            {
                _tradeGood = good; Clear(_selectedGood); GoodRow(_selectedGood, good, null, selected: true, size: 48);
                ClearTradeQuote();
                RenderMarket();
            });
            row.AddChild(choose);
            if (item is not null)
                Text(_marketRows, "MarketAmounts", $"库存 {item.Stock} · 目标 {item.RestockTarget} · 留货 {item.ReservedForUse} · 可售 {item.SellableQuantity} · 可收 {item.ReceivableQuantity}");
            if (item is { Blockers.IsEmpty: false })
                Text(_marketRows, "Blockers", Problems(item.Blockers)).AddThemeColorOverride("font_color", UiStyle.Error);
        }
    }

    private void ClearTradeQuote()
    {
        _tradeQuote = null;
        _tradeDescription.Text = "报价不锁定。输入已改变，请获取报价。";
        _tradeDescription.AddThemeColorOverride("font_color", Colors.White);
        UpdateTradeActions();
    }

    private void QuoteTrade()
    {
        if (!CanAct) return;
        RequestTradeQuote();
        if (_queries.MissingEntry is { } entry) Unavailable(_settlementBody, _state, entry);
        else UpdateTradeActions();
    }

    private void RequestTradeQuote()
    {
        _tradeQuote = null;
        if (_tradeTrain is not { } train || _tradeGood is not { } good || Local is not { } inventory ||
            !long.TryParse(_quantity.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var quantity) || quantity <= 0)
        { _tradeDescription.Text = "请选择列车、本地库存和货物，并输入正整数数量。报价不锁定。"; return; }
        var request = new TradeQuoteRequest(train, _settlementId, inventory.Id, (TradeAction)_tradeAction.Selected, good, quantity);
        if (!_queries.TryRead(nameof(ICoreReadView.QuoteTrade), () => _view.QuoteTrade(request), out var result)) return;
        _tradeQuote = QuoteValue(result, _tradeDescription);
        if (_tradeQuote is not { } quote) return;
        _tradeDescription.Text = $"报价不锁定\n当前可执行数量：{quote.CurrentlyExecutableQuantity}\n总额：{Amount(quote.Total)}" +
            (quote.Blockers.IsEmpty ? "" : "\n受阻：\n" + Problems(quote.Blockers)) +
            (quote.ShortageWarnings.IsEmpty ? "" : "\n短缺风险：\n" + Problems(quote.ShortageWarnings));
        _tradeDescription.AddThemeColorOverride("font_color", !quote.Blockers.IsEmpty ? UiStyle.Error :
            !quote.ShortageWarnings.IsEmpty ? UiStyle.Warning : Colors.White);
    }

    private void ConfirmTrade()
    {
        if (_tradeQuote is not { Total: { } total, Blockers.IsEmpty: true } quote || _tradeConfirm.Disabled) return;
        var request = quote.Request;
        CoreCommand command = request.Action switch
        {
            TradeAction.Buy => new CoreCommand.BuyGoods(request.Train, request.Settlement, request.LocalInventory, request.Good, request.Quantity, total),
            TradeAction.Sell => new CoreCommand.SellGoods(request.Train, request.Settlement, request.LocalInventory, request.Good, request.Quantity, total),
            TradeAction.Load => new CoreCommand.LoadGoods(request.Train, request.Settlement, request.LocalInventory, request.Good, request.Quantity),
            TradeAction.Unload => new CoreCommand.UnloadGoods(request.Train, request.Settlement, request.LocalInventory, request.Good, request.Quantity),
            _ => throw new InvalidOperationException("未知货物操作。")
        };
        Submit(new(_settlementTarget!.Binding, _settlementTarget.Generation, new WorldObjectId.Inventory(request.LocalInventory), LocalLease?.Id), command);
    }

    private void UpdateTradeActions()
    {
        _tradeQuoteButton.Disabled = !CanAct || Local is null;
        _tradeConfirm.Disabled = !CanAct || Local is null || _tradeQuote is not { Total: not null, Blockers.IsEmpty: true } quote ||
            quote.CurrentlyExecutableQuantity < quote.Request.Quantity;
        _tradeTrainChoice.Disabled = _tradeAction.Disabled = !CanAct;
        _quantity.Editable = CanAct;
    }

    private static T? QuoteValue<T>(CoreResult<T> result, Label description) where T : class
    {
        if (result is CoreResult<T>.Success success) return success.Value;
        description.Text = result switch
        {
            CoreResult<T>.Failure failure => "报价被拒绝：\n" + Problems(failure.Problems),
            CoreResult<T>.Faulted fault => CommandDiagnostics.Describe(fault.Fault),
            _ => throw new InvalidOperationException("未知报价结果。")
        };
        description.AddThemeColorOverride("font_color", UiStyle.Error);
        return null;
    }

    private static string Amount(Money? money) => money?.Value.ToString(CultureInfo.InvariantCulture) ?? "不可报价";
}
