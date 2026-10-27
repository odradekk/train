using System.Reflection;
using System.Runtime.Loader;
using Godot;

namespace Train.Tests.Engine.Bootstrap;

public partial class ScenePreview : Node
{
    private AssemblyLoadContext? _context;
    private AssemblyDependencyResolver? _resolver;

    public override async void _Ready()
    {
        try
        {
            // 根窗口的 TreeExiting 晚于所有子节点的 _ExitTree，在 CSharpLanguage::finalize 前排空终结器以避免竞争。
            // 等待后不再触发 GC，让随后失去引用的包装对象仍能由 Godot 的弱引用跟踪器释放。
            GetTree().Root.TreeExiting += static () => { GC.Collect(); GC.WaitForPendingFinalizers(); };
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var path = ProjectSettings.GlobalizePath("res://.godot/mono/temp/bin/Debug/Train.Engine.Tests.dll");
            _context = AssemblyLoadContext.GetLoadContext(GetType().Assembly)!;
            _resolver = new(path);
            _context.Resolving += Resolve;
            var assembly = _context.LoadFromAssemblyPath(path);
            void Start(string type) => assembly.GetType("Train.Engine.Tests." + type, throwOnError: true)!
                .GetMethod("Start", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [this]);
            var menu = new CanvasLayer { Name = "PreviewSelection" };
            AddChild(menu);
            var buttons = new VBoxContainer { Position = new(32, 32) };
            menu.AddChild(buttons);
            foreach (var (name, text) in new[] { ("WorldScenePreview", "显示帧预览"), ("InspectionLightingPreview", "检查光照预览"), ("DeveloperToolsPreview", "开发检查预览"), ("EditorPreview", "玩家编辑器预览"), ("NodeTransitionPreview", "节点列车帧序列"), ("ProductionNodeCapture", "生产 App 逐帧取证") })
            {
                var button = new Button { Name = name, Text = text, CustomMinimumSize = new(220, 48) };
                buttons.AddChild(button);
                button.Pressed += () =>
                {
                    menu.QueueFree();
                    try { Start(name); }
                    catch (Exception exception) { GD.PrintErr(exception); GetTree().Quit(1); }
                };
            }
        }
        catch (Exception exception) { GD.PrintErr(exception); GetTree().Quit(1); }
    }

    private Assembly? Resolve(AssemblyLoadContext context, AssemblyName name) =>
        _resolver!.ResolveAssemblyToPath(name) is { } path ? context.LoadFromAssemblyPath(path) : null;

    public override void _ExitTree()
    {
        if (_context is not null) _context.Resolving -= Resolve;
    }
}
