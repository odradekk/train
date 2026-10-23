using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Ui;

internal partial class DesignEditorPanel
{
    private void RefreshEquipment()
    {
        foreach (var child in _equipment.GetChildren()) { _equipment.RemoveChild(child); child.QueueFree(); }
        _equipment.AddChild(UiStyle.Label("大型设备蓝图库", true));
        if (_service.FixedContent is not { } content) return;
        foreach (var summary in content.Blueprints)
        {
            if (content.FindBlueprint(summary.Address) is not ContentLookup<BlueprintRecord>.Found found) continue;
            var draft = found.Value.Definition;
            if (draft.Specification is not DesignSpecification.Assembly && draft.Specification is not DesignSpecification.FunctionalComposite) continue;
            var composite = (found.Value.Evaluation as DesignEvaluation.Complete)?.Core?.Design.Kind is DesignKind.LargeEquipment { ManufacturedGood: GoodId.Composite good } ? good.Definition : (CompositeDefinitionId?)null;
            if (draft.Specification is DesignSpecification.FunctionalComposite && composite is null)
            {
                _equipment.AddChild(UiStyle.Label($"{summary.Display.Name}：功能复合体定义标识未知。"));
                continue;
            }
            NestedDesignItem Item() => composite is { } definition
                ? new NestedDesignItem.FunctionalCompositeReference(At(), VoxelOrientation.PxPy, definition, Paint())
                : new NestedDesignItem.AssemblyCopy(At(), VoxelOrientation.PxPy, draft);
            Add(_equipment, "PlaceEquipment", $"放置 {EconomyPanels.ServiceLibrary(summary.Address.Library)} · {summary.Display.Name}", () => Preview(new EditOperation.PlaceNested(Item())));
            Add(_equipment, "ReplaceEquipment", $"替换为 {summary.Display.Name}", () =>
            {
                if (_elements.GetSelectedItems().Length == 0) { _service.Report("请先选择设备。"); return; }
                Preview(new EditOperation.ReplaceNested(_elementIds[_elements.GetSelectedItems()[0]], Item()));
            });
        }
    }
}
