using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Ui.EconomyPanelControls;

namespace Train.Ui;

internal sealed partial class SettlementOverviewPage
{
    private Label _population = null!, _labor = null!, _sites = null!, _defense = null!, _nothingPending = null!;
    private VBoxContainer _attention = null!;
    private readonly Dictionary<BuildingId, Button> _blockedBuildings = [];
    private readonly Dictionary<GoodId, Button> _lowStock = [];

    private void BuildOwned(VBoxContainer body)
    {
        _population = Text(body, "PopulationSummary", "");
        _labor = Text(body, "LaborSummary", "");
        Text(body, "AttentionTitle", "需要处理");
        _attention = new VBoxContainer { Name = "Attention" }; body.AddChild(_attention);
        _nothingPending = Text(_attention, "Empty", "没有需要处理的事项。");
        Text(body, "SitesTitle", "工地"); _sites = Text(body, "OwnedSites", "");
        Text(body, "DefenseTitle", "防御"); _defense = Text(body, "DefenseSummary", "");
    }

    private void RefreshOwned(IContentView content)
    {
        var population = _settlement.Population;
        var period = population.Execution switch
        {
            PopulationExecution.InProgress active => active.Period,
            PopulationExecution.Disabled disabled => disabled.RetainedPeriod,
            _ => null
        };
        static string Ratio(double? value) => value is { } ratio ? $"{ratio:P1}" : "暂无统计";
        _population.Text = $"常住人口 {population.Residents}\n基本需求满足率 {Ratio(period?.BasicSatisfaction)}\n改善需求满足率 {Ratio(period?.ImprovementSatisfaction)}";
        if (population.Execution is PopulationExecution.Disabled) _population.Text += "\n居民消费已停用，请检查人口规则内容。";
        _labor.Text = $"劳动力：可用 {population.AvailableLabor:0.###} · 已占用 {population.AllocatedLabor:0.###} · 闲置 {population.IdleLabor:0.###}";
        var attention = SettlementAttention.Read(_view, content, _settlement);
        foreach (var id in _blockedBuildings.Keys.Except(attention.Buildings.Select(item => item.Building)).ToArray())
        { RemoveControl(_blockedBuildings[id]); _blockedBuildings.Remove(id); }
        foreach (var item in attention.Buildings)
        {
            if (!_blockedBuildings.TryGetValue(item.Building, out var button))
            {
                var id = item.Building;
                button = UiStyle.Button($"BlockedBuilding_{id.Value}", "", () => openBuilding(id));
                button.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                _attention.AddChild(button); _blockedBuildings.Add(id, button);
            }
            button.Text = item.Text;
        }
        foreach (var good in _lowStock.Keys.Except(attention.Goods.Select(item => item.Good)).ToArray())
        { RemoveControl(_lowStock[good]); _lowStock.Remove(good); }
        foreach (var item in attention.Goods)
        {
            if (!_lowStock.TryGetValue(item.Good, out var button))
            {
                var good = item.Good;
                button = UiStyle.Button("LowStock", "", () => openGood(good));
                button.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                _attention.AddChild(button); _lowStock.Add(good, button);
            }
            button.Text = item.Text;
        }
        _nothingPending.Visible = attention.Buildings.Count == 0 && attention.Goods.Count == 0;
        _sites.Text = Join(attention.Sites, "没有进行中的工地。");
        var armedBuildings = 0; var weapons = 0; var incomplete = false;
        foreach (var building in _settlement.Plots.Select(plot => plot.Occupant).OfType<PlotOccupant.Building>())
        {
            if (_view.FindInstance(new InstanceId.Building(building.Id)) is not Lookup<InstanceView>.Found instance ||
                content.FindDesign(instance.Value.Design) is not ContentLookup<DesignEvaluation>.Found { Value: DesignEvaluation.Complete { Core: { } input } })
            { incomplete = true; continue; }
            var armedParts = input.Design.Parts.Where(part => part.Capabilities.Any(capability => capability is PartCapability.Weapon)).ToArray();
            if (armedParts.Length > 0) armedBuildings++;
            weapons += armedParts.Where(part => !part.Missing && instance.Value.Parts.Any(damage => damage.Part == part.Id &&
                !damage.DefinitionMissing && !damage.Destroyed && damage.Durability.EffectiveCurrent > 0))
                .Sum(part => part.Capabilities.Count(capability => capability is PartCapability.Weapon));
        }
        _defense.Text = armedBuildings == 0 ? "没有防御建筑。" : $"防御建筑 {armedBuildings} 座 · 可用武器 {weapons} 件";
        if (incomplete) _defense.Text += "\n部分建筑内容缺失，无法确认防御。";
        UpdateActions();
    }
}
