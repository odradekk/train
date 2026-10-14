using System.Globalization;
using Godot;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class EconomyPanels
{
    private VBoxContainer _leaseRows = null!;
    private OptionButton _leaseChoice = null!;
    private readonly List<LeaseId> _leaseIds = [];
    private LeaseId? _leaseId;
    private LineEdit _leaseCapacity = null!;
    private LineEdit _leaseSeconds = null!;
    private Label _leaseDescription = null!;
    private Button _leaseQuoteButton = null!;
    private Button _leaseConfirm = null!;
    private LeaseQuote? _leaseQuote;

    private void BuildLeases(VBoxContainer body)
    {
        _leaseId = null; _leaseQuote = null;
        _leaseRows = List(body, "Leases", 240);
        _leaseChoice = new OptionButton { Name = "Lease" }; body.AddChild(_leaseChoice);
        Text(body, "LeaseCapacityLabel", "新租容量（续租沿用原容量）");
        _leaseCapacity = IntegerInput(body, "LeaseCapacity", "新租容量（正整数）");
        Text(body, "LeaseSecondsLabel", "租用时长（模拟秒，正整数）");
        _leaseSeconds = IntegerInput(body, "LeaseSeconds", "租用时长（模拟秒，正整数）", "3600");
        _leaseDescription = Text(body, "LeaseQuote", "报价不锁定。续租沿用原容量，重新检查可租空间。");
        _leaseChoice.ItemSelected += index =>
        { _leaseId = index > 0 && index <= _leaseIds.Count ? _leaseIds[(int)index - 1] : null; ClearLeaseQuote(); };
        _leaseCapacity.TextChanged += _ => ClearLeaseQuote(); _leaseSeconds.TextChanged += _ => ClearLeaseQuote();
        var actions = new HBoxContainer(); body.AddChild(actions);
        _leaseQuoteButton = UiStyle.Button("QuoteLease", "获取报价", QuoteLease);
        _leaseConfirm = UiStyle.Button("ConfirmLease", "确认租用／续租", ConfirmLease, true);
        actions.AddChild(_leaseQuoteButton); actions.AddChild(_leaseConfirm);
    }

    private void RefreshLeases()
    {
        Clear(_leaseRows); _leaseIds.Clear(); _leaseChoice.Clear(); _leaseChoice.AddItem("新租仓库");
        var storage = _settlement!.Market.Storage;
        Text(_leaseRows, "Storage", $"全城物理容量 {storage.PhysicalCapacity} · 已用 {storage.TotalUsed} · 预留 {storage.TotalReserved}\n有效租出 {storage.ActiveLeaseCapacity} · 到期占用 {storage.ExpiredLeaseUsed}\nNPC 容量 {storage.NpcCapacity} · 可出租 {storage.RentableCapacity}");
        foreach (var local in _locals)
        {
            if (local.Lease is not { } lease) continue;
            _leaseIds.Add(lease.Id); _leaseChoice.AddItem($"续租：租约 {lease.Id.Value}");
            var label = Text(_leaseRows, "LeaseDetails", $"租约 {lease.Id.Value} · 容量 {lease.Capacity} · 已付 {lease.Paid.Value}\n到期：{UiInput.FormatTime(lease.ExpiresAtStep)}（步 {lease.ExpiresAtStep}）\n" +
                (lease.Expired ? "已到期：只取不存，空余额度已释放" : "租约有效"));
            label.AddThemeColorOverride("font_color", lease.Expired ? UiStyle.Warning : UiStyle.Success);
        }
        if (_leaseIds.Count == 0) Text(_leaseRows, "Empty", "没有仓库租约。");
        var selected = _leaseId is { } id ? _leaseIds.IndexOf(id) + 1 : 0;
        if (_leaseId is not null && selected == 0)
        { _leaseChoice.AddItem("已不存在：所选租约"); selected = _leaseIds.Count + 1; }
        _leaseChoice.Select(selected);
        if (_leaseQuote is not null) RequestLeaseQuote();
    }

    private void ClearLeaseQuote()
    { _leaseQuote = null; _leaseDescription.Text = "报价不锁定。输入已改变，请获取报价。"; UpdateLeaseActions(); }

    private void QuoteLease()
    {
        if (!CanAct) return;
        RequestLeaseQuote();
        if (_queries.MissingEntry is { } entry) Unavailable(_settlementBody, _state, entry);
        else UpdateLeaseActions();
    }

    private void RequestLeaseQuote()
    {
        _leaseQuote = null;
        if (!ulong.TryParse(_leaseSeconds.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            seconds == 0 || seconds > ulong.MaxValue / (ulong)SimulationTime.StepsPerSecond)
        { _leaseDescription.Text = "租用时长须为可表示的正整数模拟秒。"; return; }
        var duration = seconds * (ulong)SimulationTime.StepsPerSecond;
        LeaseRequest request;
        if (_leaseId is { } id)
        {
            if (!_leaseIds.Contains(id)) { _leaseDescription.Text = "已不存在：所选租约"; return; }
            request = new LeaseRequest.Renew(id, duration);
        }
        else
        {
            if (!long.TryParse(_leaseCapacity.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var capacity) || capacity <= 0)
            { _leaseDescription.Text = "新租容量须为正整数。"; return; }
            request = new LeaseRequest.Rent(_settlementId, capacity, duration);
        }
        if (!_queries.TryRead(nameof(ICoreReadView.QuoteLease), () => _view.QuoteLease(request), out var result)) return;
        _leaseQuote = QuoteValue(result, _leaseDescription);
        if (_leaseQuote is not { } quote) return;
        _leaseDescription.Text = $"报价不锁定\n当前可租容量：{quote.CurrentlyRentableCapacity}\n总额：{Amount(quote.Total)}" +
            (quote.Blockers.IsEmpty ? "" : "\n受阻：\n" + Problems(quote.Blockers));
        _leaseDescription.AddThemeColorOverride("font_color", quote.Blockers.IsEmpty ? Colors.White : UiStyle.Error);
    }

    private void ConfirmLease()
    {
        if (_leaseConfirm.Disabled || _leaseQuote is not { Total: { } total } quote) return;
        Submit(_settlementTarget! with { Lease = _leaseId }, new CoreCommand.AcquireLease(quote.Request, total));
    }

    private void UpdateLeaseActions()
    {
        _leaseChoice.Disabled = _leaseQuoteButton.Disabled = !CanAct;
        _leaseCapacity.Editable = CanAct && _leaseId is null; _leaseSeconds.Editable = CanAct;
        _leaseConfirm.Disabled = !CanAct || _leaseQuote is not { Total: not null, Blockers.IsEmpty: true };
    }
}
