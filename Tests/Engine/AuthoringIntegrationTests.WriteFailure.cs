using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App.Authoring;
using Train.App.Editing;
using Train.Content.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class AuthoringIntegrationTests
{
    [Test]
    [Chickensoft.GoDotTest.Timeout(120000)]
    public async Task Real_external_source_change_is_reported_once_inside_authoring_without_a_global_error()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var input = new UiTestInput(TestScene);
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
            await input.Activate(Find<Button>(panel, "Publish"));
            await input.Activate(Find<Button>(panel, "ConfirmWrite"));
            await FramesUntil(TestScene, () => !service.IsBusy && !host.IsBusy, timeout: ContentPreparationTimeout);
            Require(service.LastWrite is { Success: false, Completed.IsEmpty: true } && service.WriteStage == AuthoringWriteStage.CheckSource,
                "外部修改必须在复核源文件时拒绝全部写入：" + service.Message);
            Require(File.ReadAllText(path) == external, "外部修改必须保持原样。");
            Require(service.Package is not null && service.Drafts().Single().Id == saved.Id,
                "来源冲突必须保留源包和草稿。");
            Require(ReferenceEquals(original, host.Content), "拒绝写入不得切换内存内容。");
            Require(service.PublishIssues is [{ Diagnostic.Code: "stale_basis" } issue]
                && issue.Diagnostic.Message.Contains("源包已发生外部修改", StringComparison.Ordinal),
                "本次写入的结构化诊断必须给出真实来源冲突原因。");
            var result = Find<Label>(panel, "AuthoringResult").Text;
            Require(result.Contains("一个文件也没写", StringComparison.Ordinal) && result.Contains("草稿还在", StringComparison.Ordinal)
                && !result.Contains("stale_basis", StringComparison.Ordinal),
                "结果句必须描述未写入和草稿保留，诊断由本次写入的问题行单独显示：" + result);
            var report = Find<Control>(panel, "WriteProgress");
            Require(report.IsVisibleInTree() && report.FindChildren("*", "Label", true, false).OfType<Label>()
                .Count(label => label.Text.Contains("stale_basis", StringComparison.Ordinal)) == 1,
                "创作写入结果框必须只显示一次本次来源冲突诊断。");
            var globalError = Find<PanelContainer>(ui, "ErrorPanel");
            Require(!globalError.IsVisibleInTree(), "创作写入失败不能在创作结果框外再弹全局错误面板。");

            var currentForm = service.Form;
            await input.Activate(Find<Button>(report, "LocateDiagnostic"));
            var problems = Find<Control>(panel, "SourceProblems");
            Require(problems.IsVisibleInTree() && !report.IsVisibleInTree() && ReferenceEquals(currentForm, service.Form)
                && problems.FindChildren("*", "Label", true, false).OfType<Label>()
                    .Any(label => label.Text.Contains("没有对应的可编辑定义", StringComparison.Ordinal)),
                "没有对应定义的来源诊断必须转到问题框，提示直接检查源文件，不能切换表单。");
            await input.Activate(Find<Button>(problems, "CloseProblems"));
            await input.Activate(Find<Button>(panel, "ExitAuthoring"));
            await input.WaitUntil(() => !panel.IsVisibleInTree());
            Require(!globalError.IsVisibleInTree(), "退出创作模式后不能残留全局操作失败面板。");
        }
        finally
        {
            try { await fixture.Cleanup(); }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
    }
}
