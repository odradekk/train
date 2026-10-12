
using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Simulation;

namespace Train.Core.Tests.Simulation;

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
        var session = new SimulationSession(before, new ScriptedCalculations(), new ResourceCommandCodec());
        AssertState(before, session.GetState());
    }

    [TestMethod]
    public void Advancing_zero_steps_does_not_create_a_transaction()
    {
        var before = new ResourceState();
        var session = new SimulationSession(before, new ScriptedCalculations
        {
            Step = (_, _) => throw new InvalidOperationException("零步不应计算。")
        }, new ResourceCommandCodec());
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
        var session = new SimulationSession(before, new ScriptedCalculations(), new ResourceCommandCodec());
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
        session = new SimulationSession(before, calculations, new ResourceCommandCodec());
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
        var batch = new SimulationSession(before, new ScriptedCalculations(), new ResourceCommandCodec());
        var individual = new SimulationSession(before, new ScriptedCalculations(), new ResourceCommandCodec());
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
        }, new ResourceCommandCodec());
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
        }, new ResourceCommandCodec());
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
        }, new ResourceCommandCodec());
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
        var session = new SimulationSession(before, calculations, new ResourceCommandCodec());
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
        var session = new SimulationSession(before, calculations, new ResourceCommandCodec());

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
            new CommandDecision.Execute(new Transition(expected, NoEvents))), new ResourceCommandCodec());
        session.Advance(1);
        var result = (SubmissionResult.Completed)session.Submit(new NoChangeCommand(), CommandSource.Automation);

        Assert.AreEqual(new CommandExecution(3, new SimulationPosition(61, 1), 64,
            CommandSource.Automation, CommandOutcome.Executed), result.Execution);
        AssertState(expected, session.GetState());
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void A_new_fault_returns_original_diagnostics_once_without_changing_authority(bool command)
    {
        var states = new ResourceStateCodec();
        AuthoritativeCheckpoint? firstCheckpoint = null;
        string? firstLogHash = null;
        foreach (var message in new[] { "诊断消息甲。", "诊断消息乙。" })
        {
            var exception = new InvalidOperationException(message, new IOException("内部诊断。"));
            var calculations = new ScriptedCalculations
            {
                Step = (_, _) => throw exception,
                Command = (_, _, _) => throw exception
            };
            var session = new SimulationSession(new ResourceState(), calculations, new ResourceCommandCodec());
            var diagnostic = command
                ? ((SubmissionResult.Completed)session.Submit(new NoChangeCommand(), CommandSource.Console)).Diagnostic
                : session.Advance(1).Diagnostic;

            Assert.IsNotNull(diagnostic);
            Assert.AreSame(exception, diagnostic.Exception);
            Assert.AreEqual(message, diagnostic.Exception.Message);
            Assert.IsNotNull(diagnostic.Exception.StackTrace);
            Assert.AreEqual("内部诊断。", diagnostic.Exception.InnerException!.Message);
            Assert.AreEqual(session.GetState().Fault, diagnostic.Fault);
            Assert.IsNull(session.Advance(1).Diagnostic);
            Assert.IsInstanceOfType(session.Submit(new NoChangeCommand(), CommandSource.Ui),
                typeof(SubmissionResult.NotAccepted));

            var checkpoint = states.CreateCheckpoint(session.GetState());
            var log = session.GetCommandLog();
            var logHash = log.Entries.IsEmpty ? log.PrefixSha256 : log.Entries[^1].ChainSha256;
            if (firstCheckpoint is null)
            {
                firstCheckpoint = checkpoint;
                firstLogHash = logHash;
            }
            else
            {
                Assert.AreEqual(firstCheckpoint, checkpoint);
                Assert.AreEqual(firstLogHash, logHash);
            }
            var restored = (DeterminismResult<SimulationSession>.Success)states.Restore(
                states.Capture(session.GetState()), calculations, new ResourceCommandCodec());
            Assert.IsNull(restored.Value.Advance(1).Diagnostic);
        }
    }

    [TestMethod]
    public void A_command_rejection_publishes_only_identity_counters_and_no_events()
    {
        var before = new ResourceState { NextEntityId = 7 };
        var expected = before with { NextCommandId = 2, BoundarySequence = 1, TransactionCount = 1 };
        var session = new SimulationSession(before, ScriptedCalculations.WithCommands(
            new CommandDecision.Reject(new Transition(expected, NoEvents), "requirements_not_met")), new ResourceCommandCodec());
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
        }, new ResourceCommandCodec());
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
            new CommandDecision.Execute(new Transition(invalid, ImmutableArray.Create<DomainEvent>(new MarkerEvent(1))))), new ResourceCommandCodec());
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
        var session = new SimulationSession(before, new ScriptedCalculations(), new ResourceCommandCodec());
        var result = session.Submit(new NoChangeCommand(), CommandSource.Automation);

        Assert.IsInstanceOfType(result, typeof(SubmissionResult.NotAccepted));
        AssertState(before, session.GetState());
    }

    [TestMethod]
    public void Resource_calculations_run_as_complete_steps_and_commands()
    {
        var before = new ResourceState
        {
            EntityIds = ImmutableSortedSet.Create(10UL, 20UL), NextEntityId = 21,
            Demands = ImmutableArray.Create(new ResourceDemand(20, 1, 0, 4), new ResourceDemand(10, 1, 0, 3))
        };
        var session = new SimulationSession(before, new ResourceCalculations(), new ResourceCommandCodec());
        var advanced = session.Advance(2);
        var first = (SubmissionResult.Completed)session.Submit(new CreateEntityCommand(), CommandSource.Ui);
        session.Submit(new DeleteEntityCommand(21), CommandSource.Ui);
        var rejected = (SubmissionResult.Completed)session.Submit(new DeleteEntityCommand(21), CommandSource.Ui);
        var last = (SubmissionResult.Completed)session.Submit(new CreateEntityCommand(), CommandSource.Ui);

        Assert.AreEqual(2UL, advanced.StepsAdvanced);
        Assert.AreEqual(CommandOutcome.Executed, first.Execution.Outcome);
        Assert.AreEqual(CommandOutcome.Rejected, rejected.Execution.Outcome);
        Assert.AreEqual("target_missing", rejected.Execution.Reason);
        Assert.AreEqual(CommandOutcome.Executed, last.Execution.Outcome);
        AssertState(before with
        {
            Stock = 0, Holdings = ImmutableDictionary<ulong, ulong>.Empty.Add(10, 3).Add(20, 2),
            EntityIds = ImmutableSortedSet.Create(10UL, 20UL, 22UL), NextEntityId = 23,
            CompletedSteps = 2, BoundarySequence = 4, NextCommandId = 5, TransactionCount = 6
        }, session.GetState());
        Assert.AreEqual(5UL, before.Stock);
        CollectionAssert.AreEqual(new[] { 10UL, 20UL }, before.EntityIds.ToArray());
    }

    [TestMethod]
    [DataRow(SimulationPhase.Evaluate)]
    [DataRow(SimulationPhase.Resolve)]
    [DataRow(SimulationPhase.Commit)]
    public void An_unclassified_pipeline_exception_freezes_at_its_actual_phase(SimulationPhase phase)
    {
        var before = new ResourceState();
        var session = new SimulationSession(before, new FailingPipeline(phase), new ResourceCommandCodec());
        var result = session.Advance(10);

        Assert.AreEqual(3UL, result.StepsAdvanced);
        Assert.IsNotNull(result.StateAfter.Fault);
        Assert.AreEqual("calculation_failed", result.StateAfter.Fault.Code);
        Assert.AreEqual(phase, result.StateAfter.Fault.Phase);
        Assert.AreEqual(new TransactionContext(4, SimulationOperation.Step, new SimulationPosition(4, 0)),
            result.StateAfter.Fault.Context);
        AssertState(before with { CompletedSteps = 3, TransactionCount = 3, Fault = result.StateAfter.Fault },
            session.GetState());
        Assert.IsTrue(result.Events.IsEmpty);
    }

    [TestMethod]
    [DataRow(SimulationPhase.Evaluate)]
    [DataRow(SimulationPhase.Resolve)]
    [DataRow(SimulationPhase.Commit)]
    public void A_typed_pipeline_exception_preserves_its_code_and_entity_with_the_actual_phase(SimulationPhase phase)
    {
        var before = new ResourceState { NextEntityId = 8, EntityIds = ImmutableSortedSet.Create(7UL) };
        var session = new SimulationSession(before, new FailingPipeline(phase, SimulationPhase.Boundary), new ResourceCommandCodec());
        var result = session.Advance(10);

        Assert.AreEqual(3UL, result.StepsAdvanced);
        Assert.IsNotNull(result.StateAfter.Fault);
        Assert.AreEqual("test_failure", result.StateAfter.Fault.Code);
        Assert.AreEqual(7UL, result.StateAfter.Fault.EntityId);
        Assert.AreEqual(phase, result.StateAfter.Fault.Phase);
        Assert.AreEqual(new TransactionContext(4, SimulationOperation.Step, new SimulationPosition(4, 0)),
            result.StateAfter.Fault.Context);
        AssertState(before with { CompletedSteps = 3, TransactionCount = 3, Fault = result.StateAfter.Fault },
            session.GetState());
        Assert.IsTrue(result.Events.IsEmpty);
    }

    [TestMethod]
    public void A_typed_command_exception_is_recorded_at_the_command_phase()
    {
        var before = new ResourceState { NextEntityId = 8, EntityIds = ImmutableSortedSet.Create(7UL) };
        var session = new SimulationSession(before, new ScriptedCalculations
        {
            Command = (_, _, _) => throw new SimulationCalculationException(
                "test_failure", SimulationPhase.Commit, 7, new InvalidOperationException("诊断消息。"))
        }, new ResourceCommandCodec());
        var result = (SubmissionResult.Completed)session.Submit(new CreateEntityCommand(), CommandSource.Ui);

        Assert.AreEqual(CommandOutcome.Faulted, result.Execution.Outcome);
        Assert.IsNotNull(result.StateAfter.Fault);
        Assert.AreEqual("test_failure", result.StateAfter.Fault.Code);
        Assert.AreEqual(7UL, result.StateAfter.Fault.EntityId);
        Assert.AreEqual(SimulationPhase.Command, result.StateAfter.Fault.Phase);
        Assert.AreEqual(new TransactionContext(1, SimulationOperation.Command, new SimulationPosition(0, 1), 1),
            result.StateAfter.Fault.Context);
        AssertState(before with { Fault = result.StateAfter.Fault }, session.GetState());
        Assert.IsTrue(result.Events.IsEmpty);
    }

    [TestMethod]
    [DataRow("stock")]
    [DataRow("allocator")]
    [DataRow("events")]
    public void A_rejection_cannot_publish_business_changes_allocations_or_events(string change)
    {
        var before = new ResourceState { NextEntityId = 7 };
        var candidate = before with { NextCommandId = 2, BoundarySequence = 1, TransactionCount = 1 };
        candidate = change switch
        {
            "stock" => candidate with { Stock = 0 },
            "allocator" => candidate with { NextEntityId = 8 },
            _ => candidate
        };
        var events = change == "events" ? ImmutableArray.Create<DomainEvent>(new MarkerEvent(1)) : NoEvents;
        var session = new SimulationSession(before, ScriptedCalculations.WithCommands(
            new CommandDecision.Reject(new Transition(candidate, events), "requirements_not_met")), new ResourceCommandCodec());
        var result = (SubmissionResult.Completed)session.Submit(new CreateThenRejectCommand(), CommandSource.Ui);

        Assert.AreEqual(CommandOutcome.Faulted, result.Execution.Outcome);
        Assert.IsNotNull(result.StateAfter.Fault);
        Assert.AreEqual(SimulationPhase.Validation, result.StateAfter.Fault.Phase);
        AssertState(before with { Fault = result.StateAfter.Fault }, session.GetState());
        Assert.IsTrue(result.Events.IsEmpty);
    }

    [TestMethod]
    [DataRow("step")]
    [DataRow("transaction")]
    public void Advancing_commits_the_last_representable_step_then_stops_without_overflow(string counter)
    {
        var before = counter == "step"
            ? new ResourceState { CompletedSteps = ulong.MaxValue - 1 }
            : new ResourceState { TransactionCount = ulong.MaxValue - 1 };
        var session = new SimulationSession(before, new ResourceCalculations(), new ResourceCommandCodec());
        var first = session.Advance(3);
        var next = session.Advance(1);
        var expected = counter == "step"
            ? before with { CompletedSteps = ulong.MaxValue, TransactionCount = 1 }
            : before with { CompletedSteps = 1, TransactionCount = ulong.MaxValue };

        Assert.AreEqual(1UL, first.StepsAdvanced);
        Assert.AreEqual(0UL, next.StepsAdvanced);
        AssertState(expected, first.StateAfter);
        AssertState(expected, session.GetState());
        Assert.IsNull(session.GetState().Fault);
        Assert.IsTrue(next.Events.IsEmpty);
    }

    [TestMethod]
    [DataRow("command")]
    [DataRow("sequence")]
    [DataRow("transaction")]
    public void The_last_available_command_commits_before_the_next_is_refused(string counter)
    {
        var before = counter switch
        {
            "command" => new ResourceState { NextCommandId = ulong.MaxValue - 1 },
            "sequence" => new ResourceState { BoundarySequence = ulong.MaxValue - 1 },
            _ => new ResourceState { TransactionCount = ulong.MaxValue - 1 }
        };
        var expected = counter switch
        {
            "command" => before with { NextCommandId = ulong.MaxValue, BoundarySequence = 1, TransactionCount = 1 },
            "sequence" => before with { NextCommandId = 2, BoundarySequence = ulong.MaxValue, TransactionCount = 1 },
            _ => before with { NextCommandId = 2, BoundarySequence = 1, TransactionCount = ulong.MaxValue }
        };
        var session = new SimulationSession(before, new ResourceCalculations(), new ResourceCommandCodec());
        var first = (SubmissionResult.Completed)session.Submit(new NoChangeCommand(), CommandSource.Ui);
        var next = session.Submit(new NoChangeCommand(), CommandSource.Ui);

        Assert.AreEqual(CommandOutcome.Executed, first.Execution.Outcome);
        AssertState(expected, first.StateAfter);
        Assert.IsInstanceOfType(next, typeof(SubmissionResult.NotAccepted));
        AssertState(expected, session.GetState());
    }

    [TestMethod]
    public void Reentering_a_mutation_from_calculation_rolls_back_and_freezes_the_outer_transaction()
    {
        var before = new ResourceState();
        SimulationSession? session = null;
        session = new SimulationSession(before, new ScriptedCalculations
        {
            Step = (state, context) =>
            {
                session!.Submit(new NoChangeCommand(), CommandSource.Ui);
                return ScriptedCalculations.StepResult(state, context);
            },
            Command = (_, _, _) => new CommandDecision.Execute(new Transition(
                before with { NextCommandId = 2, BoundarySequence = 1, TransactionCount = 1 }, NoEvents))
        }, new ResourceCommandCodec());
        var result = session.Advance(1);

        Assert.AreEqual(0UL, result.StepsAdvanced);
        Assert.IsNotNull(result.StateAfter.Fault);
        AssertState(before with { Fault = result.StateAfter.Fault }, session.GetState());
        Assert.IsTrue(result.Events.IsEmpty);
    }

    private sealed class FailingPipeline(SimulationPhase failingPhase, SimulationPhase? reportedPhase = null)
        : SimulationCalculations
    {
        public override StepRequests Evaluate(SimulationState state, TransactionContext context)
        {
            FailAt(SimulationPhase.Evaluate, context);
            return new ResourceRequests(ImmutableArray<ResourceDemand>.Empty);
        }

        public override ResolvedStep Resolve(SimulationState state, StepRequests requests, TransactionContext context)
        {
            FailAt(SimulationPhase.Resolve, context);
            return new ResourcePlan(ImmutableArray<ResourceGrant>.Empty);
        }

        public override SimulationState Commit(SimulationState state, ResolvedStep plan, TransactionContext context)
        {
            FailAt(SimulationPhase.Commit, context);
            return state;
        }

        public override CommandDecision ComputeCommand(
            SimulationState state, CoreCommand command, TransactionContext context) =>
            throw new InvalidOperationException("本例只计算模拟步。");

        private void FailAt(SimulationPhase phase, TransactionContext context)
        {
            if (phase == failingPhase && context.Position.CompletedSteps == 4)
            {
                if (reportedPhase is { } reported)
                    throw new SimulationCalculationException("test_failure", reported, 7,
                        new InvalidOperationException("不进入权威状态的诊断消息。"));
                throw new InvalidOperationException("不进入权威状态的诊断消息。");
            }
        }
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
