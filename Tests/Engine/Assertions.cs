using System.Diagnostics;
using System.Runtime.CompilerServices;
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

    internal static readonly TimeSpan ContentPreparationTimeout = TimeSpan.FromSeconds(60);

    internal static async Task FramesUntil(Node testScene, Func<bool> completed,
        GodotMainThreadDispatcher? dispatcher = null, TimeSpan? timeout = null,
        [CallerArgumentExpression(nameof(completed))] string? description = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        var watch = Stopwatch.StartNew();
        while (!completed() && watch.Elapsed < limit)
        {
            dispatcher?.Pump();
            await testScene.ToSignal(testScene.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        Require(completed(), $"真实 Godot 帧等待超时：{description}；已等待 {watch.Elapsed.TotalSeconds:F3} 秒，上限 {limit.TotalSeconds:F3} 秒。");
    }
}
