using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.World;
using static Train.Core.Tests.World.WorldFixture;

namespace Train.Core.Tests.World;

[TestClass]
public sealed class WorldPreparationTests
{
    [TestMethod]
    public void Configuration_errors_are_detected_before_node_retries()
    {
        var input = Input with { Configuration = Input.Configuration with { CandidateProbability = 0 } };
        Assert.ThrowsExactly<ArgumentException>(() => CoreFactory.Create().PrepareNew(input with
            { Configuration = input.Configuration with { TerrainBands = [] } }, Content, Options));
        var result = CoreFactory.Create().PrepareNew(input, Content with { SettlementTemplates = [] }, Options);
        var problem = ((CoreResult<ICoreSessionCandidate>.Failure)result).Problems.Single();
        Assert.AreEqual("Content.SettlementTemplates", problem.Field);
        Assert.AreEqual("config_invalid", problem.RelatedIdentities[0]);
        Assert.AreEqual("configuration", problem.RelatedIdentities[1]);
        Assert.AreEqual("", problem.RelatedIdentities[2]);
        Assert.AreEqual("0", problem.RelatedIdentities[4]);
    }

    [TestMethod]
    public void New_stage_diagnostics_do_not_retain_the_previous_candidate()
    {
        foreach (var (configuration, stage) in new[]
        {
            (Input.Configuration with { SettlementRatio = 0 }, "occupants"),
            (Input.Configuration with { HostileSettlementRatio = 1 }, "validation")
        })
        {
            var result = CoreFactory.Create().PrepareNew(Input with { Configuration = configuration }, Content, Options);
            var problem = ((CoreResult<ICoreSessionCandidate>.Failure)result).Problems.Single();
            Assert.AreEqual(stage, problem.RelatedIdentities[1]);
            Assert.AreEqual("", problem.RelatedIdentities[2]);
        }
    }

    [TestMethod]
    public void Reachable_traversal_charges_the_shared_budget_after_adjacency_construction()
    {
        var limits = Input.Configuration.Budget;
        var budget = new GenerationBudgetCounter(limits);
        var traversal = WorldTraversal.Build(3, [(0, 1), (1, 2)], budget);
        budget.Work(limits.MaximumSearchWork - budget.UsedSearchWork);
        var failure = Assert.ThrowsExactly<GenerationFailure>(() => traversal.Reachable(0));
        Assert.AreEqual("budget_exhausted", failure.Category);
        Assert.AreEqual("Configuration.Budget.MaximumSearchWork", failure.Field);
    }
}
