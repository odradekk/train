using System.Collections.Immutable;
using System.Globalization;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Ui.EconomyPanelControls;

namespace Train.Ui;

/// <summary>
/// 租用仓库的货物、期限和租赁操作。
/// Decision note: 2026-10-31-object-centric-management-screens
/// </summary>
internal sealed class SettlementWarehousePage(Func<bool> canAct, GoodsRows goods,
    Func<PanelTarget, CoreCommand, SubmissionResult> submit)
{
    private ICoreReadView _view = null!;
    private IContentView _content = null!;
    private SettlementView _settlement = null!;
    private IReadOnlyList<(InventoryView Inventory, LeaseView? Lease)> _locals = [];
    private PanelTarget _target = null!;
    private MarketRuleProjection? _rule;
    private OptionButton _leaseChoice = null!, _termChoice = null!;
    private readonly List<LeaseId?> _leaseIds = [];
    private LeaseId? _leaseId;
    private bool _selectOnOpen;
    private LineEdit _capacity = null!;
    private Label _capacityLabel = null!, _details = null!, _empty = null!, _duration = null!;
    private Label _termProblems = null!, _description = null!, _result = null!;
    private VBoxContainer _stock = null!;
    private GoodsRows.StockRows _stockRows = null!;
    private Button _confirm = null!;
    private LeaseQuote? _quote, _pressedQuote;
    private int _multiple = 1;

    internal void Bind(ICoreReadView view, IContentView content, SettlementView settlement,
        IReadOnlyList<(InventoryView Inventory, LeaseView? Lease)> locals, PanelTarget target)
    { _view = view; _content = content; _settlement = settlement; _locals = locals; _target = target; }

    internal void Build(VBoxContainer body)
    {
        _leaseId = null; _quote = _pressedQuote = null; _multiple = 1; _selectOnOpen = true; _leaseIds.Clear();
        Text(body, "WarehouseLabel", "租用仓库");
        _leaseChoice = new OptionButton { Name = "Lease" }; body.AddChild(_leaseChoice);
        _empty = Text(body, "WarehouseEmpty", "你在这里没有租用仓库。租用后可存放货物，等列车来装。");
        _details = Text(body, "LeaseDetails", "");
        _stock = new VBoxContainer { Name = "WarehouseGoods" }; body.AddChild(_stock);
        Text(_stock, "WarehouseGoodsHeading", "仓库里的货物");
        _stockRows = GoodsRows.BuildStock(_stock);
        Text(_stock, "WarehouseTransferHint", "在「市场」页点货物行，可在仓库与在站列车之间装卸。");
        _capacityLabel = Text(body, "LeaseCapacityLabel", "容量");
        _capacity = IntegerInput(body, "LeaseCapacity", "正整数容量");
        Text(body, "LeaseTermLabel", "本次租期");
        _termChoice = new OptionButton { Name = "LeaseMultiple" }; body.AddChild(_termChoice);
        _duration = Text(body, "LeaseDuration", "");
        _termProblems = Text(body, "LeaseTermProblems", "");
        _description = Text(body, "LeaseQuote", "");
        _result = Text(body, "LeaseResult", "");
        _confirm = UiStyle.Button("ConfirmLease", "租用", Confirm, true); Footer(body).AddChild(_confirm);
        _leaseChoice.ItemSelected += index => { _leaseId = _leaseIds[(int)index]; _result.Text = ""; Refresh(); };
        _termChoice.ItemSelected += index => { _multiple = (int)index + 1; Changed(); };
        _capacity.TextChanged += _ => Changed();
        // 模拟可在按下到释放之间推进，付款上限仍取玩家按下时看到的报价。
        _confirm.ButtonDown += () => _pressedQuote = _quote;
        _confirm.ButtonUp += () => Callable.From(() =>
        {
            if (_quote is { } quote && GodotObject.IsInstanceValid(_confirm) && quote.Revision != _view.Revision)
            { RequestQuote(); UpdateActions(); }
        }).CallDeferred();
    }

    internal void Refresh()
    {
        _rule = _content.Core.MarketRules.SingleOrDefault(rule => rule.Id == _settlement.Market.Rule.Rule);
        var leases = _locals.Select(item => item.Lease).OfType<LeaseView>().ToArray();
        if (_selectOnOpen) { _leaseId = leases.FirstOrDefault()?.Id; _selectOnOpen = false; }
        EconomyPanels.SyncChoices(_leaseChoice, _leaseIds, leases.Select(item => item.Id), _leaseId,
            id => {
                var local = _locals.Single(item => item.Lease?.Id == id);
                return $"用量 {local.Inventory.Used}/{local.Lease!.Capacity} · {UiInput.FormatTime(local.Lease.ExpiresAtStep)}到期" +
                    (local.Lease.Expired ? " · 已到期" : "");
            }, "新租仓库");
        _empty.Visible = leases.Length == 0;
        var local = Selected;
        _details.Visible = _leaseId is not null;
        _stock.Visible = local.Lease is not null;
        _capacity.Visible = _capacityLabel.Visible = _leaseId is null;
        if (local.Lease is { } lease)
        {
            _details.Text = $"用量 {local.Inventory.Used}/{lease.Capacity} · 预留 {local.Inventory.Reserved}\n到期时间：{UiInput.FormatTime(lease.ExpiresAtStep)}\n" +
                (lease.Expired ? "已到期，只能取出；续租后可继续存入。" : "租约有效，到期后只能取出。");
            goods.RenderInventory(_stock, _stockRows, local.Inventory, _content, _settlement);
            _stockRows.Access.Visible = _stockRows.Capacity.Visible = false;
            foreach (var stack in local.Inventory.Stacks)
            {
                var row = _stockRows.Goods[stack.Good];
                row.Name.Text = $"{SettlementOverviewPage.GoodName(stack.Good, _content)} × {stack.Quantity}";
                // 图标缓存的错误提示含内部标识，仓库页不显示这些悬停提示。
                row.Icon.MouseFilter = Control.MouseFilterEnum.Ignore;
            }
        }
        else if (_leaseId is not null) _details.Text = "所选仓库已不存在，请重新选择。";
        RefreshTerms();
        if (!_confirm.IsPressed()) RequestQuote();
        UpdateActions();
    }

    private (InventoryView Inventory, LeaseView? Lease) Selected => _locals.FirstOrDefault(item => item.Lease?.Id == _leaseId && item.Lease is not null);
    private ulong Duration(int multiple) => checked((ulong)_rule!.LeaseDurationTicks * (ulong)multiple);

    private void RefreshTerms()
    {
        var count = _rule?.MaximumLeaseDurationMultiple ?? 0;
        while (_termChoice.ItemCount > count) _termChoice.RemoveItem(_termChoice.ItemCount - 1);
        while (_termChoice.ItemCount < count) _termChoice.AddItem("");
        var problems = new List<string>();
        for (var index = 0; index < count; index++)
        {
            var reason = "";
            if (_leaseId is { } id && Selected.Lease is not null)
                reason = _view.QuoteLease(new LeaseRequest.Renew(id, Duration(index + 1))) switch
                {
                    CoreResult<LeaseQuote>.Success success => Describe([.. success.Value.Blockers.Where(problem => problem.Field == "Request.DurationSteps")]),
                    CoreResult<LeaseQuote>.Failure failure => "报价被拒绝：" + Describe(failure.Problems),
                    CoreResult<LeaseQuote>.Faulted fault => CommandDiagnostics.Describe(fault.Fault),
                    _ => throw new InvalidOperationException("未知报价结果。")
                };
            var title = $"{index + 1} 倍";
            if (_termChoice.GetItemText(index) != title) _termChoice.SetItemText(index, title);
            _termChoice.SetItemDisabled(index, reason.Length > 0);
            if (reason.Length > 0) problems.Add($"{title}不可选：{reason}");
        }
        var selected = _multiple <= count ? _multiple - 1 : -1;
        if (_termChoice.Selected != selected) _termChoice.Select(selected);
        _termProblems.Text = string.Join("\n", problems);
        _termProblems.Visible = problems.Count > 0;
        _duration.Text = _rule is null ? "市场规则缺失，暂时不能租用或续租。" :
            $"本次租期：{Duration(_multiple) / (decimal)UiInput.StepsPerDay:0.#########} 天";
    }

    private bool TermAvailable => _termChoice.Selected >= 0 && !_termChoice.IsItemDisabled(_termChoice.Selected);

    private void Changed() { _result.Text = ""; RefreshTerms(); RequestQuote(); UpdateActions(); }

    private void RequestQuote()
    {
        _quote = null;
        _description.AddThemeColorOverride("font_color", UiStyle.Error);
        if (!canAct()) { _description.Text = "当前操作不可用，请重新打开聚居地。"; return; }
        if (_rule is null) { _description.Text = "市场规则缺失，暂时不能租用或续租。\n" + Describe(_settlement.Market.Rule.Problems); return; }
        if (!TermAvailable) { _description.Text = "所选租期暂不可用，请选择其他租期。"; return; }
        LeaseRequest request;
        if (_leaseId is { } id)
        {
            if (Selected.Lease is null) { _description.Text = "所选仓库已不存在，请重新选择。"; return; }
            request = new LeaseRequest.Renew(id, Duration(_multiple));
        }
        else
        {
            if (!long.TryParse(_capacity.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var capacity) || capacity <= 0)
            { _description.Text = $"请输入正整数容量。当前可租容量：{_settlement.Market.Storage.RentableCapacity}"; return; }
            request = new LeaseRequest.Rent(_settlement.Id, capacity, Duration(_multiple));
        }
        switch (_view.QuoteLease(request))
        {
            case CoreResult<LeaseQuote>.Success success: _quote = success.Value; break;
            case CoreResult<LeaseQuote>.Failure failure: _description.Text = "报价被拒绝：\n" + Describe(failure.Problems); return;
            case CoreResult<LeaseQuote>.Faulted fault: _description.Text = CommandDiagnostics.Describe(fault.Fault); return;
        }
        var quote = _quote!;
        _description.Text = $"报价不锁定\n当前可租容量：{quote.CurrentlyRentableCapacity}\n预付租金：{Amount(quote.Total)}";
        if (quote.Total is not null)
        {
            var start = Math.Max(_view.Position.CompletedSteps, Selected.Lease?.ExpiresAtStep ?? 0);
            _description.Text += $"\n预计到期时间：{UiInput.FormatTime(checked(start + Duration(_multiple)))}";
        }
        if (!quote.Blockers.IsEmpty) _description.Text += "\n" + Describe(quote.Blockers);
        _description.AddThemeColorOverride("font_color", quote.Blockers.IsEmpty ? UiStyle.Foreground : UiStyle.Error);
    }

    internal void UpdateActions()
    {
        var available = canAct() && _rule is not null;
        _leaseChoice.Disabled = !canAct(); _termChoice.Disabled = !available;
        _capacity.Editable = available;
        _confirm.Disabled = !available || !TermAvailable || _quote is not { Total: not null, Blockers.IsEmpty: true };
        _confirm.Text = (_leaseId is null ? "租用" : "续租") + " · " + Amount(_quote?.Total);
    }

    private void Confirm()
    {
        var quote = _pressedQuote ?? _quote; _pressedQuote = null;
        if (!canAct() || _confirm.Disabled || quote?.Total is not { } total) return;
        var result = submit(_target, new CoreCommand.AcquireLease(quote.Request, total));
        _result.Text = result switch
        {
            SubmissionResult.NotAccepted rejected => Describe([rejected.Reason]),
            SubmissionResult.Completed { Receipt.Decision: CommandDecision.Rejected rejected } => Describe(rejected.Problems),
            SubmissionResult.Completed { Receipt.Decision: CommandDecision.Faulted fault } => CommandDiagnostics.Describe(fault.Fault),
            SubmissionResult.Completed accepted => Acquired(accepted.Events),
            _ => "租赁未完成。"
        };
    }

    private static string Acquired(ImmutableArray<EventEnvelope> events)
    {
        var acquired = events.Select(item => item.Event).OfType<CoreEvent.LeaseAcquired>().FirstOrDefault();
        return acquired is null ? "租赁已受理。" :
            $"{(acquired.Renewal ? "已续租" : "已租用仓库")} · 实付 {acquired.ActualPayment.Value} · {UiInput.FormatTime(acquired.Lease.ExpiresAtStep)}到期";
    }

    private string Describe(ImmutableArray<CoreProblem> problems) => string.Join("\n", problems.Select(problem =>
        problem.Field == "Request.DurationSteps" && _rule is not null
            ? $"续租后剩余租期不能超过最长租期 {Duration(_rule.MaximumLeaseDurationMultiple) / (decimal)UiInput.StepsPerDay:0.#########} 天。"
            : CommandDiagnostics.Describe(problem with { Subject = null }, _content)));

    internal void Close() { _leaseIds.Clear(); _quote = _pressedQuote = null; }
}
