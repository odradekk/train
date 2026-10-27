using System.Text.Json;
using Train.App.Saves;
using Train.Cli;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.SceneScale;

internal static class Composition
{
    internal static (ICoreSession Core, IContentView Content, IScenesFactory Scenes) Prepare(string repository, string? save)
    {
        var content = Train.Content.ContentFactory.Create();
        var core = Train.Core.CoreFactory.Create();
        ICoreCapture? state = null;
        ContentLoadPurpose purpose = new ContentLoadPurpose.Initial();
        if (save is not null)
        {
            var document = new FileSaveStore(repository).Read(save);
            state = Require(core.DecodeState(document.CorePayload));
            purpose = new ContentLoadPurpose.Restore(Require(content.DecodeCapture(document.ContentPayload)), state.References);
        }
        const string versionPrefix = "config/version=";
        var versionLine = File.ReadLines(Path.Combine(repository, "project.godot"))
            .Single(line => line.StartsWith(versionPrefix, StringComparison.Ordinal));
        var version = JsonSerializer.Deserialize<string>(versionLine[versionPrefix.Length..])
            ?? throw new InvalidDataException("游戏版本缺失。");
        using var loaded = Require(content.PrepareLoad(new(
            new(CliContentFiles.BuiltIn, CliContentFiles.Mods, null, version, []), purpose),
            new CliContentFiles(repository), new AssetInspector()));
        if (loaded.Report.Diagnostics.Any(d => d.Severity == ContentDiagnosticSeverity.Error) || !loaded.Report.RuntimeMissing.IsEmpty)
            throw new InvalidDataException("内容加载失败。");
        var view = loaded.View;
        var options = new CoreSessionOptions(new("probe", "probe", "anonymous", "anonymous", "net10"), false, 1);
        using var candidate = state is null
            ? Require(core.PrepareNew(Require(content.CreateWorldInput(view, new("core:basic-through-world"), 40, "stage1-world-v1")), view.Core, options))
            : Require(core.PrepareRestore(state, view.Core, options));
        if (!candidate.MissingReferences.IsEmpty)
            throw new InvalidDataException("存档引用缺失。");
        if (state is null) view = content.WithSettlementCopies(view, candidate.References.Templates);
        loaded.Activate();
        return (candidate.Activate(), view, Train.Scenes.ScenesFactory.Create());
    }

    private static T Require<T>(ContentResult<T> result) where T : notnull => result is ContentResult<T>.Success success
        ? success.Value : throw new InvalidDataException("内容操作失败。");

    private static T Require<T>(CoreResult<T> result) where T : notnull => result is CoreResult<T>.Success success
        ? success.Value : throw new InvalidDataException("核心操作失败。");
}
