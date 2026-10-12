using System.Runtime.Loader;
using Chickensoft.GoDotTest;
using Godot;

namespace Train.Tests.Engine.Bootstrap;

public partial class Runner : Node
{
    public override async void _Ready()
    {
        try
        {
            // _Ready 期间根节点仍在挂接子节点；测试加载 App 必须从下一帧开始。
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var assembly = AssemblyLoadContext.GetLoadContext(GetType().Assembly)!
                .LoadFromAssemblyPath(System.Environment.GetEnvironmentVariable("TRAIN_ENGINE_TEST_ASSEMBLY")!);
            GoTest.Adapter = new ReportAdapter(System.Environment.GetEnvironmentVariable("TRAIN_ENGINE_REPORT")!);
            GoTest.TimeoutMilliseconds = 60000;
            await GoTest.RunTests(assembly, this, TestEnvironment.From(OS.GetCmdlineUserArgs()));
        }
        catch (Exception exception)
        {
            GD.PrintErr(exception);
            GetTree().Quit(1);
        }
    }
}
