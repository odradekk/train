// Train.Tests.Core.Simulation 验证会话的完整边界、身份分配与原子发布契约。
// 领域计算使用预设结果或异常；期望值独立写出，不执行真实世界、存档或 Godot。

using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Simulation;

namespace Train.Tests.Core.Simulation;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:simulation")]
public sealed class SimulationSessionTests
{
    private static readonly ImmutableArray<DomainEvent> NoEvents = ImmutableArray<DomainEvent>.Empty;

    [TestMethod]
    public void Query_returns_the_supplied_complete_starting_boundary()
    {
        var before = new ResourceState
        {
            CompletedSteps = 60, BoundarySequence = 2, NextCommandId = 3, TransactionCount = 62
        };
        var session = new SimulationSession(before, new ScriptedCalculations());
        AssertState(before, session.GetState());
    }

    [TestMethod]
    public void Advancing_zero_steps_does_not_create_a_transaction()
    {
        var before = new ResourceState();
        var session = new SimulationSession(before, new ScriptedCalculations
        {
            Step = (_, _) => throw new InvalidOperationException("零步不应计算。")
        });
        var result = session.Advance(0);

        Assert.AreEqual(0UL, result.StepsAdvanced);
        AssertState(before, result.StateAfter);
        AssertState(before, session.GetState());
        Assert.IsTrue(result.Events.IsEmpty);
    }

    [TestMethod]
    public void A_complete_step_resets_the_boundary_sequence_and_uses_one_transaction()
    {
        var before = new ResourceState
        {
            CompletedSteps = 60, BoundarySequence = 2, NextCommandId = 3, TransactionCount = 62
        };
        var session = new SimulationSession(before, new ScriptedCalculations());
        var result = session.Advance(1);

        Assert.AreEqual(1UL, result.StepsAdvanced);
        AssertState(before with { CompletedSteps = 61, BoundarySequence = 0, TransactionCount = 63 },
            result.StateAfter);
        AssertState(result.StateAfter, session.GetState());
        Assert.IsTrue(result.Events.IsEmpty);
    }

    [TestMethod]
    public void A_candidate_is_not_visible_until_published_and_old_snapshots_remain_unchanged()
    {
        var before = new ResourceState();
        SimulationSession? session = null;
        SimulationState? observedDuringCalculation = null;
        var calculations = new ScriptedCalculations
        {
            Step = (state, context) =>
            {
                var candidate = ((ResourceState)ScriptedCalculations.StepResult(state, context).State) with
                {
                    Stock = 0,
                    Holdings = ImmutableDictionary<ulong, ulong>.Empty.Add(10, 3).Add(20, 2)
                };
                observedDuringCalculation = session!.GetState();
                return new Transition(candidate, ImmutableArray.Create<DomainEvent>(new MarkerEvent(1)));
            }
        };
        session = new SimulationSession(before, calculations);
        var oldSnapshot = session.GetState();
        var result = session.Advance(1);

        Assert.IsNotNull(observedDuringCalculation);
        AssertState(before, observedDuringCalculation);
        AssertState(before, oldSnapshot);
        Assert.AreEqual(0UL, ((ResourceState)session.GetState()).Stock);
        Assert.AreEqual(3UL, ((ResourceState)session.GetState()).Holdings[10]);
        Assert.AreEqual(2UL, ((ResourceState)session.GetState()).Holdings[20]);
        CollectionAssert.AreEqual(new DomainEvent[] { new MarkerEvent(1) }, result.Events.ToArray());
    }

    [TestMethod]
    public void Batching_steps_and_advancing_individually_reach_the_same_expected_state()
    {
        var before = new ResourceState();
        var batch = new SimulationSession(before, new ScriptedCalculations());
        var individual = new SimulationSession(before, new ScriptedCalculations());
        var result = batch.Advance(3);
        individual.Advance(1);
        individual.Advance(1);
        individual.Advance(1);
        var expected = before with { CompletedSteps = 3, TransactionCount = 3 };

        Assert.AreEqual(3UL, result.StepsAdvanced);
        AssertState(expected, batch.GetState());
        AssertState(expected, individual.GetState());
    }

