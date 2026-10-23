using System.Collections.Immutable;
using System.Reflection;
using Godot;
using Train.App.Assets;
using Train.App.Editing;
using Train.App.Logging;
using Train.App.Notifications;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.App.Composition;

internal static class HostComposition
{
    internal static HostCoordinator CreateHost(TrainLogger logger, string userData,
        GodotMainThreadDispatcher dispatcher, NotificationPolicy notifications)
    {
        var builtin = new ContentRoot("builtin");
        var mods = new ContentRoot("mods");
        var userLibrary = new ContentRoot("user-library");
        var files = new ContentFiles(dispatcher, new Dictionary<ContentRoot, ContentRootLocation>
        {
            [builtin] = new ContentRootLocation.Resource(AuthoringPaths.BuiltInCoreResourceDirectory),
            [mods] = new ContentRootLocation.Disk(AuthoringPaths.ModsDirectory(userData)),
            [userLibrary] = new ContentRootLocation.Disk(AuthoringPaths.UserLibraryDirectory(userData))
        });
        var content = Train.Content.ContentFactory.Create();
        var installation = new ContentInstallation(builtin, mods, userLibrary,
            ProjectSettings.GetSetting("application/config/version").AsString(), []);
        var discovery = content.Discover(installation, files);
        if (discovery is ContentResult<ImmutableArray<PackageStatus>>.Success discovered)
        {
            installation = installation with
            {
                EnabledMods = discovered.Value.Where(package => package.Root == mods &&
                    package.Metadata is not null && package.State is PackageState.Available or PackageState.Disabled)
                    .Select(package => package.Metadata!.Id).ToImmutableArray()
            };
        }
        else if (discovery is ContentResult<ImmutableArray<PackageStatus>>.Faulted fault)
            logger.Error("content", "content_discovery_failed", fault.Message,
                new Dictionary<string, object?> { ["code"] = fault.Code });
        else if (discovery is ContentResult<ImmutableArray<PackageStatus>>.Failure failure)
            foreach (var diagnostic in failure.Diagnostics)
                logger.Error("content", "content_discovery_failed", diagnostic.Message,
                    new Dictionary<string, object?> { ["code"] = diagnostic.Code });
        var runtime = RuntimeEnvironment.Identity(typeof(HostComposition).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);
        return new(Train.Core.CoreFactory.Create(), content, files, new GodotAssetInspector(dispatcher),
            installation, logger, notifications.Decide, sessionOptions: new(runtime, OS.IsDebugBuild(), 3));
    }

    internal static IScenesFactory CreateScenes() => Train.Scenes.ScenesFactory.Create();
}
