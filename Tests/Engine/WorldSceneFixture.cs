using Train.App;
using Train.App.Logging;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;

namespace Train.Engine.Tests;

internal static class WorldSceneFixture
{
    internal static FixedCoreReadView EmptyWorld(SimulationPosition position) => new(position, HostCoreScenario.Input)
    { World = new(1, new("fixture:railway"), 1, 1, [], [], [], [], []) };

    internal static HostCoordinator CreateHost(ScriptedCoreSession session, FixedContentView content,
        TrainLogger logger, Func<ContentResult<IContentCandidate>>? reload = null,
        Func<ScriptedCoreSession>? restore = null)
    {
        var capture = new FixedCoreCapture(new(new(0, 0), "fixed", "fixed"), HostCoreScenario.References,
            "opaque", HostCoreScenario.Input, new(1));
        session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Success(capture));
        var core = new ScriptedCoreFactory
        {
            DecodeStateCall = _ => new CoreResult<ICoreCapture>.Success(capture),
            PrepareRestoreCall = (_, _, _) => new CoreResult<ICoreSessionCandidate>.Success(
                new ScriptedCoreCandidate(restore?.Invoke() ?? session, HostCoreScenario.References))
        };
        var factory = new ScriptedContentFactory
        {
            DecodeCaptureCall = _ => new ContentResult<IContentCapture>.Success(new FixedContentCapture("opaque")),
            PrepareLoadCall = (request, _, _) => request.Purpose is ContentLoadPurpose.Restore
                ? new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(content))
                : reload?.Invoke() ?? throw new InvalidOperationException("UNCONFIGURED: 重载结果。")
        };
        var host = new HostCoordinator(core, factory, new ScriptedContentFiles(), new UnconfiguredAssetInspector(),
            new(new("builtin"), new("mods"), null, "fixture", []), logger);
        host.BeginRestore("opaque", "opaque", new(new("fixture", "fixture", "fixture", "fixture", "fixture"), true, 1));
        return host;
    }
}
