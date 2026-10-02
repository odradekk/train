using Train.Domain.Voxels;

namespace Train.Domain.WorldEditor;

public sealed record WorldEditorExample(string Path, string Purpose, string ApplicableSinceVersion);

public sealed record WorldEditorExampleEntry(
    WorldEditorExample Example, WorldSaveDocument? Document, WorldSaveResult Recovery);

/// <summary>Only ordinary, distributed saves belong here; test fixtures are never discovered by scanning.</summary>
public static class WorldEditorExamples
{
    public static IReadOnlyList<WorldEditorExample> All { get; } = Array.AsReadOnly(new[]
    {
        new WorldEditorExample("res://examples/basic-editing.world.json",
            "观察和编辑体素，运行模拟以执行待执行的放置命令。", "0.0.43")
    });
}
