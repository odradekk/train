using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App.Authoring;
using Train.App.Editing;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class AuthoringIntegrationTests(Node testScene) : TestClass(testScene)
{
    [Test]
    [Chickensoft.GoDotTest.Timeout(120000)]
    public async Task Real_grid_draft_is_saved_before_publish_and_disk_reload_activates_new_content()
    {
        using var fixture = new RealAuthoringFixture(TestScene);
        await fixture.Start(); await fixture.Open();
        var original = fixture.Host.Content!;
        fixture.Service.OpenDefinition(new(DefinitionKind.Blueprint, "engine-model"));
        await fixture.Pump(() => fixture.Editor.Session is not null);
        AddCell(fixture.Editor);
        fixture.Service.Publish();
        Require(fixture.Editor.Leaving && fixture.Service.LastWrite is null, "未保存的真实网格编辑必须先询问，不能开始写包。");
        fixture.Editor.ResolveClose(LeaveChoice.Save);
        await fixture.Pump(() => !fixture.Service.IsBusy && !fixture.Editor.IsBusy && !fixture.Editor.IsOpen, ContentPreparationTimeout);
        Require(fixture.Service.LastWrite is { Success: true, Completed.Length: > 0 } && fixture.LastOperation?.Succeeded == true,
            "真实补丁写入或普通重载失败：" + fixture.Service.Message);
        Require(!ReferenceEquals(original, fixture.Host.Content) && fixture.Host.GetStatus()!.View.InputIdentity == fixture.Host.Content!.Core.Identity,
            "普通重载必须发布新内容并保持核心配对。");
        var saved = fixture.Editor.PackageDrafts(fixture.PackageId).Single();
        Require(saved.Payload is DraftPayload.Definition { Value: DefinitionDraft.Blueprint blueprint } && blueprint.Value.Layout.Cells.Length == 2,
            "真实 EncodeDraft/DecodeDraft 必须保留网格修改。");
        var address = new BlueprintAddress(BlueprintLibrary.Packages, new(fixture.PackageId.Value + ":engine-model"));
        Require(fixture.Host.Content!.FindBlueprint(address) is ContentLookup<BlueprintRecord>.Found current && current.Value.Definition.Layout.Cells.Length == 2,
            "激活内容必须从已写磁盘读取两个体素。");
        Require(original.FindBlueprint(address) is ContentLookup<BlueprintRecord>.Found old && old.Value.Definition.Layout.Cells.Length == 1,
            "写包和重载不得原地修改旧内存内容。");
    }

    [Test]
    [Chickensoft.GoDotTest.Timeout(120000)]
    public async Task Real_form_draft_survives_source_conflict_then_writes_on_explicitly_reopened_source()
    {
        using var fixture = new RealAuthoringFixture(TestScene);
        await fixture.Start(); await fixture.Open();
        var original = fixture.Host.Content!;
        fixture.Service.OpenDefinition(new(DefinitionKind.Voxel, "engine-food"));
        var form = new AuthoringDefinitionForm(); TestScene.AddChild(form);
        try
        {
            form.Load(fixture.Service.Form!); form.Edited += fixture.Service.MarkFormDirty;
            await new UiTestInput(TestScene).Enter(Field(form, "名称"), "真实表单写回");
            fixture.Service.Publish();
            Require(fixture.Service.Leaving && fixture.Service.LastWrite is null, "未保存表单必须先结束编辑。");
            fixture.Service.ResolveClose(LeaveChoice.Cancel, null);
            fixture.Service.SaveForm(form.Capture());
            await fixture.Pump(() => !fixture.Service.IsBusy);
            var saved = fixture.Service.Drafts().Single();
            var definition = Path.Combine(fixture.ModDirectory, "resources/voxels/engine-food.voxel.json");
            var external = File.ReadAllText(definition) + "\n "; File.WriteAllText(definition, external);
            fixture.Service.Publish(); await fixture.Pump(() => !fixture.Service.IsBusy, ContentPreparationTimeout);
            Require(fixture.Service.LastWrite is { Success: false, Completed.IsEmpty: true } && File.ReadAllText(definition) == external,
                "真实来源变更必须拒绝覆盖，磁盘外部修改保持原样。");
            Require(ReferenceEquals(original, fixture.Host.Content) && fixture.Service.Drafts().Single().Id == saved.Id,
                "来源冲突必须保留旧内容和真实磁盘草稿。");
            await fixture.Open();
            fixture.Service.OpenDraft(saved, useCurrentSource: true);
            fixture.Service.SaveForm(fixture.Service.Form!); await fixture.Pump(() => !fixture.Service.IsBusy);
            fixture.Service.Publish(); await fixture.Pump(() => !fixture.Service.IsBusy, ContentPreparationTimeout);
            Require(fixture.Service.LastWrite?.Success == true && fixture.LastOperation?.Succeeded == true,
                "显式在当前来源上打开后应可重新校验写入：" + fixture.Service.Message);
            Require(fixture.Host.Content!.FindVoxelDisplay(new(fixture.PackageId.Value + ":engine-food")) is ContentLookup<VoxelDisplayProjection>.Found { Value.Display.Name: "真实表单写回" },
                "真实非网格表单必须写入并经普通重载进入投影。");
        }
        finally { form.QueueFree(); await new UiTestInput(TestScene).Frames(2); }
    }

    [Test]
    public async Task Real_reload_rejects_deleted_running_definition_and_keeps_disk_changes_and_old_memory()
    {
        var core = ContentFixture.CopyBuiltIn();
        try
        {
            using var fixture = new RealAuthoringFixture(TestScene, core);
            await fixture.Start();
            var original = fixture.Host.Content!; var status = fixture.Host.GetStatus()!;
            var snapshot = PackageSnapshot.Capture(core);
            var package = Success(fixture.Host.ContentFactory.OpenAuthoringPackage(new("authoring"), new("core"), snapshot.Version, snapshot));
            var plan = Success(fixture.Host.ContentFactory.PreparePackageWrite(package,
                [new PackageEdit.DeleteDefinition(new(DefinitionKind.Voxel, "staple-food"))]));
            var written = PackageWriter.Execute(snapshot, plan, true);
            Require(written.Success && !File.Exists(Path.Combine(core, "resources/voxels/staple-food.voxel.json")), "真实删除补丁没有落盘。");
            fixture.Host.BeginReload(CommandSource.AutomationTest);
            await fixture.Pump(() => !fixture.Host.IsBusy, ContentPreparationTimeout);
            Require(fixture.LastOperation is { Succeeded: false, Diagnostics.IsEmpty: false } && ReferenceEquals(original, fixture.Host.Content)
                && fixture.Host.GetStatus()!.Binding == status.Binding && fixture.Host.GetStatus()!.View.InputIdentity == original.Core.Identity,
                "真实内容拒绝后不得切换活动核心与内存内容。");
            Require(!File.Exists(Path.Combine(core, "resources/voxels/staple-food.voxel.json")), "普通重载拒绝不得回滚已写文件。");
            Require(fixture.LastOperation!.Diagnostics.Any(d => !string.IsNullOrEmpty(d.Field) && EditorService.Describe(d).Contains(d.Code, StringComparison.Ordinal)),
                "拒绝必须提供可报告的真实内容诊断。");
        }
        finally { Directory.Delete(core, true); }
    }

    [Test]
    [Chickensoft.GoDotTest.Timeout(120000)]
    public async Task New_template_display_is_written_and_reloaded()
    {
        using var fixture = new RealAuthoringFixture(TestScene);
        await fixture.Start(); await fixture.Open();
        var original = fixture.Host.Content!;
        var draft = NewTemplate([]);
        fixture.Service.NewDefinition(draft);
        var panel = new AuthoringPanel(); panel.Initialize(fixture.Service, fixture.Editor); TestScene.AddChild(panel); panel.Open();
        var form = panel.FindChildren("DefinitionForm", "", true, false).OfType<AuthoringDefinitionForm>().Single();
        try
        {
            var captured = (DefinitionDraft.SettlementTemplate)form.Capture();
            Require(captured.Value.Display == draft.Value.Display, "表单必须读出新建模板的显示文本。");
            fixture.Service.SaveForm(captured);
            await fixture.Pump(() => !fixture.Service.IsBusy);
            fixture.Service.Publish();
            await fixture.Pump(() => !fixture.Service.IsBusy, ContentPreparationTimeout);
            var path = Path.Combine(fixture.ModDirectory, "resources", "settlement-templates", "new-template.settlement-template.json");
            Require(fixture.Service.LastWrite?.Success == true && fixture.LastOperation?.Succeeded == true && File.Exists(path),
                "新建模板必须写入并完成普通重载：" + fixture.Service.Message);
            var id = fixture.PackageId.Value + ":new-template";
            Require(fixture.Host.Content!.Core.SettlementTemplates.Any(template => template.Id.Value == id),
                "普通重载后的核心投影必须包含新模板。");
            Require(fixture.Host.Content.FindDefinitionDisplay(new CoreDefinitionReference.Template(new(id))) is ContentLookup<ContentDisplay>.Found found && found.Value.Text == draft.Value.Display,
                "重载后的显示文本必须来自草稿。");
            Require(!original.Core.SettlementTemplates.Any(template => template.Id.Value == id),
                "写入和重载不得改写旧内存内容。");
        }
        finally { panel.QueueFree(); await new UiTestInput(TestScene).Frames(2); }
    }

    [Test]
    [Chickensoft.GoDotTest.Timeout(120000)]
    public async Task Wrong_kind_inventory_blueprint_is_rejected_without_writing()
    {
        using var fixture = new RealAuthoringFixture(TestScene);
        await fixture.Start(); await fixture.Open();
        await RejectedForm(fixture, NewTemplate([new TemplateInventoryDraft.Blueprint(new("core:agricultural-town-ground"), 1)]), "/inventory", code: "reference_wrong_kind");
        Require(!File.Exists(Path.Combine(fixture.ModDirectory, "resources", "settlement-templates", "new-template.settlement-template.json")),
            "错误类别的库存蓝图不得留下模板文件。");
    }

    [Test]
    public async Task Lair_type_write_invalid_field_is_reported_without_host_special_case_or_writing()
    {
        using var fixture = new RealAuthoringFixture(TestScene);
        await fixture.Start(); await fixture.Open();
        await RejectedForm(fixture, new DefinitionDraft.LairType(new("new-lair", new("生物聚落", ""), null, null, [], null, null, null, null, null, null, null)), "/generation_weight");
    }

    private static DefinitionDraft.SettlementTemplate NewTemplate(params TemplateInventoryDraft[] inventory) =>
        new(new("new-template", new("新建模板", "隔离说明"), new("core:agricultural-town"), 1, new("core:agricultural-town-ground"),
            [new(2, PlotSize.SmallStation, new(104, 1, -176), VoxelOrientation.PxPy, null)], [], [.. inventory], []));

    private async Task RejectedForm(RealAuthoringFixture fixture, DefinitionDraft draft, string expected, string code = "invalid_field")
    {
        var original = fixture.Host.Content!;
        fixture.Service.NewDefinition(draft);
        var panel = new AuthoringPanel(); panel.Initialize(fixture.Service, fixture.Editor); TestScene.AddChild(panel); panel.Open();
        var form = panel.FindChildren("DefinitionForm", "", true, false).OfType<AuthoringDefinitionForm>().Single();
        try
        {
            fixture.Service.SaveForm(form.Capture());
            await fixture.Pump(() => !fixture.Service.IsBusy);
            fixture.Service.Publish(); await fixture.Pump(() => !fixture.Service.IsBusy, ContentPreparationTimeout);
            Require(fixture.Service.LastWrite is null && ReferenceEquals(original, fixture.Host.Content), "真实内容拒绝不能写文件或激活候选。");
            var message = Find<Label>(panel, "AuthoringResult").Text;
            Require(message == "一个文件也没写，游戏里的内容保持原样，草稿还在。",
                "结果栏必须只描述未写入和草稿保留，诊断由问题框显示：" + message);
            var issues = fixture.Service.PublishIssues;
            var operation = fixture.LastOperation;
            Require(issues.Any(issue => issue.Diagnostic.Code == code && issue.Diagnostic.Field.Contains(expected, StringComparison.Ordinal))
                && operation is { Succeeded: false } && operation.Diagnostics.Any(diagnostic => diagnostic.Code == code
                    && diagnostic.Field.Contains(expected, StringComparison.Ordinal)),
                "本次写入与真实宿主失败必须保留相同的字段错误诊断。");
            var input = new UiTestInput(TestScene);
            await input.Activate(Find<Button>(panel, "Problems"));
            var problems = Find<Control>(panel, "SourceProblems");
            Require(problems.IsVisibleInTree() && problems.FindChildren("LocateDiagnostic", "Button", true, false).OfType<Button>()
                .Any(button =>
                {
                    var text = string.Join("\n", button.GetParent().FindChildren("*", "Label", true, false).OfType<Label>().Select(label => label.Text));
                    return text.Contains(code, StringComparison.Ordinal) && text.Contains(expected, StringComparison.Ordinal);
                }), "真实问题框必须在同一诊断行显示错误代码、字段位置与定位按钮。");
            Require(fixture.Service.PublishIssues.SequenceEqual(issues) && ReferenceEquals(operation, fixture.LastOperation),
                "打开问题框不能修改本次写入诊断或宿主结果。");
            Require(fixture.Service.Drafts().Length == 1, "写包拒绝须保留已成功编码和保存的草稿。");
        }
        finally { panel.QueueFree(); await new UiTestInput(TestScene).Frames(2); }
    }

    internal static void AddCell(EditorService editor)
    {
        var palette = editor.State!.Snapshot.Draft.Layout.Palette[0];
        var operation = new EditOperation.Cells(new EditFootprint.Cell(new(1, 0, 0)), new CellEdit.Add(palette));
        var preview = editor.Preview(operation);
        Require(preview is SceneResult<IEditPreview>.Success { Value.CanApply: true }, "真实网格修改预览不可应用：" + preview);
        editor.Apply(operation);
        Require(editor.IsDirty && editor.Session!.Capture().Draft.Layout.Cells.Length == 2, "真实网格操作未产生未保存修改。");
    }
    private static LineEdit Field(AuthoringDefinitionForm form, string title) => form.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().Single(field => field.TooltipText == title);
    internal static T Success<T>(ContentResult<T> result) where T : notnull => result is ContentResult<T>.Success success ? success.Value : throw new InvalidOperationException(result.ToString());
}
