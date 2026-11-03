using Chickensoft.GoDotTest;
using Godot;
using Train.App.Editing;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class AuthoringPackageCreationTests(Node testScene) : TestClass(testScene)
{
    [Test]
    [Chickensoft.GoDotTest.Timeout(120000)]
    public async Task New_mod_can_be_written_from_the_menu_then_used_by_a_new_game()
    {
        using var fixture = new RealAuthoringFixture(TestScene);
        Require(fixture.Host.BeginReload(CommandSource.AutomationTest).Problem is null, "菜单初始加载请求被拒绝。");
        await fixture.Pump(() => !fixture.Host.IsBusy, ContentPreparationTimeout);
        Require(fixture.LastOperation?.Succeeded == true && fixture.Host.Content is not null && fixture.Host.GetStatus() is null,
            "创作前必须完成真实内容加载，并保持无世界的菜单状态。");

        var metadata = new PackageMetadata(new("d76611b9-b408-447f-aa9e-2da18b463f56"), "菜单创作模组", "0.1.0", ["Engine tests"], []);
        var directory = Path.Combine(fixture.Mods, metadata.Id.Value);
        fixture.Service.CreateMod(metadata);
        await fixture.Pump(() => !fixture.Service.IsBusy);
        Require(fixture.Service.Package?.Package == metadata.Id && fixture.Service.Target is { } openedTarget
            && Path.GetFullPath(openedTarget.Directory) == Path.GetFullPath(directory)
            && File.Exists(Path.Combine(directory, "mod.json")),
            "创建必须在隔离 mods 目录写出真实包，并自动打开它：" + fixture.Service.Message);
        Require(fixture.Host.GetStatus() is null, "创建模组不得隐式创建世界。");

        var definition = new DefinitionDraft.SettlementTemplate(new("menu-template", new("菜单创作模板", "无世界时保存并发布"),
            new("core:agricultural-town"), 1, new("core:agricultural-town-ground"),
            [new(2, PlotSize.SmallStation, new(104, 1, -176), VoxelOrientation.PxPy, null)], [], [], []));
        fixture.Service.NewDefinition(definition);
        Require(fixture.Service.Form is DefinitionDraft.SettlementTemplate, "菜单创作必须能够打开新定义表单。");
        await fixture.Pump(() => fixture.Service.TemplatePreview is not null, ContentPreparationTimeout);
        Require(fixture.Service.TemplatePreview?.Content == fixture.Host.Content!.Generation,
            "没有世界时也必须能够预览当前内容版本中的模板：" + fixture.Service.Message);
        fixture.Service.SaveForm(fixture.Service.Form!);
        await fixture.Pump(() => !fixture.Service.IsBusy);
        var saved = fixture.Service.Drafts().Single();
        Require(saved.Target is DraftTarget.PackageDefinition target && target.Package == metadata.Id
            && target.Kind == DefinitionKind.SettlementTemplate && target.LocalId == "menu-template",
            "保存必须生成属于新模组的真实模板草稿。");
        var draftPath = Directory.EnumerateFiles(AuthoringPaths.PackageDraftsDirectory(fixture.UserData, metadata.Id), "*.json").Single();
        var draftBytes = File.ReadAllBytes(draftPath);

        fixture.Service.Publish();
        await fixture.Pump(() => !fixture.Service.IsBusy && !fixture.Host.IsBusy, ContentPreparationTimeout);
        Require(fixture.Service.LastWrite is { Success: true, Completed.IsEmpty: false } && fixture.LastOperation?.Succeeded == true,
            "菜单创作必须完成真实写包和普通重载：" + fixture.Service.Message);
        Require(fixture.Host.Content!.Packages.Any(package => package.Metadata?.Id == metadata.Id && package.State == PackageState.Available),
            "新建模组必须被登记并经普通重载启用。");
        var id = metadata.Id.Value + ":menu-template";
        Require(fixture.Host.Content.Core.SettlementTemplates.Any(template => template.Id.Value == id)
            && fixture.Host.Content.FindDefinitionDisplay(new CoreDefinitionReference.Template(new(id)))
                is ContentLookup<ContentDisplay>.Found found && found.Value.Text == definition.Value.Display,
            "写入的新定义和显示文本必须能从重载后的真实内容读取。");
        Require(fixture.Host.GetStatus() is null, "写包和重载后仍须保持无世界的菜单状态。");
        Require(fixture.Editor.PackageDrafts(metadata.Id).Single().Id == saved.Id
            && draftBytes.AsSpan().SequenceEqual(File.ReadAllBytes(draftPath)), "写包和普通重载必须保留磁盘草稿。");

        Require(fixture.Host.BeginNewGame(new("core:basic-through-world"), 1, "stage1-world-v1", fixture.Host.SessionOptions!).Problem is null,
            "菜单创作结束后，新局请求不得被残留编辑状态阻止。");
        await fixture.Pump(() => !fixture.Host.IsBusy, ContentPreparationTimeout);
        Require(fixture.LastOperation?.Succeeded == true && fixture.Host.GetStatus() is not null,
            "菜单创作结束后必须能够创建真实普通游戏会话。");
    }

    [Test]
    [Chickensoft.GoDotTest.Timeout(120000)]
    public async Task Existing_mod_directory_is_preserved_and_is_not_enabled_or_opened()
    {
        using var fixture = new RealAuthoringFixture(TestScene);
        var metadata = new PackageMetadata(new("47f069f4-08b0-4c83-8e4a-0e3f435f5146"), "不得覆盖现有模组", "0.1.0", ["Engine tests"], []);
        var directory = Path.Combine(fixture.Mods, metadata.Id.Value);
        ContentFixture.WriteMod(directory, Guid.Parse(metadata.Id.Value));
        File.WriteAllBytes(Path.Combine(directory, "marker.bin"), [0, 255, 17, 128]);
        var originalFiles = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(directory, path), File.ReadAllBytes);
        Require(fixture.Host.BeginReload(CommandSource.AutomationTest).Problem is null, "菜单初始加载请求被拒绝。");
        await fixture.Pump(() => !fixture.Host.IsBusy, ContentPreparationTimeout);
        Require(fixture.LastOperation?.Succeeded == true && fixture.Host.Content!.Packages.Any(package =>
            package.Metadata?.Id == metadata.Id && package.State == PackageState.Disabled), "冲突目录中的合法模组必须初始未启用。");
        await fixture.Open();
        var originalContent = fixture.Host.Content;
        var originalPackage = fixture.Service.Package;
        var originalTarget = fixture.Service.Target;
        var originalMessage = fixture.Service.Message;

        fixture.Service.CreateMod(metadata);
        await fixture.Pump(() => !fixture.Service.IsBusy && !fixture.Host.IsBusy);
        Require(!string.IsNullOrWhiteSpace(fixture.Service.Message) && fixture.Service.Message != originalMessage,
            "同名目录冲突必须向调用方报告创建失败。");
        Require(ReferenceEquals(originalContent, fixture.Host.Content) && ReferenceEquals(originalPackage, fixture.Service.Package)
            && fixture.Service.Target == originalTarget, "创建失败不得切换已打开源包或活动内容。");
        Require(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Count() == originalFiles.Count
            && originalFiles.All(file => File.Exists(Path.Combine(directory, file.Key))
                && file.Value.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(directory, file.Key)))),
            "同名目录创建失败不得新增、删除或覆盖任何现有文件，包括 marker。");

        Require(fixture.Host.BeginReload(CommandSource.AutomationTest).Problem is null, "冲突后的普通重载请求被拒绝。");
        await fixture.Pump(() => !fixture.Host.IsBusy, ContentPreparationTimeout);
        Require(fixture.LastOperation?.Succeeded == true && fixture.Host.Content!.Packages.Any(package =>
            package.Metadata?.Id == metadata.Id && package.State == PackageState.Disabled),
            "创建失败不得把现有目录登记为启用模组。");
        Require(fixture.Host.GetStatus() is null, "创建冲突和普通重载不得隐式创建世界。");
    }
}
