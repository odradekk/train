using System.Diagnostics;
using Godot;
using Train.App.Assets;

namespace Train.Engine.Tests;

internal static class Assertions
{
    internal static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    internal static async Task FramesUntil(Node testScene, Func<bool> completed,
        GodotMainThreadDispatcher? dispatcher = null)
    {
        var watch = Stopwatch.StartNew();
        while (!completed() && watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            dispatcher?.Pump();
            await testScene.ToSignal(testScene.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        Require(completed(), "真实 Godot 帧未完成模块交接。");
    }
}
