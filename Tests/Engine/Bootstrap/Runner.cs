using System.Runtime.Loader;
using System.Reflection;
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
            var path = System.Environment.GetEnvironmentVariable("TRAIN_ENGINE_TEST_ASSEMBLY")!;
            var context = AssemblyLoadContext.GetLoadContext(GetType().Assembly)!;
            var resolver = new AssemblyDependencyResolver(path);
            // 测试专用依赖不在宿主 deps.json 中；按测试程序集自身的依赖清单加载。
            Assembly? Resolve(AssemblyLoadContext loadContext, AssemblyName name) =>
                resolver.ResolveAssemblyToPath(name) is { } dependency ? loadContext.LoadFromAssemblyPath(dependency) : null;
            context.Resolving += Resolve;
            try
            {
                var assembly = context.LoadFromAssemblyPath(path);
                GoTest.Adapter = new ReportAdapter(System.Environment.GetEnvironmentVariable("TRAIN_ENGINE_REPORT")!);
                GoTest.TimeoutMilliseconds = 60000;
                await GoTest.RunTests(assembly, this, TestEnvironment.From(OS.GetCmdlineUserArgs()));
            }
            finally { context.Resolving -= Resolve; }
        }
        catch (Exception exception)
        {
            GD.PrintErr(exception);
            GetTree().Quit(1);
        }
    }
}
