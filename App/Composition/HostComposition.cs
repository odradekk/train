using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Reflection;
using Godot;
using Train.App.Assets;
using Train.App.Logging;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.App.Composition;

internal static class HostComposition
{
    internal static HostCoordinator CreateHost(TrainLogger logger, string userData,
        GodotMainThreadDispatcher dispatcher)
    {
        var builtin = new ContentRoot("builtin");
        var mods = new ContentRoot("mods");
        var files = new ContentFiles(dispatcher, new Dictionary<ContentRoot, ContentRootLocation>
        {
            [builtin] = new ContentRootLocation.Resource("res://builtin/core"),
            [mods] = new ContentRootLocation.Disk(Path.Combine(userData, "mods"))
        });
        var content = Train.Content.ContentFactory.Create();
        var installation = new ContentInstallation(builtin, mods, null,
            ProjectSettings.GetSetting("application/config/version").AsString(), []);
        var discovery = content.Discover(installation, files);
        if (discovery is not ContentResult<ImmutableArray<PackageStatus>>.Success discovered)
            throw new InvalidOperationException($"内容发现失败：{discovery}");
        installation = installation with
        {
            EnabledMods = discovered.Value.Where(package => package.Root == mods &&
                package.Metadata is not null && package.State is PackageState.Available or PackageState.Disabled)
                .Select(package => package.Metadata!.Id).ToImmutableArray()
        };
        var runtime = new RuntimeIdentity(typeof(HostComposition).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion, "core-session-v1",
            RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription);
        return new(Train.Core.CoreFactory.Create(), content, files, new GodotAssetInspector(dispatcher),
            installation, logger, sessionOptions: new(runtime, OS.IsDebugBuild(), 3));
    }

    internal static IScenesFactory CreateScenes() => Train.Scenes.ScenesFactory.Create();
}