    [TestMethod]
    [DataRow(SimulationPhase.Evaluate)]
    [DataRow(SimulationPhase.Resolve)]
    [DataRow(SimulationPhase.Commit)]
    public void A_step_fault_preserves_earlier_steps_and_their_events(SimulationPhase phase)
    {
        var before = new ResourceState();
        var session = new SimulationSession(before, new ScriptedCalculations
        {
            Step = (state, context) =>
            {
                if (context.Position.CompletedSteps == 4)
                    throw new SimulationCalculationException("test_failure", phase);
                return ScriptedCalculations.StepResult(state, context) with
                {
                    Events = ImmutableArray.Create<DomainEvent>(new MarkerEvent(context.Position.CompletedSteps))
                };
            }
        });
        var result = session.Advance(10);

        Assert.AreEqual(3UL, result.StepsAdvanced);
        Assert.IsNotNull(result.StateAfter.Fault);
        Assert.AreEqual(phase, result.StateAfter.Fault.Phase);
        Assert.AreEqual(new TransactionContext(4, SimulationOperation.Step, new SimulationPosition(4, 0)),
            result.StateAfter.Fault.Context);
        AssertState(before with { CompletedSteps = 3, TransactionCount = 3, Fault = result.StateAfter.Fault },
            result.StateAfter);
        CollectionAssert.AreEqual(new DomainEvent[] { new MarkerEvent(1), new MarkerEvent(2), new MarkerEvent(3) },
            result.Events.ToArray());
    }

    [TestMethod]
    public void An_invalid_step_candidate_rolls_back_business_data_and_discards_its_events()
    {
        var before = new ResourceState();
        var session = new SimulationSession(before, new ScriptedCalculations
        {
            Step = (state, context) =>
            {
                var result = ScriptedCalculations.StepResult(state, context);
                if (context.Position.CompletedSteps == 4)
                    result = result with { State = ((ResourceState)result.State) with { Stock = 0, NextEntityId = 0 } };
                return result with
                {
                    Events = ImmutableArray.Create<DomainEvent>(new MarkerEvent(context.Position.CompletedSteps))
                };
            }
        });
        var result = session.Advance(10);

        Assert.AreEqual(3UL, result.StepsAdvanced);
        Assert.IsNotNull(result.StateAfter.Fault);
        Assert.AreEqual(SimulationPhase.Validation, result.StateAfter.Fault.Phase);
        AssertState(before with { CompletedSteps = 3, TransactionCount = 3, Fault = result.StateAfter.Fault },
            session.GetState());
        CollectionAssert.AreEqual(new DomainEvent[] { new MarkerEvent(1), new MarkerEvent(2), new MarkerEvent(3) },
            result.Events.ToArray());
    }

    [TestMethod]
    public void A_frozen_session_neither_advances_nor_accepts_another_command()
    {
        var before = new ResourceState
        {
            Fault = new SimulationFault("test_failure", SimulationPhase.Commit,
                new TransactionContext(1, SimulationOperation.Step, new SimulationPosition(1, 0)))
        };
        var session = new SimulationSession(before, new ScriptedCalculations
        {
            Step = (_, _) => throw new InvalidOperationException("冻结后不应计算。")
        });
        var advanced = session.Advance(1);
        var submitted = session.Submit(new NoChangeCommand(), CommandSource.Ui);

        Assert.AreEqual(0UL, advanced.StepsAdvanced);
        Assert.IsTrue(advanced.Events.IsEmpty);
        Assert.IsInstanceOfType(submitted, typeof(SubmissionResult.NotAccepted));
        AssertState(before, session.GetState());
    }

