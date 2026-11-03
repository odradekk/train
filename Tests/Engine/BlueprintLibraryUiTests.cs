using Chickensoft.GoDotTest;
using Godot;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class BlueprintLibraryUiTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task Library_groups_search_and_type_filters_select_the_visible_designs()
    {
        var fixture = await Ready();
        try
        {
            var library = Library(fixture);
            Require(new[] { "本局蓝图库", "用户内容库", "内容包", "草稿" }.All(text =>
                library.FindChildren("*", "Button", true, false).OfType<Button>().Any(button => button.Text.StartsWith(text))),
                "左栏必须提供三个库和草稿分组。");
            Require(Shown(library, "要造车或改装，到非敌对聚居地委托"), "库中应说明建造和改装的入口。");
            Require(Cards(library).SequenceEqual(new[] { "本局货车", "本局仓库" }), "默认应显示本局蓝图库。");
            var input = new UiTestInput(TestScene);
            await input.Activate(Button(library, "车辆", exact: true));
            Require(Cards(library).SequenceEqual(new[] { "本局货车" }), "车辆筛选必须排除建筑。");
            await input.Enter(Find<LineEdit>(library, "LibrarySearch"), "仓库");
            Require(Cards(library).Length == 0 && Shown(library, "没有符合条件的设计"), "搜索和类型筛选必须同时生效。");
            await input.Activate(Button(library, "建筑", exact: true));
            Require(Cards(library).SequenceEqual(new[] { "本局仓库" }), "切换类型后应按保留的搜索显示建筑。");
            Require(!Shown(library, "辆车使用这个设计"), "建筑不能显示车辆数。");
            await input.Activate(Button(library, "内容包"));
            await input.Activate(Button(library, "全部", exact: true));
            await input.Enter(Find<LineEdit>(library, "LibrarySearch"), "");
            await input.PressKey(Key.Backspace);
            Require(Cards(library).SequenceEqual(new[] { "内容包机车", "内容包设备" }), "内容包组只能显示内容包设计。");
            Require(Button(library, "复制到本局蓝图库并编辑", exact: true).IsVisibleInTree() &&
                !library.FindChildren("*", "Button", true, false).OfType<Button>().Any(button =>
                    button.IsVisibleInTree() && button.Text is "编辑" or "删除…"), "内容包不能提供直接编辑和删除。");
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }

    [Test]
    public async Task Package_functional_composite_explains_why_neither_library_can_copy_it()
    {
        var fixture = await Ready();
        try
        {
            var library = Library(fixture);
            var input = new UiTestInput(TestScene);
            await input.Activate(Button(library, "内容包"));
            await input.Activate(library.FindChildren("DesignCard", "Button", true, false).OfType<Button>()
                .Single(card => card.TooltipText == "内容包设备"));
            Require(!library.FindChildren("*", "Button", true, false).OfType<Button>().Any(button =>
                button.IsVisibleInTree() && button.Text is "复制到本局蓝图库并编辑" or "复制到用户内容库并编辑"),
                "根功能复合体不能提供本局或用户库的复制编辑入口。");
            Require(Shown(library, "内容包里的蓝图只读") && Shown(library, "功能复合体") &&
                Shown(library, "整件装到其他设计") && Shown(library, "不能复制编辑") && !Shown(library, "复制一份后再编辑"),
                "详情必须说明功能复合体只能整件用于其他设计，不能暗示它可以复制编辑。");
            await input.Activate(library.FindChildren("DesignCard", "Button", true, false).OfType<Button>()
                .Single(card => card.TooltipText == "内容包机车"));
            Require(Button(library, "复制到本局蓝图库并编辑", exact: true).IsVisibleInTree() &&
                Button(library, "复制到用户内容库并编辑", exact: true).IsVisibleInTree(),
                "切回普通内容包蓝图后必须恢复两个复制入口。");
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }

    [Test]
    public async Task Selected_vehicle_details_show_materials_and_zero_usage_without_internal_names()
    {
        var fixture = await Ready();
        try
        {
            var library = Library(fixture);
            Require(Shown(library, "质量：12.5 吨") && Shown(library, "结构金属 × 3"), "选中蓝图应显示属性与制造材料。");
            Require(Shown(library, "0 辆车使用这个设计") && Shown(library, "修改蓝图不会影响已建成的车辆"), "没有自有车辆时也应说明数量及蓝图修改的影响。");
            Require(!Shown(library, "fixture:steel") && !Shown(library, "local-wagon"), "蓝图库不能显示货物标识或设计哈希。");
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }

    [Test]
    public async Task Package_copy_opens_an_unsaved_local_draft_and_exit_returns_only_library_edits()
    {
        var fixture = await Ready();
        try
        {
            var library = Library(fixture);
            var input = new UiTestInput(TestScene);
            Require(library.FindChildren("*", "Button", true, false).OfType<Button>().Any(button => button.Text.StartsWith("内容包")),
                "内容包必须有自己的分组入口。");
            await input.Activate(Button(library, "内容包"));
            await input.Activate(Button(library, "复制到本局蓝图库并编辑", exact: true));
            await FramesUntil(TestScene, () => fixture.Editor.Session is not null);
            Require(fixture.Editor.Target is DraftTarget.LocalBlueprint { Id: null } && fixture.Editor.IsDirty &&
                fixture.Editor.SaveTarget is BlueprintSaveTarget.New { Library: BlueprintLibrary.Local }, "复制必须打开尚未发布的本局副本。");
            var panel = fixture.App.FindChildren("*", "", true, false).OfType<DesignEditorPanel>().Single();
            await input.Activate(Find<Button>(panel, "ExitEditor"));
            await FramesUntil(TestScene, () => Find<Button>(panel, "CancelLeave").IsVisibleInTree());
            await input.Activate(Find<Button>(panel, "CancelLeave"));
            Require(fixture.Editor.IsOpen && !library.IsVisibleInTree(), "取消退出必须留在编辑器。");
            await input.Activate(Find<Button>(panel, "ExitEditor"));
            await FramesUntil(TestScene, () => Find<Button>(panel, "DiscardAndLeave").IsVisibleInTree());
            await input.Activate(Find<Button>(panel, "DiscardAndLeave"));
            await FramesUntil(TestScene, () => library.IsVisibleInTree() && !fixture.Editor.IsOpen);
            library.Hide();
            fixture.Editor.Open(EditorPreviewSession.Draft, new DraftTarget.UserBlueprint(null));
            await FramesUntil(TestScene, () => fixture.Editor.Session is not null && panel.IsVisibleInTree());
            await input.Activate(Find<Button>(panel, "ExitEditor"));
            await input.Frames(3);
            Require(!fixture.Editor.IsOpen && !library.IsVisibleInTree(), "其他入口打开的编辑不能被改成退出回库。");
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }

    [Test]
    public async Task New_design_fields_follow_type_and_facility_choices_build_the_draft()
    {
        var fixture = await Ready();
        try
        {
            var library = Library(fixture);
            library.NewDraft();
            var dialog = library.GetChildren().OfType<AcceptDialog>().Single();
            Require(dialog.FindChildren("*", "Label", true, false).OfType<Label>().Any(label => label.Text == "名称"),
                "新建表单的字段必须带标签。");
            var input = new UiTestInput(TestScene);
            await FramesUntil(TestScene, () => Find<Label>(dialog, "EnvelopeLimit").Text.Contains("长 52"));
            var viewport = fixture.GetViewport().GetVisibleRect();
            Require(viewport.Encloses(new Rect2(dialog.Position, dialog.Size)), "新建弹窗和创建按钮必须完整留在窗口内。");
            await input.Choose(Find<OptionButton>(dialog, "DesignKind"), 1);
            Require(!Find<Control>(dialog, "VehicleOptions").Visible && Find<Control>(dialog, "BuildingOptions").Visible,
                "建筑表单不能继续显示车辆档位。");
            await input.Activate(Find<CheckBox>(dialog, "FacilityAgriculture"), dialog);
            await input.Activate(Find<CheckBox>(dialog, "FacilityRecovery"), dialog);
            await input.Enter(Find<LineEdit>(dialog, "DesignName"), "测试农场");
            await input.Choose(Find<OptionButton>(dialog, "SaveDestination"), 1);
            await input.Activate(dialog.GetOkButton(), dialog);
            await FramesUntil(TestScene, () => fixture.Editor.State is not null);
            var draft = fixture.Editor.State!.Snapshot.Draft;
            Require(draft.Display.Name == "测试农场" && fixture.Editor.Target is DraftTarget.UserBlueprint &&
                draft.Specification is DesignSpecification.Building { FacilityTags: var tags } &&
                tags.SequenceEqual(new[] { FacilityTag.Agriculture, FacilityTag.Recovery }), "创建必须只采用实际选择的设施和保存位置。");
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }

    private async Task<EditorPreview> Ready(ICoreReadView? view = null,
        System.Collections.Immutable.ImmutableArray<SavedContentDraft> drafts = default,
        System.Action<ScriptedContentFactory>? configure = null)
    {
        var entries = new[]
        {
            (BlueprintLibrary.Local, "local-wagon", "本局货车", (DesignSpecification)new DesignSpecification.Vehicle(SizeClass.Small)),
            (BlueprintLibrary.Local, "local-warehouse", "本局仓库", (DesignSpecification)new DesignSpecification.Building(PlotSize.Small, BuildingKind.Warehouse, [])),
            (BlueprintLibrary.User, "user-equipment", "用户设备", (DesignSpecification)new DesignSpecification.Assembly(null)),
            (BlueprintLibrary.Packages, "pack-locomotive", "内容包机车", (DesignSpecification)new DesignSpecification.Vehicle(SizeClass.Medium)),
            (BlueprintLibrary.Packages, "pack-equipment", "内容包设备", (DesignSpecification)new DesignSpecification.FunctionalComposite([new PartCapability.Power(5)], null))
        };
        var records = entries.Select(entry =>
        {
            var address = new BlueprintAddress(entry.Item1, new(entry.Item2));
            var draft = EditorPreviewSession.Draft with { Display = new(entry.Item3, "设计说明"), Specification = entry.Item4 };
            var evaluation = (DesignEvaluation.Complete)EditorPreviewSession.Evaluate(draft);
            var projection = new DesignProjection(new(entry.Item2), new("fixture-composition"), new DesignKind.Vehicle(SizeClass.Small, 1),
                [], 20, 20, 12.5, new(50), [], [new(new GoodId.Voxel(new("fixture:steel")), 3)], 3, [], true, []);
            return new BlueprintRecord(address, draft, [], evaluation with { Design = new(entry.Item2), Core = new(HostCoreScenario.Input, projection, []) });
        }).ToArray();
        var content = new FixedContentView(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1))
        {
            Blueprints = [.. records.Select(record => new BlueprintSummary(record.Address, record.Definition.Display,
                ((DesignEvaluation.Complete)record.Evaluation).Design, true, []))],
            BlueprintRecords = records.ToDictionary(record => record.Address, record => (ContentLookup<BlueprintRecord>)new ContentLookup<BlueprintRecord>.Found(record)),
            Designs = records.ToDictionary(record => ((DesignEvaluation.Complete)record.Evaluation).Design,
                record => (ContentLookup<DesignEvaluation>)new ContentLookup<DesignEvaluation>.Found(record.Evaluation)),
            LocalDrafts = drafts.IsDefault ? [] : drafts, Voxels = [], GenerationProfiles = [],
            Goods = new Dictionary<GoodId, ContentLookup<GoodPresentation>>
            {
                [new GoodId.Voxel(new("fixture:steel"))] = new ContentLookup<GoodPresentation>.Found(new(new GoodId.Voxel(new("fixture:steel")), new(new("结构金属", ""), null, null, false), null, false))
            }
        };
        var fixture = new EditorPreview { Name = "BlueprintLibraryFixture", Content = content, Core = view };
        TestScene.AddChild(fixture);
        await FramesUntil(TestScene, () => fixture.Editor.Session is not null);
        fixture.Editor.RequestClose(() => { });
        ((ScriptedContentFactory)fixture.App.Host!.ContentFactory).EvaluateDesignCall = (_, draft, _) =>
        {
            var evaluation = (DesignEvaluation.Complete)EditorPreviewSession.Evaluate(draft);
            return new ContentResult<DesignEvaluation>.Success(evaluation with
            { Scene = evaluation.Scene with { AllowedEnvelope = new(new(-3, 0, -26), new(3, 8, 25)) } });
        };
        configure?.Invoke((ScriptedContentFactory)fixture.App.Host.ContentFactory);
        Library(fixture).Open();
        await new UiTestInput(TestScene).Frames(3);
        return fixture;
    }

    private static BlueprintLibraryPanel Library(EditorPreview fixture) =>
        fixture.App.FindChildren("*", "", true, false).OfType<BlueprintLibraryPanel>().Single();
    private static Button Button(Node root, string text, bool exact = false) => root.FindChildren("*", "Button", true, false)
        .OfType<Button>().Single(button => button.IsVisibleInTree() && (exact ? button.Text == text : button.Text.StartsWith(text)));
    private static string[] Cards(Node root) => root.FindChildren("CardName", "Label", true, false).OfType<Label>()
        .Where(label => label.IsVisibleInTree()).Select(label => label.Text).ToArray();
    private static bool Shown(Node root, string text) => root.FindChildren("*", "Label", true, false).OfType<Label>()
        .Any(label => label.IsVisibleInTree() && label.Text.Contains(text));
}
