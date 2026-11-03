using Chickensoft.GoDotTest;
using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class AuthoringEquipmentGridTests(Node testScene) : TestClass(testScene)
{
    [Test]
    [Chickensoft.GoDotTest.Timeout(120000)]
    public async Task Equipment_form_opens_grid_and_keeps_package_identity_after_a_grid_edit()
    {
        using var fixture = new RealAuthoringFixture(TestScene);
        var source = Path.Combine(fixture.ModDirectory, "resources", "blueprints", "engine-equipment.blueprint.json");
        File.WriteAllText(source, """
            {"schema_version":3,"id":"engine-equipment","category":"large_equipment",
             "simulation":{"behaviors":{"propulsion":{"traction":12,"rated_speed":34}}},
             "layout":{"palette":[{"voxel":"engine-food"}],"cells":[[0,0,0,0]]},
             "display":{"name":"设备入口回归","description":"隔离包"}}
            """);
        var original = File.ReadAllBytes(source);
        await fixture.Start(); await fixture.Open();
        fixture.Service.OpenDefinition(new(DefinitionKind.Blueprint, "engine-equipment"), grid: false);
        var panel = new AuthoringPanel(); panel.Initialize(fixture.Service, fixture.Editor); TestScene.AddChild(panel); panel.Open();
        var input = new UiTestInput(TestScene);
        try
        {
            await fixture.Pump(() => !fixture.Service.IsBusy);
            var form = Find<AuthoringDefinitionForm>(panel, "DefinitionForm");
            var traction = form.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>()
                .Single(field => field.TooltipText == "牵引力");
            await input.Enter(traction, "42");
            await input.Activate(form.FindChildren("*", "Button", true, false).OfType<Button>().Single(button => button.Text == "编辑体素网格"));
            Require(fixture.Service.Leaving, "有修改的设备表单进入网格前必须询问保存、放弃或取消。");
            await input.Activate(Find<Button>(panel, "SaveAndLeave"));
            await fixture.Pump(() => !fixture.Service.IsBusy && !fixture.Service.Leaving && !fixture.Editor.IsBusy);
            Require(fixture.Editor.Session is not null, "设备必须能从表单进入真实网格编辑器：" + fixture.Editor.Message);
            var expected = new GoodId.Composite(new(fixture.PackageId.Value + ":engine-equipment"));
            var opened = fixture.Editor.State!.CurrentEvaluation as DesignEvaluation.Complete;
            Require(opened?.Core?.Design.Kind is DesignKind.LargeEquipment equipment && equipment.ManufacturedGood == expected
                && opened.Definition.Specification is DesignSpecification.FunctionalComposite composite
                && composite.Behaviors[0] is PartCapability.Propulsion { TractionKilonewtons: 42 },
                "首次求值必须使用保存后的表单参数及包定义的设备身份。");

            fixture.Editor.Apply(new EditOperation.Cells(new EditFootprint.Cell(new(1, 0, 0)),
                new CellEdit.Add(new(new(fixture.PackageId.Value + ":engine-food"), VoxelOrientation.PxPy, new("cube"), null))));
            await fixture.Pump(() => fixture.Editor.HasError || fixture.Editor.State?.CurrentEvaluation
                is DesignEvaluation.Complete { Definition.Layout.Cells.Length: 2 });
            Require(!fixture.Editor.HasError && fixture.Editor.State?.CurrentEvaluation is DesignEvaluation.Complete changed
                && changed.Core?.Design.Kind is DesignKind.LargeEquipment after && after.ManufacturedGood == expected,
                "修改网格后的重新求值必须保留同一包定义身份：" + fixture.Editor.Message);
            Require(original.AsSpan().SequenceEqual(File.ReadAllBytes(source)), "表单保存和网格编辑不得写入源包。");
        }
        finally { panel.QueueFree(); await input.Frames(3); }
    }
}