    [TestMethod]
    [DataRow(CommandSource.Ui)]
    [DataRow(CommandSource.Console)]
    [DataRow(CommandSource.Automation)]
    public void All_command_sources_use_the_same_identity_and_publication_rules(CommandSource source)
    {
        var before = new ResourceState();
        var candidate = before with { NextCommandId = 2, BoundarySequence = 1, TransactionCount = 1 };
        var events = ImmutableArray.Create<DomainEvent>(new MarkerEvent(1));
        var calculations = ScriptedCalculations.WithCommands(
            new CommandDecision.Execute(new Transition(candidate, events)));
        var session = new SimulationSession(before, calculations);
        var result = (SubmissionResult.Completed)session.Submit(new NoChangeCommand(), source);

        Assert.AreEqual(new CommandExecution(1, new SimulationPosition(0, 1), 1, source, CommandOutcome.Executed),
            result.Execution);
        AssertState(candidate, result.StateAfter);
        AssertState(candidate, session.GetState());
        CollectionAssert.AreEqual(new DomainEvent[] { new MarkerEvent(1) }, result.Events.ToArray());
        var input = calculations.CommandInputs.Single();
        AssertState(before, input.Before);
        Assert.AreEqual(new NoChangeCommand(), input.Command);
        Assert.AreEqual(new TransactionContext(1, SimulationOperation.Command, new SimulationPosition(0, 1), 1),
            input.Context);
    }

    [TestMethod]
    public void A_missing_target_rejection_keeps_previous_commands_and_allows_the_next_one()
    {
        var before = new ResourceState { NextEntityId = 11, EntityIds = ImmutableSortedSet.Create(10UL) };
        var deleted = before with
        {
            EntityIds = ImmutableSortedSet<ulong>.Empty,
            NextCommandId = 2, BoundarySequence = 1, TransactionCount = 1
        };
        var rejected = deleted with { NextCommandId = 3, BoundarySequence = 2, TransactionCount = 2 };
        var created = rejected with
        {
            EntityIds = ImmutableSortedSet.Create(11UL), NextEntityId = 12,
            NextCommandId = 4, BoundarySequence = 3, TransactionCount = 3
        };
        var calculations = ScriptedCalculations.WithCommands(
            new CommandDecision.Execute(new Transition(deleted, NoEvents)),
            new CommandDecision.Reject(new Transition(rejected, NoEvents), "target_missing"),
            new CommandDecision.Execute(new Transition(created, NoEvents)));
        var session = new SimulationSession(before, calculations);

        var first = (SubmissionResult.Completed)session.Submit(new DeleteEntityCommand(10), CommandSource.Automation);
        var second = (SubmissionResult.Completed)session.Submit(new DeleteEntityCommand(10), CommandSource.Automation);
        var third = (SubmissionResult.Completed)session.Submit(new CreateEntityCommand(), CommandSource.Automation);

        Assert.AreEqual(CommandOutcome.Executed, first.Execution.Outcome);
        Assert.AreEqual(CommandOutcome.Rejected, second.Execution.Outcome);
        Assert.AreEqual("target_missing", second.Execution.Reason);
        Assert.AreEqual(CommandOutcome.Executed, third.Execution.Outcome);
        CollectionAssert.AreEqual(new[] { 1UL, 2UL, 3UL },
            new[] { first.Execution.CommandId, second.Execution.CommandId, third.Execution.CommandId });
        CollectionAssert.AreEqual(new[] { new SimulationPosition(0, 1), new(0, 2), new(0, 3) },
            new[] { first.Execution.Position, second.Execution.Position, third.Execution.Position });
        AssertState(deleted, first.StateAfter);
        AssertState(rejected, second.StateAfter);
        AssertState(created, session.GetState());
        Assert.IsTrue(second.Events.IsEmpty);
        Assert.AreEqual(3, calculations.CommandInputs.Count);
        AssertState(deleted, calculations.CommandInputs[1].Before);
        Assert.AreEqual(new DeleteEntityCommand(10), calculations.CommandInputs[1].Command);
        Assert.AreEqual(new TransactionContext(2, SimulationOperation.Command, new SimulationPosition(0, 2), 2),
            calculations.CommandInputs[1].Context);
        Assert.AreEqual(new CreateEntityCommand(), calculations.CommandInputs[2].Command);
        Assert.AreEqual(new TransactionContext(3, SimulationOperation.Command, new SimulationPosition(0, 3), 3),
            calculations.CommandInputs[2].Context);
    }

