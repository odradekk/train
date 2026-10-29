using Godot;
using Train.Rendering;
using Train.Scenes.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

internal static class ScenePackingTestDriver
{
    internal static Task StartFormal(SceneDisplay display, ISceneFrame frame, Node testScene) =>
        Run(display, testScene, () =>
        {
            using var prepared = display.Prepare(frame);
            Require(prepared is null, "正式准备必须等待首个网格打包。");
        }, () => display.PreparationPacking);

    internal static Task StartCandidate(SceneDisplay display, Node testScene) =>
        Run(display, testScene, () => display.PrepareCandidate(TimeSpan.FromMilliseconds(4)),
            () => display.CandidatePacking);

    private static async Task Run(SceneDisplay display, Node testScene, Action start, Func<Task> packing)
    {
        await new UiTestInput(testScene).Frames(1);
        var timestamp = display.Timestamp;
        var packMesh = display.PackMesh;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = Task.CompletedTask;
        try
        {
            display.Timestamp = () => 0;
            display.PackMesh = (mesh, cancellation) =>
            {
                entered.TrySetResult();
                release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellation).GetAwaiter().GetResult();
                return packMesh(mesh, cancellation);
            };
            start();
            completion = packing();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            release.TrySetResult();
            try { await completion.WaitAsync(TimeSpan.FromSeconds(30)); }
            finally
            {
                display.PackMesh = packMesh;
                display.Timestamp = timestamp;
            }
        }
        await new UiTestInput(testScene).Frames(1);
    }
}
