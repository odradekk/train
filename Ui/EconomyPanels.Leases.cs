using System.Globalization;
using Godot;
using Train.App;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class EconomyPanels
{
    private VBoxContainer _leaseRows = null!;
    private Label _storage = null!;
    private Label _leasesEmpty = null!;
    private readonly Dictionary<LeaseId, Label> _leaseItems = [];
    private OptionButton _leaseChoice = null!;
    private readonly List<LeaseId?> _leaseIds = [];
    private LeaseId? _leaseId;
    private LineEdit _leaseCapacity = null!;
    private OptionButton _leaseMultipleChoice = null!;
    private int _leaseMultiple = 1;
    private Label _leaseDuration = null!;
    private Label _leaseTermProblems = null!;
    private MarketRuleProjection? _leaseRule;
    private Label _leaseDescription = null!;
    private Button _leaseQuoteButton = null!;
    private Button _leaseConfirm = null!;
    private LeaseQuote? _leaseQuote;

    private void BuildLeases(VBoxContainer body)
    {
        _leaseId = null; _leaseQuote = null; _leaseRule = null; _leaseMultiple = 1; _leaseIds.Clear(); _leaseItems.Clear();
        _leaseRows = List(body, "Leases", 240); _storage = Text(_leaseRows, "Storage", "");
        _leasesEmpty = Text(_leaseRows, "Empty", "没有仓库租约。");
        _leaseChoice = new OptionButton { Name = "Lease" }; body.AddChild(_leaseChoice);
        Text(body, "LeaseCapacityLabel", "新租容量（续租沿用原容量）");
        _leaseCapacity = IntegerInput(body, "LeaseCapacity", "新租容量（正整数）");
        Text(body, "LeaseMultipleLabel", "本次租期倍数（续租仅追加本次租期）");
        _leaseMultipleChoice = new OptionButton { Name = "LeaseMultiple" }; body.AddChild(_leaseMultipleChoice);
        _leaseDuration = Text(body, "LeaseDuration", "");
        _leaseChoice.ItemSelected += index => { _leaseId = _leaseIds[(int)index]; RefreshLeaseTerms(); ClearLeaseQuote(); };
        _leaseMultipleChoice.ItemSelected += index => { _leaseMultiple = (int)index + 1; RefreshLeaseDuration(); ClearLeaseQuote(); };
        _leaseCapacity.TextChanged += _ => ClearLeaseQuote();
        var actions = new HBoxContainer(); body.AddChild(actions);
        _leaseQuoteButton = UiStyle.Button("QuoteLease", "获取报价", () => Quote(RequestLeaseQuote));
        _leaseConfirm = UiStyle.Button("ConfirmLease", "确认租用／续租", ConfirmLease, true);
        actions.AddChild(_leaseQuoteButton); actions.AddChild(_leaseConfirm);
        _leaseTermProblems = Text(body, "LeaseTermProblems", "");
        _leaseDescription = Text(body, "LeaseQuote", "报价不锁定。续租沿用原容量，重新检查可租空间。");
    }

    private void RefreshLeases()
    {
        var status = _settlement!.Market.Rule;
        _leaseRule = _content.Core.MarketRules.SingleOrDefault(rule => rule.Id == status.Rule);
        if (_leaseRule is null) ResetQuote(ref _leaseQuote, _leaseDescription, "市场规则缺失，不能获取或确认租赁报价。");
        var storage = _settlement!.Market.Storage;
        _storage.Text = $"全城物理容量 {storage.PhysicalCapacity} · 已用 {storage.TotalUsed} · 预留 {storage.TotalReserved}\n有效租出 {storage.ActiveLeaseCapacity} · 到期占用 {storage.ExpiredLeaseUsed}\nNPC 容量 {storage.NpcCapacity} · 可出租 {storage.RentableCapacity}";
        var leases = _locals.Where(local => local.Lease is not null).Select(local => local.Lease!).ToArray();
        foreach (var id in _leaseItems.Keys.Where(id => !leases.Any(lease => lease.Id == id)).ToArray())
        { RemoveControl(_leaseItems[id]); _leaseItems.Remove(id); }
        foreach (var lease in leases)
        {
            if (!_leaseItems.TryGetValue(lease.Id, out var label)) _leaseItems.Add(lease.Id, label = Text(_leaseRows, "LeaseDetails", ""));
            label.Text = $"租约 {lease.Id.Value} · 容量 {lease.Capacity} · 已付 {lease.Paid.Value}\n到期：{UiInput.FormatTime(lease.ExpiresAtStep)}（步 {lease.ExpiresAtStep}）\n" +
                (lease.Expired ? "已到期：只取不存，空余额度已释放" : "租约有效");
            label.AddThemeColorOverride("font_color", lease.Expired ? UiStyle.Warning : UiStyle.Success);
        }
        _leasesEmpty.Visible = leases.Length == 0;
        SyncChoices(_leaseChoice, _leaseIds, leases.Select(lease => lease.Id), _leaseId, id => $"续租：租约 {id.Value}", "新租仓库");
        RefreshLeaseTerms();
        if (_leaseId is { } selected && !_leaseItems.ContainsKey(selected)) MissingSelection(ref _leaseQuote, _leaseDescription, "租约");
    }

    private ulong LeaseDurationSteps(int multiple) => checked((ulong)_leaseRule!.LeaseDurationTicks * (ulong)multiple);

    private void RefreshLeaseDuration()
    {
        var status = _settlement!.Market.Rule;
        _leaseDuration.Text = _leaseRule is { } rule
            ? $"固定租期：{rule.LeaseDurationTicks / (decimal)UiInput.StepsPerDay:0.#########} 游戏日\n本次租期：{LeaseDurationSteps(_leaseMultiple) / (decimal)UiInput.StepsPerDay:0.#########} 游戏日（{_leaseMultiple} 倍）"
            : $"市场规则缺失：{status.Rule.Value}，无法确定租期。\n" + Problems(status.Problems);
        _leaseDuration.AddThemeColorOverride("font_color", _leaseRule is null ? UiStyle.Error : UiStyle.Foreground);
    }

    private void RefreshLeaseTerms()
    {
        var count = _leaseRule?.MaximumLeaseDurationMultiple ?? 0;
        while (_leaseMultipleChoice.ItemCount > count) _leaseMultipleChoice.RemoveItem(_leaseMultipleChoice.ItemCount - 1);
        while (_leaseMultipleChoice.ItemCount < count) _leaseMultipleChoice.AddItem("");
        var reasons = new List<(int Multiple, string Reason)>();
        for (var index = 0; index < count; index++)
        {
            var reason = "";
            if (_leaseId is { } id)
            {
                if (!_leaseItems.ContainsKey(id)) reason = "已不存在：所选租约";
                else
                {
                    var result = _view.QuoteLease(new LeaseRequest.Renew(id, LeaseDurationSteps(index + 1)));
                    reason = result switch
                    {
                        CoreResult<LeaseQuote>.Success success => Problems([.. success.Value.Blockers.Where(problem => problem.Field == "Request.DurationSteps")]),
                        CoreResult<LeaseQuote>.Failure failure => "报价被拒绝：\n" + Problems(failure.Problems),
                        CoreResult<LeaseQuote>.Faulted fault => CommandDiagnostics.Describe(fault.Fault),
                        _ => throw new InvalidOperationException("未知报价结果。")
                    };
                    if (result is CoreResult<LeaseQuote>.Success && reason.Length > 0)
                        reason = $"续租后剩余租期不能超过最长租期 {LeaseDurationSteps(count) / (decimal)UiInput.StepsPerDay:0.#########} 游戏日。" + reason;
                }
            }
            var title = $"{index + 1} 倍" + (reason.Length == 0 ? "" : "（不可选）");
            if (_leaseMultipleChoice.GetItemText(index) != title) _leaseMultipleChoice.SetItemText(index, title);
            _leaseMultipleChoice.SetItemDisabled(index, reason.Length > 0);
            if (reason.Length > 0) reasons.Add((index + 1, reason));
        }
        var selected = _leaseMultiple <= count ? _leaseMultiple - 1 : -1;
        if (_leaseMultipleChoice.Selected != selected) _leaseMultipleChoice.Select(selected);
        _leaseTermProblems.Text = string.Join("\n", reasons.GroupBy(item => item.Reason)
            .Select(group => $"{string.Join("、", group.Select(item => item.Multiple))} 倍不可选：{group.Key}"));
        _leaseTermProblems.Visible = reasons.Count > 0;
        RefreshLeaseDuration();
    }

    private bool LeaseTermAvailable => _leaseMultipleChoice.Selected >= 0 &&
        !_leaseMultipleChoice.IsItemDisabled(_leaseMultipleChoice.Selected);

    private void ClearLeaseQuote() { ResetQuote(ref _leaseQuote, _leaseDescription); UpdateLeaseActions(); }

    private void RequestLeaseQuote()
    {
        _leaseQuote = null;
        if (_leaseRule is null || !LeaseTermAvailable) return;
        var duration = LeaseDurationSteps(_leaseMultiple);
        LeaseRequest request;
        if (_leaseId is { } id)
        {
            if (!_leaseItems.ContainsKey(id)) { MissingSelection(ref _leaseQuote, _leaseDescription, "租约"); return; }
            request = new LeaseRequest.Renew(id, duration);
        }
        else
        {
            if (!long.TryParse(_leaseCapacity.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var capacity) || capacity <= 0)
            { ResetQuote(ref _leaseQuote, _leaseDescription, "新租容量须为正整数。"); return; }
            request = new LeaseRequest.Rent(_settlementId, capacity, duration);
        }
        _leaseQuote = ReadQuote(_view.QuoteLease(request), _leaseDescription);
    }

    private void ConfirmLease()
    {
        if (_leaseConfirm.Disabled || _leaseQuote is not { Total: { } total } quote) return;
        Submit(_settlementTarget!, new CoreCommand.AcquireLease(quote.Request, total), RequestLeaseQuote);
    }

    private void UpdateLeaseActions()
    {
        var present = _leaseId is null || _leaseItems.ContainsKey(_leaseId.Value);
        var available = CanAct && _leaseRule is not null;
        _leaseChoice.Disabled = !available; _leaseMultipleChoice.Disabled = !available;
        _leaseQuoteButton.Disabled = !available || !present || !LeaseTermAvailable;
        _leaseCapacity.Editable = available && _leaseId is null;
        _leaseConfirm.Disabled = !available || !present || !LeaseTermAvailable || _leaseQuote is not { Total: not null, Blockers.IsEmpty: true };
        if (_leaseQuote is { } quote) ShowQuote(_leaseDescription, quote.Revision,
            $"当前可租容量：{quote.CurrentlyRentableCapacity}\n总额：{Amount(quote.Total)}", quote.Blockers,
            reason: quote.Total is null ? "报价未提供总额，不能确认租用。" : null);
    }
}
