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
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var path = ProjectSettings.GlobalizePath("res://.godot/mono/temp/bin/Debug/Train.Engine.Tests.dll");
            _context = AssemblyLoadContext.GetLoadContext(GetType().Assembly)!;
            _resolver = new(path);
            _context.Resolving += Resolve;
            var assembly = _context.LoadFromAssemblyPath(path);
            assembly.GetType("Train.Engine.Tests.WorldScenePreview", throwOnError: true)!
                .GetMethod("Start", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [this]);
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