    [TestMethod]
    public void A_command_after_a_step_uses_the_new_boundary_and_keeps_global_command_identity()
    {
        var before = new ResourceState
        {
            CompletedSteps = 60, BoundarySequence = 2, NextCommandId = 3, TransactionCount = 62
        };
        var expected = before with
        {
            CompletedSteps = 61, BoundarySequence = 1, NextCommandId = 4, TransactionCount = 64
        };
        var session = new SimulationSession(before, ScriptedCalculations.WithCommands(
            new CommandDecision.Execute(new Transition(expected, NoEvents))));
        session.Advance(1);
        var result = (SubmissionResult.Completed)session.Submit(new NoChangeCommand(), CommandSource.Automation);

        Assert.AreEqual(new CommandExecution(3, new SimulationPosition(61, 1), 64,
            CommandSource.Automation, CommandOutcome.Executed), result.Execution);
        AssertState(expected, session.GetState());
    }

    [TestMethod]
    public void A_committed_entity_id_is_not_reused_after_deletion()
    {
        var before = new ResourceState { NextEntityId = 7 };
        var created = before with
        {
            EntityIds = ImmutableSortedSet.Create(7UL), NextEntityId = 8,
            NextCommandId = 2, BoundarySequence = 1, TransactionCount = 1
        };
        var deleted = created with
        {
            EntityIds = ImmutableSortedSet<ulong>.Empty,
            NextCommandId = 3, BoundarySequence = 2, TransactionCount = 2
        };
        var recreated = deleted with
        {
            EntityIds = ImmutableSortedSet.Create(8UL), NextEntityId = 9,
            NextCommandId = 4, BoundarySequence = 3, TransactionCount = 3
        };
        var session = new SimulationSession(before, ScriptedCalculations.WithCommands(
            new CommandDecision.Execute(new Transition(created, NoEvents)),
            new CommandDecision.Execute(new Transition(deleted, NoEvents)),
            new CommandDecision.Execute(new Transition(recreated, NoEvents))));

        session.Submit(new CreateEntityCommand(), CommandSource.Automation);
        session.Submit(new DeleteEntityCommand(7), CommandSource.Automation);
        session.Submit(new CreateEntityCommand(), CommandSource.Automation);

        AssertState(recreated, session.GetState());
        Assert.AreEqual(7UL, before.NextEntityId);
    }

    [TestMethod]
    public void A_command_rejection_publishes_only_identity_counters_and_no_events()
    {
        var before = new ResourceState { NextEntityId = 7 };
        var expected = before with { NextCommandId = 2, BoundarySequence = 1, TransactionCount = 1 };
        var session = new SimulationSession(before, ScriptedCalculations.WithCommands(
            new CommandDecision.Reject(new Transition(expected, NoEvents), "requirements_not_met")));
        var result = (SubmissionResult.Completed)session.Submit(new CreateThenRejectCommand(), CommandSource.Console);

        Assert.AreEqual(CommandOutcome.Rejected, result.Execution.Outcome);
        Assert.AreEqual("requirements_not_met", result.Execution.Reason);
        AssertState(expected, session.GetState());
        Assert.IsTrue(result.Events.IsEmpty);
        Assert.IsNull(result.StateAfter.Fault);
    }

    [TestMethod]
    public void A_command_exception_restores_the_complete_start_and_freezes()
    {
        var before = new ResourceState
        {
            CompletedSteps = 10, BoundarySequence = 4, NextCommandId = 5,
            NextEntityId = 7, TransactionCount = 14, EntityIds = ImmutableSortedSet.Create(6UL)
        };
        var session = new SimulationSession(before, new ScriptedCalculations
        {
            Command = (_, _, _) => throw new InvalidOperationException("仅供诊断的异常消息。")
        });
        var result = (SubmissionResult.Completed)session.Submit(new CreateEntityCommand(), CommandSource.Console);

        Assert.AreEqual(CommandOutcome.Faulted, result.Execution.Outcome);
        Assert.AreEqual(5UL, result.Execution.CommandId);
        Assert.AreEqual(new SimulationPosition(10, 5), result.Execution.Position);
        Assert.AreEqual(15UL, result.Execution.TransactionId);
        Assert.IsNotNull(result.StateAfter.Fault);
        Assert.AreEqual(SimulationPhase.Command, result.StateAfter.Fault.Phase);
        AssertState(before with { Fault = result.StateAfter.Fault }, session.GetState());
        Assert.IsTrue(result.Events.IsEmpty);
        Assert.IsInstanceOfType(session.Submit(new NoChangeCommand(), CommandSource.Ui),
            typeof(SubmissionResult.NotAccepted));
        AssertState(before with { Fault = result.StateAfter.Fault }, session.GetState());
    }

