using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
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
        var discovered = content.Discover(installation, files);
        if (discovered is not ContentResult<ImmutableArray<PackageStatus>>.Success packages)
            throw new InvalidOperationException($"内容发现失败：{discovered}");
        installation = installation with
        {
            EnabledMods = packages.Value.Where(package => package.State == PackageState.Disabled)
                .Select(package => package.Metadata!.Id).ToImmutableArray()
        };
        return new(Train.Core.CoreFactory.Create(), content, files, new GodotAssetInspector(dispatcher),
            installation, logger, sessionOptions: new(Identity(), OS.IsDebugBuild(), 1));
    }

    internal static IScenesFactory CreateScenes() => Train.Scenes.ScenesFactory.Create();

    private static RuntimeIdentity Identity()
    {
        var manifest = new StringBuilder();
        foreach (var assembly in new[] { typeof(App).Assembly, typeof(ICoreFactory).Assembly,
            typeof(IContentFactory).Assembly, typeof(IScenesFactory).Assembly,
            typeof(Train.Core.CoreFactory).Assembly, typeof(Train.Content.ContentFactory).Assembly,
            typeof(Train.Scenes.ScenesFactory).Assembly })
            // Godot 可从内存加载程序集，Assembly.Location 此时为空。
            manifest.Append(assembly.GetName().Name).Append(':')
                .Append(assembly.ManifestModule.ModuleVersionId).Append('\n');
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.ToString()))),
            "core-session-v1", RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(), RuntimeInformation.FrameworkDescription);
    }
}
