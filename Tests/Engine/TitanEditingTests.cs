using Chickensoft.GoDotTest;
using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

// 大尺寸与拥挤场景专项的编辑器样例：真实泰坦设计上一次 5,000 格区域编辑、50 步撤销和重做，结果仍能通过发布评估。
public class TitanEditingTests(Node testScene) : TestClass(testScene)
{
    [Test]
    [Chickensoft.GoDotTest.Timeout(300000)]
    public async Task Titan_region_edit_of_5000_cells_undoes_and_redoes_50_steps_and_still_evaluates_valid()
    {
        using var fixture = new RealAuthoringFixture(TestScene);
        await fixture.Start();
        var content = fixture.Host.Content!;
        var titan = (DesignEvaluation.Complete)((ContentLookup<BlueprintRecord>.Found)content.FindBlueprint(
            new(BlueprintLibrary.Packages, new("core:titan-armed-vehicle")))).Value.Evaluation;
        using var session = Value(fixture.Scenes.OpenEditor(content, titan));
        var original = session.Capture().Draft;
        var cube = new VoxelPaletteEntry(new("core:structural-metal"), VoxelOrientation.PxPy, null, null);

        var region = EmptyBox(titan.Scene);
        var preview = Value(session.Preview(new EditOperation.Cells(new EditFootprint.Box(region), new CellEdit.Add(cube))));
        Require(preview is { CoveredCells: 5000, AffectedCells: 5000, CanApply: true, RequiresConfirmation: true },
            $"泰坦内部 5,000 格区域编辑预览不符：覆盖 {preview.CoveredCells}，改动 {preview.AffectedCells}，冲突 {preview.Conflicts.Length}。");
        var edited = Value(session.Apply(preview, true)).Snapshot.Draft;
        Require(edited.Layout.Cells.Length == original.Layout.Cells.Length + 5000, "区域编辑没有加入 5,000 格。");
        // 另外 49 次单格删除，凑满 50 步历史。
        foreach (var at in Cells(region).Take(49))
            Value(session.Apply(Value(session.Preview(new EditOperation.Cells(new EditFootprint.Cell(at), new CellEdit.Remove()))), false));
        var final = session.Capture().Draft;

        for (var step = 0; step < 50; step++) Value(session.Undo());
        Require(!session.Read().CanUndo && session.Capture().Draft.Layout.Cells.SequenceEqual(original.Layout.Cells),
            "50 步撤销后必须回到原始泰坦且没有更多历史。");
        for (var step = 0; step < 50; step++) Value(session.Redo());
        var redone = session.Capture().Draft;
        Require(!session.Read().CanRedo && redone.Layout.Cells.SequenceEqual(final.Layout.Cells),
            "50 步重做后必须回到编辑后的设计。");

        var evaluation = fixture.Host.ContentFactory.EvaluateDesign(content, redone);
        Require(evaluation is ContentResult<DesignEvaluation>.Success { Value: DesignEvaluation.Complete { IsValid: true } complete }
            && complete.Scene.Voxels.Length == titan.Scene.Voxels.Length + 5000 - 49,
            "编辑后的泰坦必须仍能通过发布评估：" + evaluation);
    }

    // 在泰坦占用范围内找一块体积正好 5,000 格、完全空着的长方体，保证区域编辑的每一格都是改动。
    private static GridBounds EmptyBox(DesignSceneProjection scene)
    {
        var bounds = scene.OccupiedBounds!;
        var (nx, ny, nz) = (bounds.Maximum.X - bounds.Minimum.X + 1, bounds.Maximum.Y - bounds.Minimum.Y + 1,
            bounds.Maximum.Z - bounds.Minimum.Z + 1);
        var sum = new int[nx + 1, ny + 1, nz + 1];
        foreach (var voxel in scene.Voxels)
            sum[voxel.At.X - bounds.Minimum.X + 1, voxel.At.Y - bounds.Minimum.Y + 1, voxel.At.Z - bounds.Minimum.Z + 1] = 1;
        for (var x = 1; x <= nx; x++)
        for (var y = 1; y <= ny; y++)
        for (var z = 1; z <= nz; z++)
            sum[x, y, z] += sum[x - 1, y, z] + sum[x, y - 1, z] + sum[x, y, z - 1] - sum[x - 1, y - 1, z]
                - sum[x - 1, y, z - 1] - sum[x, y - 1, z - 1] + sum[x - 1, y - 1, z - 1];
        for (var a = 1; a <= nx; a++)
        for (var b = 1; b <= ny; b++)
        {
            if (5000 % (a * b) != 0 || 5000 / (a * b) is var c && c > nz) continue;
            for (var x = 0; x + a <= nx; x++)
            for (var y = 0; y + b <= ny; y++)
            for (var z = 0; z + c <= nz; z++)
                if (sum[x + a, y + b, z + c] - sum[x, y + b, z + c] - sum[x + a, y, z + c] - sum[x + a, y + b, z]
                    + sum[x, y, z + c] + sum[x, y + b, z] + sum[x + a, y, z] - sum[x, y, z] == 0)
                    return new(new(bounds.Minimum.X + x, bounds.Minimum.Y + y, bounds.Minimum.Z + z),
                        new(bounds.Minimum.X + x + a - 1, bounds.Minimum.Y + y + b - 1, bounds.Minimum.Z + z + c - 1));
        }
        throw new InvalidOperationException("泰坦范围内没有体积 5,000 格的空长方体。");
    }

    private static IEnumerable<GridPosition> Cells(GridBounds box)
    {
        for (var x = box.Minimum.X; x <= box.Maximum.X; x++)
        for (var y = box.Minimum.Y; y <= box.Maximum.Y; y++)
        for (var z = box.Minimum.Z; z <= box.Maximum.Z; z++)
            yield return new(x, y, z);
    }

    private static T Value<T>(SceneResult<T> result) where T : notnull => result is SceneResult<T>.Success success
        ? success.Value : throw new InvalidOperationException("编辑器调用失败：" + result);
}
