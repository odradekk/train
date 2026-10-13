using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;

namespace Train.Core.Tests.World;

[TestClass]
public sealed class WorldPreparationTests
{
    [TestMethod]
    public void Configuration_errors_are_detected_before_node_retries()
    {
        var input = WorldFixture.Input with { Configuration = WorldFixture.Input.Configuration with { CandidateProbability = 0 } };
        Assert.ThrowsExactly<ArgumentException>(() => CoreFactory.Create().PrepareNew(input with
            { Configuration = input.Configuration with { TerrainBands = [] } }, WorldFixture.Content, WorldGenerationTests.Options));
        var result = CoreFactory.Create().PrepareNew(input, WorldFixture.Content with { SettlementTemplates = [] }, WorldGenerationTests.Options);
        var problem = ((CoreResult<ICoreSessionCandidate>.Failure)result).Problems.Single();
        Assert.AreEqual("Content.SettlementTemplates", problem.Field);
        CollectionAssert.AreEqual(new[] { "config_invalid", "configuration", "", "0", "0", "1" }, problem.RelatedIdentities.ToArray());
    }

    [TestMethod]
    public void New_stage_diagnostics_do_not_retain_the_previous_candidate()
    {
        foreach (var (configuration, stage) in new[]
        {
            (WorldFixture.Input.Configuration with { SettlementRatio = 0 }, "occupants"),
            (WorldFixture.Input.Configuration with { HostileSettlementRatio = 1 }, "validation")
        })
        {
            var result = CoreFactory.Create().PrepareNew(WorldFixture.Input with { Configuration = configuration },
                WorldFixture.Content, WorldGenerationTests.Options);
            var problem = ((CoreResult<ICoreSessionCandidate>.Failure)result).Problems.Single();
            Assert.AreEqual(stage, problem.RelatedIdentities[1]);
            Assert.AreEqual("", problem.RelatedIdentities[2]);
        }
    }

    [TestMethod]
    public void Start_search_is_metered_and_can_exhaust_the_shared_budget()
    {
        // 单格输入：规范化 1，节点投放/采样/带匹配 3，占据者排序 2，铁路遍历 1，起点排序 1。
        // 起点 BFS 至少再访问一次节点，因此工作量 8 必须在 validation 阶段耗尽。
        var input = WorldFixture.Input with { Configuration = WorldFixture.Input.Configuration with
            { Budget = WorldFixture.Input.Configuration.Budget with { MaximumSearchWork = 8 } } };
        var result = CoreFactory.Create().PrepareNew(input, WorldFixture.Content, WorldGenerationTests.Options);
        var problem = ((CoreResult<ICoreSessionCandidate>.Failure)result).Problems.Single();
        Assert.AreEqual("budget_exhausted", problem.RelatedIdentities[0]);
        Assert.AreEqual("validation", problem.RelatedIdentities[1]);
        Assert.AreEqual("Configuration.Budget.MaximumSearchWork", problem.Field);
    }
}
