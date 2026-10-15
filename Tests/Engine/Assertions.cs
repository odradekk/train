using System.Diagnostics;
using Godot;
using Train.App.Assets;
using Train.Core.Contracts;

namespace Train.Engine.Tests;

internal static class Assertions
{
    internal static SubmissionResult Completed(ulong number, ICoreReadView before, ICoreReadView after, CommandDecision decision)
        => new SubmissionResult.Completed(new(number, before.Position, after.Position, decision), [], null);
    internal static T Find<T>(Node parent, string name) where T : Node => parent.FindChildren(name, "", true, false).OfType<T>().Single();
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
