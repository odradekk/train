using System.Collections.Immutable;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Ui.EconomyPanelControls;

namespace Train.Ui;

internal sealed class SettlementPanel
{
    private readonly SettlementStockPage _stock;
    private readonly SettlementMarketPage _market;
    private readonly SettlementVehiclesPage _vehicles;
    private readonly SettlementLeasesPage _leases;
    private readonly SettlementProductionPage _production;
    private readonly SettlementPopulationPage _population;
    private readonly SettlementPlotsPage _plots;
    private readonly SettlementServicesPage _services;
    private readonly Func<InventoryId?, InventoryView?> _selectLocal;
    private PanelContainer? _panel;
    private VBoxContainer _body = null!;
    private Label _state = null!;
    private Label _wallet = null!;
    private OptionButton _localChoice = null!;
    private readonly List<InventoryId?> _localOptions = [];
    private IContentView _content = null!;
    private SettlementView? _settlement;

    internal SettlementPanel(SettlementStockPage stock, SettlementMarketPage market, SettlementVehiclesPage vehicles,
        SettlementLeasesPage leases, SettlementProductionPage production, SettlementPopulationPage population,
        SettlementPlotsPage plots, SettlementServicesPage services, Func<InventoryId?, InventoryView?> selectLocal)
    {
        _stock = stock; _market = market; _vehicles = vehicles; _leases = leases; _production = production;
        _population = population; _plots = plots; _services = services; _selectLocal = selectLocal;
    }

    internal bool Visible => _panel?.Visible == true;
    internal void Bind(IContentView content, SettlementView? settlement) { _content = content; _settlement = settlement; }

    internal void Open(PanelContainer panel, VBoxContainer body)
    {
        _panel = panel; _body = body; _localOptions.Clear();
        _state = Text(body, "State", ""); _wallet = Text(body, "Wallet", "");
        _localChoice = new OptionButton { Name = "LocalInventory" };
        body.AddChild(_localChoice);
        _localChoice.ItemSelected += index =>
        {
            var local = _selectLocal(_localOptions[(int)index]);
            _stock.Render(local, _content, _settlement); _market.ClearTradeQuote(); _market.RenderMarket();
        };
        var tabs = new TabContainer { Name = "Pages", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new(0, 160) };
        body.AddChild(tabs);
        _stock.Build(Page(tabs, "库存"));
        _market.BuildTrade(Page(tabs, "市场"));
        _vehicles.BuildVehicles(Page(tabs, "现车"));
        _leases.BuildLeases(Page(tabs, "仓库租用"));
        _production.BuildProduction(Page(tabs, "生产")); _population.BuildPopulation(Page(tabs, "人口"));
        _plots.BuildPlots(Page(tabs, "地块与建筑"));
        _services.BuildServices(Page(tabs, "建造与工地"));
        tabs.TabChanged += _ => _services.RefreshServices();
    }

    internal void ReadSettlement(ICoreReadView view, IContentView content, SettlementView settlement,
        IReadOnlyList<(InventoryView Inventory, LeaseView? Lease)> locals, InventoryId? localId, InventoryView? local)
    {
        _wallet.Text = $"资金：{view.Economy.Wallet.Value}";
        EconomyPanels.SyncChoices(_localChoice, _localOptions, locals.Select(item => item.Inventory.Id), localId,
            id => locals.First(item => item.Inventory.Id == id).Lease is { } lease
                ? $"租用库存 {id.Value} · 租约 {lease.Id.Value}" : $"聚居地库存 {id.Value}");
        _stock.Render(local, content, settlement); _market.RefreshTrade(); _vehicles.RefreshVehicles(); _leases.RefreshLeases();
        _production.RefreshProduction(); _population.RefreshPopulation(); _plots.RefreshPlots();
    }

    internal void UpdateActions(ICoreReadView view, bool busy, bool canAct)
    {
        _localChoice.Disabled = !canAct;
        _market.UpdateTradeActions(); _vehicles.UpdateVehicleActions(); _leases.UpdateLeaseActions();
        _state.Text = view.Fault is { } fault ? CommandDiagnostics.Describe(fault) : busy ? "当前操作尚未结束" : "";
    }

    internal void Invalidate() => EconomyPanelControls.Invalidate(_body, _state);
    internal bool IsCurrent(SettlementId current, SettlementId requested)
        => _panel is not null && GodotObject.IsInstanceValid(_panel) && current == requested;
    internal void Show() { _panel!.Show(); UiStyle.BringToFront(_panel); }
    internal void SelectServices()
    {
        var tabs = _body.GetNode<TabContainer>("Pages");
        tabs.CurrentTab = Enumerable.Range(0, tabs.GetTabCount()).Single(index => tabs.GetTabTitle(index) == "建造与工地");
    }
    internal void RemoveWindow() => EconomyPanelControls.RemoveWindow(_panel);
    internal void Close() { RemoveWindow(); _panel = null; _localOptions.Clear(); _stock.Close(); }
}
