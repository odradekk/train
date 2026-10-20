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

public class AuthoringIntegrationTests(Node testScene) : TestClass(testScene)
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
    [Chickensoft.GoDotTest.Timeout(120000)]
    public async Task Real_external_source_change_is_rejected_with_diagnostic_not_host_fault_in_ui()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var id = Guid.NewGuid();
        var directory = Path.Combine(AuthoringPaths.ModsDirectory(OS.GetUserDataDir()), id.ToString("D"));
        try
        {
            ContentFixture.WriteMod(directory, id);
            var app = await fixture.LoadAndAddScene<Train.App.App>("res://App/App.tscn");
            await FramesUntil(TestScene, () => !app.Host!.IsBusy, timeout: ContentPreparationTimeout);
            var host = app.Host!;
            Require(host.BeginNewGame(new("core:basic-through-world"), 1, "stage1-world-v1", host.SessionOptions!).Problem is null,
                "真实新局请求被拒绝。");
            await FramesUntil(TestScene, () => !host.IsBusy && host.GetStatus() is not null, timeout: ContentPreparationTimeout);
            var original = host.Content!;
            var service = app.Authoring!;
            Require(service.CanUse, "真实开发构建必须允许创作模式。");
            service.Open(directory, new(id.ToString("D")));
            await FramesUntil(TestScene, () => !service.IsBusy && service.Package is not null);
            service.OpenDefinition(new(DefinitionKind.Voxel, "engine-food"), grid: false);
            Require(service.Form is not null, "真实源包没有打开体素表单。");
            service.SaveForm(service.Form!);
            await FramesUntil(TestScene, () => !service.IsBusy);
            var saved = service.Drafts().Single();
            var path = Path.Combine(directory, "resources/voxels/engine-food.voxel.json");
            var external = File.ReadAllText(path) + "\n ";
            File.WriteAllText(path, external);
            var ui = app.GetNode<GameUi>("GameUi");
            var panel = Find<AuthoringPanel>(ui, "Authoring");
            panel.Open();
            await new UiTestInput(TestScene).Activate(Find<Button>(panel, "Publish"));
            await FramesUntil(TestScene, () => !service.IsBusy && !host.IsBusy, timeout: ContentPreparationTimeout);
            Require(service.LastWrite is { Success: false, Completed.IsEmpty: true },
                "外部修改必须拒绝全部写入：" + service.Message);
            Require(File.ReadAllText(path) == external, "外部修改必须保持原样。");
            Require(service.Package is not null && service.Drafts().Single().Id == saved.Id,
                "来源冲突必须保留源包和草稿。");
            Require(ReferenceEquals(original, host.Content), "拒绝写入不得切换内存内容。");
            var result = Find<Label>(panel, "AuthoringResult").Text;
            Require(result.Contains("stale_basis", StringComparison.Ordinal) &&
                result.Contains("源包已发生外部修改", StringComparison.Ordinal) &&
                result.Contains("未写入任何文件", StringComparison.Ordinal) &&
                result.Contains("草稿保留", StringComparison.Ordinal) &&
                result.Contains("请重新载入", StringComparison.Ordinal) &&
                result.Contains("处理外部修改后重新校验", StringComparison.Ordinal),
                "真实创作面板必须显示诊断、未写入、草稿保留与重新载入提示：" + result);
            Require(ui.ErrorText.Contains("源包已发生外部修改", StringComparison.Ordinal) &&
                ui.ErrorText.Contains("请重新载入", StringComparison.Ordinal) &&
                !ui.ErrorText.Contains("发生故障", StringComparison.Ordinal) &&
                !ui.ErrorText.Contains("package_write_failed", StringComparison.Ordinal),
                "真实 GameUi 弹窗必须只显示诊断与重新载入提示：" + ui.ErrorText);
        }
        finally
        {
            try { await fixture.Cleanup(); }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
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
    public async Task New_template_display_gap_is_reported_with_field_without_writing()
    {
        using var fixture = new RealAuthoringFixture(TestScene);
        await fixture.Start(); await fixture.Open();
        await RejectedForm(fixture, new DefinitionDraft.SettlementTemplate(new("new-template", new("core:agricultural-town"), 1, null, [], [], [], [])), "/display");
    }

    [Test]
    public async Task Lair_type_write_invalid_field_is_reported_without_host_special_case_or_writing()
    {
        using var fixture = new RealAuthoringFixture(TestScene);
        await fixture.Start(); await fixture.Open();
        await RejectedForm(fixture, new DefinitionDraft.LairType(new("new-lair", new("生物聚落", ""), null, null, [], null, null, null, null, null, null, null)), "/generation_weight");
    }

    private async Task RejectedForm(RealAuthoringFixture fixture, DefinitionDraft draft, string expected, bool encodable = true)
    {
        var original = fixture.Host.Content!;
        fixture.Service.NewDefinition(draft);
        var panel = new AuthoringPanel(); panel.Initialize(fixture.Service, fixture.Editor); TestScene.AddChild(panel); panel.Open();
        var form = panel.FindChildren("DefinitionForm", "", true, false).OfType<AuthoringDefinitionForm>().Single();
        try
        {
            fixture.Service.SaveForm(form.Capture());
            await fixture.Pump(() => !fixture.Service.IsBusy);
            if (encodable) { fixture.Service.Publish(); await fixture.Pump(() => !fixture.Service.IsBusy, ContentPreparationTimeout); }
            Require(fixture.Service.LastWrite is null && ReferenceEquals(original, fixture.Host.Content), "真实内容拒绝不能写文件或激活候选。");
            var message = Find<Label>(panel, "AuthoringResult").Text;
            Require(message.Contains(expected, StringComparison.Ordinal) && message.Contains("invalid_field", StringComparison.Ordinal),
                "真实面板必须按普通诊断路径显示错误与字段位置：" + message);
            Require(fixture.Service.Drafts().Length == (encodable ? 1 : 0), "仅成功编码的草稿可以落盘；写包拒绝须保留已保存草稿。");
        }
        finally { panel.QueueFree(); await new UiTestInput(TestScene).Frames(2); }
    }

    internal static void AddCell(EditorService editor)
    {
        var palette = editor.State!.Snapshot.Draft.Layout.Palette[0];
        var preview = editor.Preview(new EditOperation.Cells(new EditFootprint.Cell(new(1, 0, 0)), new CellEdit.Add(palette)));
        Require(preview is SceneResult<IEditPreview>.Success { Value.CanApply: true }, "真实网格修改预览不可应用：" + preview);
        editor.Apply(((SceneResult<IEditPreview>.Success)preview!).Value, false);
        Require(editor.IsDirty && editor.Session!.Capture().Draft.Layout.Cells.Length == 2, "真实网格操作未产生未保存修改。");
    }
    private static LineEdit Field(AuthoringDefinitionForm form, string title) => form.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().Single(field => field.TooltipText == title);
    internal static T Success<T>(ContentResult<T> result) where T : notnull => result is ContentResult<T>.Success success ? success.Value : throw new InvalidOperationException(result.ToString());
}
