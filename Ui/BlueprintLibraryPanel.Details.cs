using System.Collections.Immutable;
using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class BlueprintLibraryPanel
{
    private Label? _vehicleCount;
    private DesignHash? _countDesign;
    private ICoreReadView? _countView;

    private void ShowDetails(LibraryItem? item)
    {
        Clear(_details); Clear(_actions); _vehicleCount = null; _countView = null; _countDesign = null;
        if (item is null) { Text(_details, "选择一份设计查看详情。"); return; }
        _details.AddChild(Thumbnail(item, 160));
        Text(_details, item.Name).ThemeTypeVariation = "Title";
        Text(_details, item.Draft is { } draft ? DraftDescription(draft) : Groups[_group]);
        if (item.Design is { } design)
        {
            if (design.Display.Description.Length != 0) Text(_details, design.Display.Description);
            Text(_details, design.Specification switch
            {
                DesignSpecification.Vehicle vehicle => "车辆 · " + (vehicle.Size is { } size ? EconomyPanels.SizeName(size) : "尺寸尚未选择"),
                DesignSpecification.Building building => "建筑 · " + (building.PlotSize is { } plot ? EconomyPanels.PlotSizeName(plot) : "地块尚未选择"),
                _ => "大型设备"
            });
        }
        Text(_details, "属性").ThemeTypeVariation = "Title";
        switch (item.Evaluation)
        {
            case DesignEvaluation.Complete complete when complete.Core?.Design is { } projection:
                Text(_details, $"质量：{projection.MassTons:0.###} 吨\n结构耐久：{projection.MaxStructureDurability}\n基本货币价值：{projection.StructuralBaseValue.Value}");
                var capabilities = projection.Parts.SelectMany(part => part.Capabilities).ToArray();
                Text(_details, $"载量：{capabilities.OfType<PartCapability.Storage>().Sum(value => value.Capacity)}");
                if (capabilities.OfType<PartCapability.Propulsion>().Any())
                    Text(_details, $"牵引力：{capabilities.OfType<PartCapability.Propulsion>().Sum(value => value.TractionKilonewtons):0.##} 千牛");
                Materials(projection.BillOfMaterials);
                break;
            case DesignEvaluation.Partial partial:
                Text(_details, "部分内容缺失，属性尚不完整。");
                Text(_details, "质量：" + (partial.Metrics.MassTons is ComputedValue<double>.Known mass ? $"{mass.Value:0.###} 吨" : "未知"));
                if (partial.Metrics.BillOfMaterials is ComputedValue<ImmutableArray<GoodsQuantity>>.Known materials) Materials(materials.Value);
                else Text(_details, "制造材料未知，部分内容缺失。");
                break;
            default:
                Text(_details, item.Evaluation is null && item.Draft is not null ? "正在评估草稿…" : "设计属性与制造材料暂不可用。");
                break;
        }
        if (item.Blueprint is { } blueprint)
        {
            if (item.Design?.Specification is DesignSpecification.Vehicle)
            {
                _countDesign = blueprint.Design; _vehicleCount = Text(_details, ""); UpdateVehicleCount();
                Text(_details, "修改蓝图不会影响已建成的车辆。");
            }
            if (blueprint.Address.Library == BlueprintLibrary.Packages)
            {
                Text(_details, "内容包里的蓝图只读。复制一份后再编辑。");
                Action("CopyLocalBlueprint", "复制到本局蓝图库并编辑", () => Copy(item, BlueprintLibrary.Local), true);
            }
            else
            {
                Action("OpenBlueprint", "编辑", () => Edit(() => _service.OpenBlueprint(blueprint.Address, fromLibrary: true)), true);
                if (blueprint.Address.Library == BlueprintLibrary.User)
                    Action("CopyLocalBlueprint", "复制到本局蓝图库并编辑", () => Copy(item, BlueprintLibrary.Local));
                Action("DeleteBlueprint", "删除…", () => Confirm("删除蓝图", $"删除「{item.Name}」？已建成的车辆不受影响。", "删除", () => _service.DeleteBlueprint(blueprint.Address)));
            }
            if (blueprint.Address.Library != BlueprintLibrary.User)
                Action("CopyUserBlueprint", "复制到用户内容库并编辑", () => Copy(item, BlueprintLibrary.User));
        }
        else if (item.Draft is { } saved)
        {
            Action("OpenDraft", "继续编辑", () => Edit(() => _service.OpenDraft(saved, fromLibrary: true)), true);
            Action("DiscardDraft", "丢弃…", () => Confirm("丢弃草稿", $"丢弃「{item.Name}」？草稿里的修改会丢失。", "丢弃", () => _service.DeleteDraft(saved)));
        }
    }

    private void Materials(ImmutableArray<GoodsQuantity> materials)
    {
        Text(_details, "制造材料").ThemeTypeVariation = "Title";
        if (materials.IsEmpty) Text(_details, "无需制造材料。");
        foreach (var material in materials)
        {
            var name = _observedContent?.FindGood(material.Good) is ContentLookup<GoodPresentation>.Found found
                ? found.Value.Display.Text.Name : "名称不可用的货物";
            Text(_details, $"{name} × {material.Quantity}");
        }
    }

    private void UpdateVehicleCount()
    {
        if (_vehicleCount is null || _service.Host.GetStatus()?.View is not { } view || ReferenceEquals(view, _countView)) return;
        _countView = view;
        var locations = view.World.Nodes.Select(node => (WorldLocation)new WorldLocation.Node(node.Id))
            .Concat(view.World.Edges.Select(edge => new WorldLocation.Edge(edge.Id, 0)));
        var ids = locations.Select(view.FindLocation).OfType<Lookup<LocationView>.Found>()
            .SelectMany(location => location.Value.Instances).OfType<InstanceId.Vehicle>().Distinct();
        var count = ids.Select(view.FindInstance).OfType<Lookup<InstanceView>.Found>()
            .Count(instance => instance.Value.Ownership == SettlementRelation.PlayerOwned && instance.Value.Design == _countDesign);
        _vehicleCount.Text = $"本局有 {count} 辆车使用这个设计。";
    }

    private string DraftDescription(SavedContentDraft draft)
    {
        if (draft.Target is DraftTarget.Instance { Id: var id })
        {
            if (_service.Host.GetStatus()?.View is not { } view || view.FindInstance(id) is not Lookup<InstanceView>.Found instance)
                return "改装：原车辆或建筑已不存在。";
            if (id is InstanceId.Vehicle vehicle && instance.Value.Details is InstanceDetails.Vehicle { Train: { } train } &&
                view.FindTrain(train) is Lookup<TrainView>.Found found)
                return $"改装：列车 {train.Value} 的第 {found.Value.Vehicles.IndexOf(vehicle.Id) + 1} 辆车。";
            return id is InstanceId.Vehicle ? "改装：停放车辆。" : "改装：建筑。";
        }
        var user = draft.Target is DraftTarget.UserBlueprint;
        var idValue = draft.Target switch { DraftTarget.LocalBlueprint local => local.Id, DraftTarget.UserBlueprint library => library.Id, _ => null };
        var blueprint = _observedContent?.Blueprints.FirstOrDefault(value => value.Address.Library == (user ? BlueprintLibrary.User : BlueprintLibrary.Local) && value.Address.Id == idValue);
        return blueprint is null ? $"新设计：{(user ? "用户内容库" : "本局蓝图库")}的草稿。" : $"编辑：{blueprint.Display.Name} 的草稿。";
    }

    private void Copy(LibraryItem item, BlueprintLibrary destination)
    {
        if (item.Design is not { } design) { _service.Report("蓝图已不可用。"); return; }
        Edit(() => _service.Open(design, destination == BlueprintLibrary.Local ? new DraftTarget.LocalBlueprint(null) : new DraftTarget.UserBlueprint(null),
            new BlueprintSaveTarget.New(destination), true, fromLibrary: true));
    }
    private void Edit(System.Action open) { open(); if (_service.IsOpen) Hide(); }
    private void Action(string name, string text, System.Action action, bool primary = false) => _actions.AddChild(UiStyle.Button(name, text, action, primary));
    private void Confirm(string title, string message, string verb, System.Action confirm)
    {
        var dialog = new ConfirmationDialog { Title = title, DialogText = message, DialogAutowrap = true, OkButtonText = verb, CancelButtonText = "取消" };
        AddChild(dialog); dialog.Confirmed += () => { confirm(); dialog.QueueFree(); }; dialog.Canceled += dialog.QueueFree;
        dialog.PopupCenteredClamped(new(560, 180));
    }
}
