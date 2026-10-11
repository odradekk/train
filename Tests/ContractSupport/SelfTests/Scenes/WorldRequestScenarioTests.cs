// 体素契约支持自检：验证请求身份情景及有限情景替身的行为。
// 相同目标和不同目标分别运行共享断言，并验证未配置调用失败。
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;

namespace Train.ContractSupport.Tests.Scenes;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:scenes-contract-support")]
public sealed class WorldRequestScenarioTests
{
    [TestMethod]
    public async Task NewTargetInvalidatesOldHit()
    {
        await using var session = new FixedWorldRequestSession(WorldRequestScenario.NextTarget);
        WorldRequestScenario.AssertOldHitExpires(session, WorldRequestScenario.Hit,
            WorldRequestScenario.NextTarget);
    }

    [TestMethod]
    public async Task SameTargetInvalidatesOldHit()
    {
        await using var session = new FixedWorldRequestSession(WorldRequestScenario.InitialTarget);
        WorldRequestScenario.AssertOldHitExpires(session, WorldRequestScenario.Hit,
            WorldRequestScenario.InitialTarget);
    }

    [TestMethod]
    public async Task UnconfiguredTargetAndRepeatedRequestFailClearly()
    {
        await using var session = new FixedWorldRequestSession(WorldRequestScenario.NextTarget);
        AssertUnconfigured(() => session.RequestView(WorldRequestScenario.InitialTarget));
        WorldRequestScenario.AssertOldHitExpires(session, WorldRequestScenario.Hit,
            WorldRequestScenario.NextTarget);
        AssertUnconfigured(() => session.RequestView(WorldRequestScenario.NextTarget));
    }

    [TestMethod]
    public async Task UnconfiguredOperationsFailClearly()
    {
        await using var session = new FixedWorldRequestSession(WorldRequestScenario.NextTarget);
        AssertUnconfigured(() => session.Resolve(WorldRequestScenario.Hit with { DistanceCells = 2 }));
        AssertUnconfigured(() => session.Observe(new SceneWorldSnapshot(null!, null!), []));
        AssertUnconfigured(() => session.Present(new SceneFrameRequest(0, null, SceneDebugLayer.None), null));
        WorldRequestScenario.AssertOldHitExpires(session, WorldRequestScenario.Hit,
            WorldRequestScenario.NextTarget);
    }

    private static void AssertUnconfigured(Action action)
    {
        var error = Assert.ThrowsException<InvalidOperationException>(action);
        StringAssert.StartsWith(error.Message, "UNCONFIGURED:");
    }
}
