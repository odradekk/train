using System.Runtime.Loader;
using Chickensoft.GoDotTest;
using Godot;

namespace Train.Tests.Engine.Bootstrap;

public partial class Runner : Node
{
    [Export] public bool ObserveHostWindow { get; set; }

    public override async void _Ready()
    {
        try
        {
            // _Ready 期间根节点仍在挂接子节点；测试加载 App 必须从下一帧开始。
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var path = ObserveHostWindow
                ? ProjectSettings.GlobalizePath("res://.godot/mono/temp/bin/Debug/Train.Engine.Tests.dll")
                : System.Environment.GetEnvironmentVariable("TRAIN_ENGINE_TEST_ASSEMBLY")!;
            var context = AssemblyLoadContext.GetLoadContext(GetType().Assembly)!;
            // 测试 DLL 的依赖不在产品 deps.json 中；仅此 Debug 引导器加载共享替身。
            foreach (var name in new[] { "Train.Core.ContractSupport", "Train.Content.ContractSupport" })
                context.LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(path)!, name + ".dll"));
            var assembly = context.LoadFromAssemblyPath(path);
            if (ObserveHostWindow)
            {
                await (Task)assembly.GetType("Train.Engine.Tests.HostWindowObservation")!
                    .GetMethod("Run")!.Invoke(null, [this])!;
                GetTree().Quit();
                return;
            }
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