    [TestMethod]
    public void An_invalid_command_candidate_is_not_published_as_an_expected_rejection()
    {
        var before = new ResourceState();
        var invalid = before with
        {
            Stock = 0, NextEntityId = 0, NextCommandId = 2, BoundarySequence = 1, TransactionCount = 1
        };
        var session = new SimulationSession(before, ScriptedCalculations.WithCommands(
            new CommandDecision.Execute(new Transition(invalid, ImmutableArray.Create<DomainEvent>(new MarkerEvent(1))))));
        var result = (SubmissionResult.Completed)session.Submit(new InvalidCandidateCommand(), CommandSource.Automation);

        Assert.AreEqual(CommandOutcome.Faulted, result.Execution.Outcome);
        Assert.IsNotNull(result.StateAfter.Fault);
        Assert.AreEqual(SimulationPhase.Validation, result.StateAfter.Fault.Phase);
        AssertState(before with { Fault = result.StateAfter.Fault }, session.GetState());
        Assert.IsTrue(result.Events.IsEmpty);
    }

    [TestMethod]
    [DataRow("command")]
    [DataRow("sequence")]
    [DataRow("transaction")]
    public void Identity_exhaustion_refuses_the_whole_command_without_consuming_any_counter(string counter)
    {
        var before = counter switch
        {
            "command" => new ResourceState { NextCommandId = ulong.MaxValue },
            "sequence" => new ResourceState { BoundarySequence = ulong.MaxValue },
            _ => new ResourceState { TransactionCount = ulong.MaxValue }
        };
        var session = new SimulationSession(before, new ScriptedCalculations());
        var result = session.Submit(new NoChangeCommand(), CommandSource.Automation);

        Assert.IsInstanceOfType(result, typeof(SubmissionResult.NotAccepted));
        AssertState(before, session.GetState());
    }

    private static void AssertState(SimulationState expected, SimulationState actual)
    {
        Assert.AreEqual(expected.WorldSeed, actual.WorldSeed);
        Assert.AreEqual(expected.GeneratorVersion, actual.GeneratorVersion);
        Assert.AreEqual(expected.CompletedSteps, actual.CompletedSteps);
        Assert.AreEqual(expected.BoundarySequence, actual.BoundarySequence);
        Assert.AreEqual(expected.NextCommandId, actual.NextCommandId);
        Assert.AreEqual(expected.NextEntityId, actual.NextEntityId);
        Assert.AreEqual(expected.TransactionCount, actual.TransactionCount);
        Assert.AreEqual(expected.Fault, actual.Fault);
        var expectedResource = (ResourceState)expected;
        var actualResource = (ResourceState)actual;
        Assert.AreEqual(expectedResource.Stock, actualResource.Stock);
        CollectionAssert.AreEqual(expectedResource.EntityIds.ToArray(), actualResource.EntityIds.ToArray());
        CollectionAssert.AreEqual(expectedResource.Holdings.OrderBy(p => p.Key).ToArray(),
            actualResource.Holdings.OrderBy(p => p.Key).ToArray());
        CollectionAssert.AreEqual(expectedResource.Demands.OrderBy(d => d.EntityId).ThenBy(d => d.RequestId).ToArray(),
            actualResource.Demands.OrderBy(d => d.EntityId).ThenBy(d => d.RequestId).ToArray());
    }
}
