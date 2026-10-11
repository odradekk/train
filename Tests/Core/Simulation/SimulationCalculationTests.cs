// Train.Core.Tests.Simulation 把各计算用途的固定输入和独立预期输出写成单元测试。
// 预期结果来自独立例子，同时检查共享流水线与资源领域计算。

using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Simulation;

namespace Train.Core.Tests.Simulation;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:simulation")]
public sealed class SimulationCalculationTests
{
    private static readonly TransactionContext FirstStep = new(
        1, SimulationOperation.Step, new SimulationPosition(1, 0));

    [TestMethod]
    public void Positions_compare_by_step_then_sequence()
    {
        var examples = new (SimulationPosition Left, SimulationPosition Right, int Sign)[]
        {
            (new(60, 2), new(60, 3), -1),
            (new(60, 3), new(60, 2), 1),
            (new(60, 2), new(61, 0), -1),
            (new(61, 0), new(60, ulong.MaxValue), 1),
            (new(60, 2), new(60, 2), 0)
        };
        foreach (var (left, right, sign) in examples)
            Assert.AreEqual(sign, Math.Sign(left.CompareTo(right)), $"{left} / {right}");
    }

    [TestMethod]
    public void Evaluate_describes_demand_without_allocating_stock()
    {
        var before = ResourceInput() with { Demands = Demands() };
        var result = (ResourceRequests)new ResourceCalculations().Evaluate(before, FirstStep);

        Assert.AreEqual(2, result.Items.Length);
        Assert.AreEqual(3UL, result.Items.Single(r => r.EntityId == 10).Amount);
        Assert.AreEqual(4UL, result.Items.Single(r => r.EntityId == 20).Amount);
        Assert.AreEqual(5UL, before.Stock);
        Assert.AreEqual(0, before.Holdings.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Resolve_uses_stable_identity_and_preserves_resource_limits(bool reverse)
    {
        var demands = Demands();
        if (reverse)
            demands = demands.Reverse().ToImmutableArray();
        var before = ResourceInput() with { Demands = demands };
        var result = (ResourcePlan)new ResourceCalculations().Resolve(
            before, new ResourceRequests(demands), FirstStep);

        CollectionAssert.AreEqual(new[] { 10UL, 20UL }, result.Grants.Select(g => g.EntityId).ToArray());
        CollectionAssert.AreEqual(new[] { 3UL, 2UL }, result.Grants.Select(g => g.Amount).ToArray());
        Assert.AreEqual(5UL, result.Grants.Aggregate(0UL, (total, g) => total + g.Amount));
        Assert.AreEqual(5UL, before.Stock);
    }

    [TestMethod]
    public void Resolve_applies_gameplay_priority_before_identity()
    {
        var demands = ImmutableArray.Create(
            new ResourceDemand(10, 1, 0, 3), new ResourceDemand(20, 1, -1, 4));
        var result = (ResourcePlan)new ResourceCalculations().Resolve(
            ResourceInput(), new ResourceRequests(demands), FirstStep);

        CollectionAssert.AreEqual(new[] { 20UL, 10UL }, result.Grants.Select(g => g.EntityId).ToArray());
        CollectionAssert.AreEqual(new[] { 4UL, 1UL }, result.Grants.Select(g => g.Amount).ToArray());
    }

    [TestMethod]
    public void Resolve_does_not_partially_grant_a_complete_batch()
    {
        var demands = ImmutableArray.Create(
            new ResourceDemand(10, 1, 0, 3), new ResourceDemand(20, 1, 0, 4, AllowPartial: false));
        var result = (ResourcePlan)new ResourceCalculations().Resolve(
            ResourceInput(), new ResourceRequests(demands), FirstStep);

        // 未获准请求不进入执行计划；剩余的 2 单位保留在库存中。
        CollectionAssert.AreEqual(new[] { new ResourceGrant(10, 1, 3) }, result.Grants.ToArray());
    }

    [TestMethod]
    public void Resolve_keeps_an_empty_resource_pool_empty()
    {
        var before = ResourceInput() with { Stock = 0 };
        var result = (ResourcePlan)new ResourceCalculations().Resolve(
            before, new ResourceRequests(Demands()), FirstStep);

        Assert.IsTrue(result.Grants.IsEmpty);
        Assert.AreEqual(0UL, before.Stock);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Commit_applies_the_granted_plan_and_preserves_the_input(bool grantSecond)
    {
        var before = ResourceInput();
        var plan = new ResourcePlan(ImmutableArray.Create(
            new ResourceGrant(10, 1, 3), new ResourceGrant(20, 1, 2)));
        if (!grantSecond)
            plan = new ResourcePlan(ImmutableArray.Create(new ResourceGrant(10, 1, 3)));
        var result = (ResourceState)new ResourceCalculations().Commit(before, plan, FirstStep);

        Assert.AreEqual(grantSecond ? 0UL : 2UL, result.Stock);
        Assert.AreEqual(3UL, result.Holdings[10]);
        Assert.AreEqual(grantSecond ? 2UL : 0UL, result.Holdings.GetValueOrDefault(20UL));
        Assert.AreEqual(5UL, before.Stock);
        Assert.AreEqual(0, before.Holdings.Count);
        Assert.AreEqual(0UL, result.CompletedSteps);
        Assert.AreEqual(0UL, result.TransactionCount);
    }

    [TestMethod]
    public void Step_computes_a_new_boundary_from_the_same_committed_input()
    {
        var before = ResourceInput() with
        {
            Demands = Demands(), CompletedSteps = 60, BoundarySequence = 2,
            NextCommandId = 3, TransactionCount = 62
        };
        var context = new TransactionContext(63, SimulationOperation.Step, new SimulationPosition(61, 0));
        var calculations = new PipelineExamples(before);
        var result = calculations.ComputeStep(before, context);
        var candidate = (ResourceState)result.State;

        Assert.AreEqual(0UL, candidate.Stock);
        Assert.AreEqual(3UL, candidate.Holdings[10]);
        Assert.AreEqual(2UL, candidate.Holdings[20]);
        Assert.AreEqual(61UL, candidate.CompletedSteps);
        Assert.AreEqual(0UL, candidate.BoundarySequence);
        Assert.AreEqual(63UL, candidate.TransactionCount);
        Assert.AreEqual(3UL, candidate.NextCommandId);
        Assert.AreEqual(5UL, before.Stock);
        Assert.AreEqual(60UL, before.CompletedSteps);
        Assert.IsNull(candidate.Fault);
        foreach (var phase in new[] { SimulationPhase.Evaluate, SimulationPhase.Resolve, SimulationPhase.Commit })
        {
            // 观察各阶段读取的完整事实；不把内部调用次数或对象引用身份作为合同。
            var input = calculations.Inputs[phase];
            var state = (ResourceState)input.Before;
            Assert.AreEqual(5UL, state.Stock);
            Assert.AreEqual(0, state.Holdings.Count);
            Assert.AreEqual(60UL, state.CompletedSteps);
            Assert.AreEqual(2UL, state.BoundarySequence);
            Assert.AreEqual(3UL, state.NextCommandId);
            Assert.AreEqual(21UL, state.NextEntityId);
            Assert.AreEqual(62UL, state.TransactionCount);
            Assert.AreEqual(42UL, state.WorldSeed);
            Assert.AreEqual("test-generator-v1", state.GeneratorVersion);
            CollectionAssert.AreEqual(new[] { 10UL, 20UL }, state.EntityIds.ToArray());
            CollectionAssert.AreEqual(Demands().ToArray(), state.Demands.ToArray());
            Assert.IsNull(state.Fault);
            Assert.AreEqual(context, input.Context);
        }
        CollectionAssert.AreEqual(Demands().ToArray(), calculations.RequestsReceived!.Items.ToArray());
        CollectionAssert.AreEqual(new[] { new ResourceGrant(10, 1, 3), new ResourceGrant(20, 1, 2) },
            calculations.PlanReceived!.Grants.ToArray());
    }

    [TestMethod]
    public void Command_computes_a_created_entity_and_consumes_only_its_own_identity()
    {
        var before = new ResourceState { NextEntityId = 7 };
        var context = new TransactionContext(1, SimulationOperation.Command, new SimulationPosition(0, 1), 1);
        var result = (CommandDecision.Execute)new ResourceCalculations().ComputeCommand(
            before, new CreateEntityCommand(), context);
        var candidate = (ResourceState)result.Transition.State;

        CollectionAssert.AreEqual(new[] { 7UL }, candidate.EntityIds.ToArray());
        Assert.AreEqual(8UL, candidate.NextEntityId);
        Assert.AreEqual(2UL, candidate.NextCommandId);
        Assert.AreEqual(1UL, candidate.BoundarySequence);
        Assert.AreEqual(1UL, candidate.TransactionCount);
        Assert.AreEqual(0UL, candidate.CompletedSteps);
        Assert.AreEqual(5UL, candidate.Stock);
        Assert.AreEqual(0, before.EntityIds.Count);
        Assert.AreEqual(7UL, before.NextEntityId);
    }

    [TestMethod]
    public void Command_rejects_a_missing_target_without_selecting_an_existing_one()
    {
        var before = new ResourceState { NextEntityId = 8, EntityIds = ImmutableSortedSet.Create(7UL) };
        var context = new TransactionContext(1, SimulationOperation.Command, new SimulationPosition(0, 1), 1);
        var result = (CommandDecision.Reject)new ResourceCalculations().ComputeCommand(
            before, new DeleteEntityCommand(6), context);

        Assert.AreEqual("target_missing", result.Reason);
        CollectionAssert.AreEqual(new[] { 7UL }, ((ResourceState)result.Transition.State).EntityIds.ToArray());
        Assert.AreEqual(8UL, result.Transition.State.NextEntityId);
        Assert.AreEqual(2UL, result.Transition.State.NextCommandId);
        Assert.AreEqual(1UL, result.Transition.State.BoundarySequence);
        Assert.AreEqual(1UL, result.Transition.State.TransactionCount);
        Assert.IsTrue(result.Transition.Events.IsEmpty);
        Assert.IsNull(result.Transition.State.Fault);
    }

    [TestMethod]
    public void Command_rejects_entity_id_exhaustion_without_rollover_or_partial_creation()
    {
        var before = new ResourceState { NextEntityId = ulong.MaxValue };
        var context = new TransactionContext(1, SimulationOperation.Command, new SimulationPosition(0, 1), 1);
        var result = (CommandDecision.Reject)new ResourceCalculations().ComputeCommand(
            before, new CreateEntityCommand(), context);

        Assert.AreEqual("entity_id_exhausted", result.Reason);
        Assert.AreEqual(ulong.MaxValue, result.Transition.State.NextEntityId);
        Assert.AreEqual(0, ((ResourceState)result.Transition.State).EntityIds.Count);
        Assert.AreEqual(2UL, result.Transition.State.NextCommandId);
        Assert.AreEqual(1UL, result.Transition.State.BoundarySequence);
        Assert.AreEqual(1UL, result.Transition.State.TransactionCount);
        Assert.IsTrue(result.Transition.Events.IsEmpty);
        Assert.IsNull(result.Transition.State.Fault);
    }

    [TestMethod]
    public void Command_computes_an_expected_rejection_without_allocating_an_entity()
    {
        var before = new ResourceState { NextEntityId = 7 };
        var context = new TransactionContext(1, SimulationOperation.Command, new SimulationPosition(0, 1), 1);
        var result = (CommandDecision.Reject)new ResourceCalculations().ComputeCommand(
            before, new CreateThenRejectCommand(), context);

        Assert.AreEqual("requirements_not_met", result.Reason);
        Assert.AreEqual(7UL, result.Transition.State.NextEntityId);
        Assert.AreEqual(2UL, result.Transition.State.NextCommandId);
        Assert.AreEqual(1UL, result.Transition.State.BoundarySequence);
        Assert.AreEqual(1UL, result.Transition.State.TransactionCount);
        Assert.AreEqual(0, ((ResourceState)result.Transition.State).EntityIds.Count);
        Assert.IsTrue(result.Transition.Events.IsEmpty);
        Assert.AreEqual(1UL, before.NextCommandId);
    }

    [TestMethod]
    public void Entity_allocation_advances_only_the_candidate_and_never_wraps()
    {
        var examples = new (ulong Next, ulong Id, ulong After)[]
        {
            (7, 7, 8), (ulong.MaxValue - 1, ulong.MaxValue - 1, ulong.MaxValue)
        };
        foreach (var (next, id, after) in examples)
        {
            var before = new ResourceState { NextEntityId = next };
            var result = SimulationCalculations.AllocateEntityId(before);
            Assert.IsNotNull(result);
            Assert.AreEqual(id, result.EntityId);
            Assert.AreEqual(after, result.Candidate.NextEntityId);
            Assert.AreEqual(next, before.NextEntityId);
            Assert.AreEqual(5UL, ((ResourceState)result.Candidate).Stock);
            Assert.AreEqual(0UL, result.Candidate.TransactionCount);
        }

        Assert.IsNull(SimulationCalculations.AllocateEntityId(
            new ResourceState { NextEntityId = ulong.MaxValue }));
    }

    [TestMethod]
    public void Invariant_check_accepts_a_complete_step_transition()
    {
        var before = new ResourceState();
        var candidate = before with { CompletedSteps = 1, TransactionCount = 1 };
        Assert.IsNull(new ResourceCalculations().CheckInvariants(before, candidate, FirstStep));
    }

    [TestMethod]
    public void Invariant_check_rejects_an_invalid_entity_allocator()
    {
        var before = new ResourceState();
        var candidate = before with { CompletedSteps = 1, TransactionCount = 1, NextEntityId = 0 };
        var result = new ResourceCalculations().CheckInvariants(before, candidate, FirstStep);

        Assert.IsNotNull(result);
        Assert.AreEqual(nameof(SimulationState.NextEntityId), result.Field);
    }

    [TestMethod]
    [DataRow("seed", nameof(SimulationState.WorldSeed))]
    [DataRow("generator", nameof(SimulationState.GeneratorVersion))]
    [DataRow("step", nameof(SimulationState.CompletedSteps))]
    [DataRow("sequence", nameof(SimulationState.BoundarySequence))]
    [DataRow("command", nameof(SimulationState.NextCommandId))]
    [DataRow("entity", nameof(SimulationState.NextEntityId))]
    [DataRow("transaction", nameof(SimulationState.TransactionCount))]
    [DataRow("position", nameof(TransactionContext.Position))]
    public void Invariants_reject_a_successor_that_changes_identity_or_reuses_counters(string change, string field)
    {
        var before = new ResourceState
        {
            CompletedSteps = 60, BoundarySequence = 2, NextCommandId = 3,
            NextEntityId = 7, TransactionCount = 62
        };
        var candidate = before with { CompletedSteps = 61, BoundarySequence = 0, TransactionCount = 63 };
        var context = new TransactionContext(63, SimulationOperation.Step, new SimulationPosition(61, 0));
        candidate = change switch
        {
            "seed" => candidate with { WorldSeed = 43 },
            "generator" => candidate with { GeneratorVersion = "another-generator" },
            "step" => candidate with { CompletedSteps = 60 },
            "sequence" => candidate with { BoundarySequence = 2 },
            "command" => candidate with { NextCommandId = 4 },
            "entity" => candidate with { NextEntityId = 6 },
            "transaction" => candidate with { TransactionCount = 62 },
            _ => candidate
        };
        if (change == "position")
            context = context with { Position = new SimulationPosition(60, 3) };

        var violation = new ResourceCalculations().CheckInvariants(before, candidate, context);

        Assert.IsNotNull(violation);
        Assert.AreEqual(field, violation.Field);
        Assert.AreEqual(60UL, before.CompletedSteps);
        Assert.AreEqual(7UL, before.NextEntityId);
    }

    [TestMethod]
    public void Fault_state_preserves_all_committed_data_and_adds_the_fault()
    {
        var before = new ResourceState
        {
            CompletedSteps = 3, BoundarySequence = 1, NextCommandId = 2,
            NextEntityId = 8, TransactionCount = 4, EntityIds = ImmutableSortedSet.Create(7UL)
        };
        var fault = new SimulationFault("test_failure", SimulationPhase.Commit,
            new TransactionContext(5, SimulationOperation.Step, new SimulationPosition(4, 0)));
        var result = (ResourceState)SimulationCalculations.CreateFaultState(before, fault);

        Assert.AreEqual(before with { Fault = fault }, result);
        Assert.AreEqual(5UL, result.Stock);
        CollectionAssert.AreEqual(new[] { 7UL }, result.EntityIds.ToArray());
        Assert.IsNull(before.Fault);
    }

    private static ImmutableArray<ResourceDemand> Demands() => ImmutableArray.Create(
        new ResourceDemand(10, 1, 0, 3), new ResourceDemand(20, 1, 0, 4));

    private static ResourceState ResourceInput() => new()
    {
        EntityIds = ImmutableSortedSet.Create(10UL, 20UL), NextEntityId = 21
    };

    /// <summary>各阶段只返回预设数据，观察真实 ComputeStep 的输入及结果转交，不实现领域算法。</summary>
    private sealed class PipelineExamples(ResourceState before) : SimulationCalculations
    {
        public Dictionary<SimulationPhase, (SimulationState Before, TransactionContext Context)> Inputs { get; } = [];
        public ResourceRequests? RequestsReceived { get; private set; }
        public ResourcePlan? PlanReceived { get; private set; }

        public override StepRequests Evaluate(SimulationState state, TransactionContext context)
        {
            Inputs[SimulationPhase.Evaluate] = (state, context);
            return new ResourceRequests(Demands());
        }

        public override ResolvedStep Resolve(
            SimulationState state, StepRequests requests, TransactionContext context)
        {
            Inputs[SimulationPhase.Resolve] = (state, context);
            RequestsReceived = (ResourceRequests)requests;
            return new ResourcePlan(ImmutableArray.Create(new ResourceGrant(10, 1, 3), new ResourceGrant(20, 1, 2)));
        }

        public override SimulationState Commit(
            SimulationState state, ResolvedStep plan, TransactionContext context)
        {
            Inputs[SimulationPhase.Commit] = (state, context);
            PlanReceived = (ResourcePlan)plan;
            return before with
            {
                Stock = 0, Holdings = ImmutableDictionary<ulong, ulong>.Empty.Add(10, 3).Add(20, 2)
            };
        }

        public override CommandDecision ComputeCommand(
            SimulationState state, CoreCommand command, TransactionContext context) =>
            throw new InvalidOperationException("本例只计算模拟步。");
    }
}
