using System.Globalization;
using Godot;
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
    private LineEdit _leaseSeconds = null!;
    private Label _leaseDescription = null!;
    private Button _leaseQuoteButton = null!;
    private Button _leaseConfirm = null!;
    private LeaseQuote? _leaseQuote;

    private void BuildLeases(VBoxContainer body)
    {
        _leaseId = null; _leaseQuote = null; _leaseIds.Clear(); _leaseItems.Clear();
        _leaseRows = List(body, "Leases", 240); _storage = Text(_leaseRows, "Storage", "");
        _leasesEmpty = Text(_leaseRows, "Empty", "没有仓库租约。");
        _leaseChoice = new OptionButton { Name = "Lease" }; body.AddChild(_leaseChoice);
        Text(body, "LeaseCapacityLabel", "新租容量（续租沿用原容量）");
        _leaseCapacity = IntegerInput(body, "LeaseCapacity", "新租容量（正整数）");
        Text(body, "LeaseSecondsLabel", "租用时长（模拟秒，正整数）");
        _leaseSeconds = IntegerInput(body, "LeaseSeconds", "租用时长（模拟秒，正整数）", "3600");
        _leaseDescription = Text(body, "LeaseQuote", "报价不锁定。续租沿用原容量，重新检查可租空间。");
        _leaseChoice.ItemSelected += index => { _leaseId = _leaseIds[(int)index]; ClearLeaseQuote(); };
        _leaseCapacity.TextChanged += _ => ClearLeaseQuote(); _leaseSeconds.TextChanged += _ => ClearLeaseQuote();
        var actions = new HBoxContainer(); body.AddChild(actions);
        _leaseQuoteButton = UiStyle.Button("QuoteLease", "获取报价", () => Quote(RequestLeaseQuote));
        _leaseConfirm = UiStyle.Button("ConfirmLease", "确认租用／续租", ConfirmLease, true);
        actions.AddChild(_leaseQuoteButton); actions.AddChild(_leaseConfirm);
    }

    private void RefreshLeases()
    {
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
        if (_leaseId is { } selected && !_leaseItems.ContainsKey(selected)) MissingSelection(ref _leaseQuote, _leaseDescription, "租约");
    }

    private void ClearLeaseQuote() { ResetQuote(ref _leaseQuote, _leaseDescription); UpdateLeaseActions(); }

    private void RequestLeaseQuote()
    {
        _leaseQuote = null;
        if (!ulong.TryParse(_leaseSeconds.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            seconds == 0 || seconds > ulong.MaxValue / (ulong)SimulationTime.StepsPerSecond)
        { ResetQuote(ref _leaseQuote, _leaseDescription, "租用时长须为可表示的正整数模拟秒。"); return; }
        var duration = seconds * (ulong)SimulationTime.StepsPerSecond;
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
        _leaseQuote = ReadQuote(nameof(ICoreReadView.QuoteLease), () => _view.QuoteLease(request), _leaseDescription);
    }

    private void ConfirmLease()
    {
        if (_leaseConfirm.Disabled || _leaseQuote is not { Total: { } total } quote) return;
        Submit(_settlementTarget!, new CoreCommand.AcquireLease(quote.Request, total), RequestLeaseQuote);
    }

    private void UpdateLeaseActions()
    {
        var present = _leaseId is null || _leaseItems.ContainsKey(_leaseId.Value);
        _leaseChoice.Disabled = !CanAct; _leaseQuoteButton.Disabled = !CanAct || !present;
        _leaseCapacity.Editable = CanAct && _leaseId is null; _leaseSeconds.Editable = CanAct;
        _leaseConfirm.Disabled = !CanAct || !present || _leaseQuote is not { Total: not null, Blockers.IsEmpty: true };
        if (_leaseQuote is { } quote) ShowQuote(_leaseDescription, quote.Revision,
            $"当前可租容量：{quote.CurrentlyRentableCapacity}\n总额：{Amount(quote.Total)}", quote.Blockers,
            reason: quote.Total is null ? "报价未提供总额，不能确认租用。" : null);
    }
}
