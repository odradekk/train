using System.Globalization;
using Godot;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class EconomyPanels
{
    private OptionButton _tradeTrainChoice = null!;
    private readonly List<TrainId?> _tradeTrains = [];
    private TrainId? _tradeTrain;
    private GoodId? _tradeGood;
    private VBoxContainer _marketRows = null!;
    private Label _marketProblems = null!;
    private Label _marketEmpty = null!;
    private readonly Dictionary<GoodId, MarketRow> _marketItems = [];
    private VBoxContainer _selectedGood = null!;
    private GoodsRow? _selectedRow;
    private OptionButton _tradeAction = null!;
    private LineEdit _quantity = null!;
    private Label _tradeDescription = null!;
    private Button _tradeQuoteButton = null!;
    private Button _tradeConfirm = null!;
    private TradeQuote? _tradeQuote;
    private sealed record MarketRow(VBoxContainer Control, GoodsRow Good, Button Choose, Label Amounts, Label Blockers);

    private void BuildTrade(VBoxContainer body)
    {
        _tradeTrain = null; _tradeGood = null; _tradeQuote = null; _selectedRow = null;
        _tradeTrains.Clear(); _marketItems.Clear();
        _tradeTrainChoice = new OptionButton { Name = "Train" }; body.AddChild(_tradeTrainChoice);
        _tradeTrainChoice.ItemSelected += index =>
        { _tradeTrain = _tradeTrains[(int)index]; ClearTradeQuote(); RenderMarket(); };
        _marketRows = List(body, "Goods", 210);
        _marketProblems = Text(_marketRows, "RuleProblems", "");
        _marketProblems.AddThemeColorOverride("font_color", UiStyle.Error);
        _marketEmpty = Text(_marketRows, "Empty", "没有可显示的货物。");
        _selectedGood = new VBoxContainer { Name = "SelectedGood" }; body.AddChild(_selectedGood);
        _tradeAction = new OptionButton { Name = "Action" };
        foreach (var (action, title) in new[] { (TradeAction.Buy, "买入"), (TradeAction.Sell, "卖出"),
            (TradeAction.Load, "装载（本地 → 列车）"), (TradeAction.Unload, "卸载（列车 → 本地）") })
            _tradeAction.AddItem(title, (int)action);
        body.AddChild(_tradeAction);
        Text(body, "QuantityLabel", "货物数量（正整数）");
        _quantity = IntegerInput(body, "Quantity", "货物数量（正整数）", "1");
        _tradeAction.ItemSelected += _ => ClearTradeQuote(); _quantity.TextChanged += _ => ClearTradeQuote();
        var actions = Footer(body);
        _tradeQuoteButton = UiStyle.Button("QuoteTrade", "获取报价", () => Quote(RequestTradeQuote));
        _tradeConfirm = UiStyle.Button("ConfirmTrade", "确认交易", ConfirmTrade, true);
        actions.AddChild(_tradeQuoteButton); actions.AddChild(_tradeConfirm);
        _tradeDescription = Text(body, "Quote", "请选择货物和列车，再获取报价。报价不锁定。");
    }

    private void RefreshTrade()
    {
        var trains = _view.World.Trains.Where(item => _view.FindTrain(item.Id) is Lookup<TrainView>.Found
            { Value.Position: TrainPosition.Node node } && node.Id == _settlement!.Node).Select(item => item.Id).ToArray();
        SyncChoices(_tradeTrainChoice, _tradeTrains, trains, _tradeTrain, id => $"列车 {id.Value}", "请选择列车");
        if (_tradeTrain is { } train && !trains.Contains(train)) MissingSelection(ref _tradeQuote, _tradeDescription, "列车");
        RenderMarket();
    }

    private void RenderMarket()
    {
        var market = _settlement!.Market;
        _marketProblems.Text = Problems(market.Rule.Problems, _content); _marketProblems.Visible = !market.Rule.Problems.IsEmpty;
        var trainGoods = Array.Empty<InventoryStackView>();
        if (_tradeTrain is { } id && _view.FindTrain(id) is Lookup<TrainView>.Found train)
        {
            if (_view.FindInventory(train.Value.Inventory) is Lookup<InventoryView>.Found inventory) trainGoods = inventory.Value.Stacks.ToArray();
        }
        var goods = market.Goods.Select(item => item.Good).Concat(Local?.Stacks.Select(stack => stack.Good) ?? [])
            .Concat(trainGoods.Select(stack => stack.Good)).Concat(_tradeGood is null ? [] : new[] { _tradeGood }).Distinct().ToArray();
        _marketEmpty.Visible = goods.Length == 0;
        foreach (var good in _marketItems.Keys.Except(goods).ToArray())
        { RemoveControl(_marketItems[good].Control); _marketItems.Remove(good); }
        foreach (var good in goods)
        {
            var item = market.Goods.FirstOrDefault(item => item.Good == good);
            if (!_marketItems.TryGetValue(good, out var row))
            {
                var box = new VBoxContainer(); _marketRows.AddChild(box);
                var display = CreateGoodRow(box, good);
                var choose = UiStyle.Button("SelectGood", "选择", () =>
                {
                    if (_tradeGood != good)
                    {
                        _tradeGood = good; Clear(_selectedGood); _selectedRow = CreateGoodRow(_selectedGood, good, 48);
                        UpdateGoodRow(_selectedRow, good, null, selected: true); ClearTradeQuote();
                    }
                    RenderMarket();
                });
                display.Control.AddChild(choose);
                row = new(box, display, choose, Text(box, "MarketAmounts", ""), Text(box, "Blockers", ""));
                row.Blockers.AddThemeColorOverride("font_color", UiStyle.Error); _marketItems.Add(good, row);
            }
            UpdateGoodRow(row.Good, good, item?.Stock, item is { Blockers.IsEmpty: false }, _tradeGood == good);
            row.Choose.Text = _tradeGood == good ? "已选择" : "选择"; row.Choose.Disabled = !CanAct;
            row.Amounts.Visible = item is not null;
            if (item is not null) row.Amounts.Text = $"库存 {item.Stock} · 补货目标 {item.RestockTarget} · 留货 {item.ReservedForUse} · 可售 {item.SellableQuantity} · 可收 {item.ReceivableQuantity}";
            row.Blockers.Text = item is null ? "" : Problems(item.Blockers, _content); row.Blockers.Visible = row.Blockers.Text.Length > 0;
        }
    }

    private void ClearTradeQuote()
    { ResetQuote(ref _tradeQuote, _tradeDescription); UpdateTradeActions(); }

    private void RequestTradeQuote()
    {
        _tradeQuote = null;
        if (_tradeTrain is not { } train || _tradeGood is not { } good || Local is not { } inventory ||
            !long.TryParse(_quantity.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var quantity) || quantity <= 0)
        { ResetQuote(ref _tradeQuote, _tradeDescription, "请选择列车、本地库存和货物，并输入正整数数量。"); return; }
        var request = new TradeQuoteRequest(train, _settlementId, inventory.Id, (TradeAction)_tradeAction.GetSelectedId(), good, quantity);
        _tradeQuote = ReadQuote(_view.QuoteTrade(request), _tradeDescription);
    }

    private void ConfirmTrade()
    {
        if (_tradeQuote is not { } quote || _tradeConfirm.Disabled) return;
        var request = quote.Request;
        CoreCommand command = request.Action switch
        {
            TradeAction.Buy => new CoreCommand.BuyGoods(request.Train, request.Settlement, request.LocalInventory, request.Good, request.Quantity, quote.Total!.Value),
            TradeAction.Sell => new CoreCommand.SellGoods(request.Train, request.Settlement, request.LocalInventory, request.Good, request.Quantity, quote.Total!.Value),
            TradeAction.Load => new CoreCommand.LoadGoods(request.Train, request.Settlement, request.LocalInventory, request.Good, request.Quantity),
            TradeAction.Unload => new CoreCommand.UnloadGoods(request.Train, request.Settlement, request.LocalInventory, request.Good, request.Quantity),
            _ => throw new InvalidOperationException("未知货物操作。")
        };
        Submit(new(_settlementTarget!.Binding, _settlementTarget.Generation, new WorldObjectId.Inventory(request.LocalInventory)), command, RequestTradeQuote);
    }

    private void UpdateTradeActions()
    {
        var trainPresent = _tradeTrain is { } train && _view.FindTrain(train) is Lookup<TrainView>.Found
            { Value.Position: TrainPosition.Node node } && node.Id == _settlement!.Node;
        _tradeQuoteButton.Disabled = !CanAct || Local is null || !trainPresent;
        _tradeConfirm.Disabled = !CanAct || Local is null || !trainPresent || _tradeQuote is not { Blockers.IsEmpty: true } quote ||
            quote.CurrentlyExecutableQuantity < quote.Request.Quantity ||
            (quote.Request.Action is TradeAction.Buy or TradeAction.Sell && quote.Total is null);
        _tradeTrainChoice.Disabled = _tradeAction.Disabled = !CanAct; _quantity.Editable = CanAct;
        if (_tradeQuote is { } displayed)
        {
            var free = displayed.Request.Action is TradeAction.Load or TradeAction.Unload;
            var reason = displayed.CurrentlyExecutableQuantity < displayed.Request.Quantity ? "可执行数量不足，不能确认全部输入数量。" :
                !free && displayed.Total is null ? "报价未提供总额，不能确认买卖。" : null;
            ShowQuote(_tradeDescription, displayed.Revision,
                $"当前可执行数量：{displayed.CurrentlyExecutableQuantity}\n总额：{(free && displayed.Total is null ? "免费装卸（无价格条件）" : Amount(displayed.Total))}",
                displayed.Blockers, displayed.ShortageWarnings, reason);
        }
    }

    private static string Amount(Money? money) => money?.Value.ToString(CultureInfo.InvariantCulture) ?? "不可报价";
}
