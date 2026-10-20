using System.Text.Json;
using Godot;
using Train.Content.Contracts;
using Train.Scenes.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

// 仅由手工 --authoring-core-probe 入口执行；普通自动套件不写仓库内容。
public static class AuthoringCoreProbe
{
    public static async Task<int> Run(Node runner)
    {
        Require(OS.HasFeature("editor"), "源码 core 写入取证必须使用编辑器二进制。");
        var core = ProjectSettings.GlobalizePath("res://builtin/core");
        var path = Path.Combine(core, "resources/blueprints/small-locomotive.blueprint.json");
        var original = File.ReadAllBytes(path);
        var output = System.Environment.GetEnvironmentVariable("TRAIN_AUTHORING_CORE_REPORT")!;
        try
        {
            using var fixture = new RealAuthoringFixture(runner, core);
            await fixture.Start();
            Require(fixture.Service.CanWriteCore, "真实源码 core 门槛未通过。");
            fixture.Service.OpenCore(); await fixture.Pump(() => !fixture.Service.IsBusy);
            Require(fixture.Service.Package is not null, "真实 core 无法打开：" + fixture.Service.Message);
            fixture.Service.OpenDefinition(new(DefinitionKind.Blueprint, "small-locomotive"));
            await fixture.Pump(() => fixture.Editor.Session is not null);
            var draft = fixture.Editor.State!.Snapshot.Draft;
            var name = draft.Display.Name + "（源码写入验证）";
            var preview = fixture.Editor.Preview(new EditOperation.Properties(draft.Display with { Name = name }, draft.Specification));
            Require(preview is SceneResult<IEditPreview>.Success { Value.CanApply: true }, "真实 core 编辑预览不可应用。");
            fixture.Editor.Apply(((SceneResult<IEditPreview>.Success)preview!).Value, false);
            fixture.Editor.SaveDraft(); await fixture.Pump(() => !fixture.Editor.IsBusy);
            fixture.Service.Publish(); await fixture.Pump(() => !fixture.Service.IsBusy, ContentPreparationTimeout);
            Require(fixture.Service.LastWrite is { Success: true } && fixture.LastOperation?.Succeeded == true,
                "真实源码写入或普通重载失败：" + fixture.Service.Message);
            Require(fixture.Host.Content!.FindBlueprint(new(BlueprintLibrary.Packages, new("core:small-locomotive")))
                is ContentLookup<BlueprintRecord>.Found found && found.Value.Definition.Display.Name == name, "源码写入未激活。");
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = true, core, file = path,
                changed = !File.ReadAllBytes(path).SequenceEqual(original), completed = fixture.Service.LastWrite!.Completed.Select(c => c.RelativePath),
                generation = fixture.Host.Content.Generation.Value, message = fixture.Service.Message }));
            return 0;
        }
        finally
        {
            File.WriteAllBytes(path, original);
            Require(File.ReadAllBytes(path).SequenceEqual(original), "源码 core 原始字节恢复失败。");
        }
    }
}
