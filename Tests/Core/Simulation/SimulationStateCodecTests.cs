
using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Simulation;

namespace Train.Core.Tests.Simulation;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:simulation")]
public sealed class SimulationStateCodecTests
{
    private const string DomainText = "{\"stock\":9,\"holdings\":[{\"entity_id\":10,\"amount\":3}],\"entity_ids\":[10,20],\"demands\":[{\"entity_id\":10,\"request_id\":1,\"priority\":0,\"amount\":3,\"allow_partial\":true},{\"entity_id\":20,\"request_id\":1,\"priority\":2,\"amount\":4,\"allow_partial\":false}]}";
    private const string StateText = "{\"core_state_format_version\":1,\"state_kind\":\"fixture.resources\",\"state_version\":1,\"simulation\":{\"world_seed\":42,\"generator_version\":\"test-generator-v1\",\"completed_steps\":4,\"boundary_sequence\":2,\"next_command_id\":3,\"next_entity_id\":21,\"transaction_count\":6,\"fault\":null},\"domain\":" + DomainText + "}";

    [TestMethod]
    public void Capture_preserves_the_complete_runtime_type_and_does_not_advance()
    {
        var state = State();
        var snapshot = new ResourceStateCodec().Capture(state);
        Assert.AreEqual("fixture.resources", snapshot.StateKind);
        Assert.AreEqual(1U, snapshot.StateVersion);
        Assert.AreSame(state, snapshot.State);
        Assert.AreEqual(4UL, snapshot.State.CompletedSteps);
        Assert.AreEqual(6UL, snapshot.State.TransactionCount);
    }

    [TestMethod]
    public void Encode_matches_the_independently_written_version_1_vector()
    {
        var codec = new ResourceStateCodec();
        Assert.AreEqual(StateText, codec.Encode(new("fixture.resources", 1, State())));
    }

    [DataTestMethod]
    [DataRow("fixture.other", 1U)]
    [DataRow("fixture.resources", 2U)]
    public void Encode_rejects_a_snapshot_owned_by_another_format(string kind, uint version)
    {
        var codec = new ResourceStateCodec();
        Assert.ThrowsException<ArgumentException>(() => codec.Encode(new(kind, version, State())));
    }

    [TestMethod]
    public void Domain_writer_uses_fixed_fields_and_stable_identity_order()
    {
        var shuffled = State() with
        {
            Holdings = ImmutableDictionary<ulong, ulong>.Empty.Add(20, 2).Add(10, 3),
            Demands = State().Demands.Reverse().ToImmutableArray()
        };
        Assert.AreEqual(DomainText.Replace("[{\"entity_id\":10,\"amount\":3}]", "[{\"entity_id\":10,\"amount\":3},{\"entity_id\":20,\"amount\":2}]"),
            new ResourceStateCodec().WriteDomainText(shuffled));
    }

    [TestMethod]
    public void Domain_reader_copies_every_boundary_counter_and_fault()
    {
        var fault = new SimulationFault("fixture_failure", SimulationPhase.Command,
            new TransactionContext(7, SimulationOperation.Command, new(4, 3), 3), 10);
        var boundary = State() with { Fault = fault };
        var result = Success(new ResourceStateCodec().ReadDomainForTest(boundary, DomainText));
        var restored = (ResourceState)result;
        Assert.AreEqual(boundary.WorldSeed, restored.WorldSeed);
        Assert.AreEqual(boundary.CompletedSteps, restored.CompletedSteps);
        Assert.AreEqual(boundary.BoundarySequence, restored.BoundarySequence);
        Assert.AreEqual(boundary.NextCommandId, restored.NextCommandId);
        Assert.AreEqual(boundary.NextEntityId, restored.NextEntityId);
        Assert.AreEqual(boundary.TransactionCount, restored.TransactionCount);
        Assert.AreSame(fault, restored.Fault);
        Assert.AreEqual(9UL, restored.Stock);
        CollectionAssert.AreEqual(new ulong[] { 10, 20 }, restored.EntityIds.ToArray());
    }

    [DataTestMethod]
    [DataRow("{\"stock\":9,\"holdings\":[],\"entity_ids\":[],\"demands\":[],\"extra\":0}", "unknown_field", "/extra")]
    [DataRow("{\"holdings\":[],\"entity_ids\":[],\"demands\":[]}", "missing_field", "/stock")]
    [DataRow("{\"stock\":-1,\"holdings\":[],\"entity_ids\":[],\"demands\":[]}", "invalid_uint64", "/stock")]
    [DataRow("{\"stock\":0,\"holdings\":[{\"entity_id\":0,\"amount\":1}],\"entity_ids\":[],\"demands\":[]}", "invalid_entity_id", "/holdings/0/entity_id")]
    [DataRow("{\"stock\":0,\"holdings\":[],\"entity_ids\":[1,1],\"demands\":[]}", "duplicate_identity", "/entity_ids/1")]
    [DataRow("{\"stock\":0,\"holdings\":[],\"entity_ids\":[1],\"demands\":[{\"entity_id\":1,\"request_id\":1,\"priority\":2147483648,\"amount\":1,\"allow_partial\":true}]}", "invalid_int32", "/demands/0/priority")]
    [DataRow("{\"stock\":0,\"holdings\":[],\"entity_ids\":[1],\"demands\":[{\"entity_id\":1,\"request_id\":1,\"priority\":0,\"amount\":1,\"allow_partial\":true},{\"entity_id\":1,\"request_id\":1,\"priority\":1,\"amount\":2,\"allow_partial\":false}]}", "duplicate_identity", "/demands/1")]
    public void Domain_reader_rejects_invalid_data_at_the_exact_relative_path(string json, string code, string path)
    {
        var failure = Failure(new ResourceStateCodec().ReadDomainForTest(State(), json));
        Assert.AreEqual(DeterminismErrorKind.InvalidData, failure.Kind);
        Assert.AreEqual(code, failure.Code);
        Assert.AreEqual(path, failure.Path);
    }

    [TestMethod]
    public void Unordered_domain_collections_reencode_by_stable_identity()
    {
        const string input = "{\"stock\":0,\"holdings\":[{\"entity_id\":2,\"amount\":1},{\"entity_id\":1,\"amount\":3}],\"entity_ids\":[2,1],\"demands\":[]}";
        const string expected = "{\"stock\":0,\"holdings\":[{\"entity_id\":1,\"amount\":3},{\"entity_id\":2,\"amount\":1}],\"entity_ids\":[1,2],\"demands\":[]}";
        var codec = new ResourceStateCodec();
        var state = (ResourceState)Success(codec.ReadDomainForTest(new ResourceState { NextEntityId = 3 }, input));
        Assert.AreEqual(expected, codec.WriteDomainText(state));
    }

    [DataTestMethod]
    [DataRow("{\"core_state_format_version\":2,\"state_kind\":\"fixture.resources\",\"state_version\":1,\"simulation\":{},\"domain\":{}}", "unsupported_core_state_format", "/core_state_format_version")]
    [DataRow("{\"core_state_format_version\":1,\"state_kind\":\"other\",\"state_version\":1,\"simulation\":{},\"domain\":{}}", "unsupported_state_kind", "/state_kind")]
    [DataRow("{\"core_state_format_version\":1,\"state_kind\":\"fixture.resources\",\"state_version\":2,\"simulation\":{},\"domain\":{}}", "unsupported_state_version", "/state_version")]
    public void Decode_rejects_unknown_envelope_formats_with_public_paths(string json, string code, string path)
    {
        var error = Failure(new ResourceStateCodec().Decode(json));
        Assert.AreEqual(code, error.Code);
        Assert.AreEqual(path, error.Path);
    }

    [TestMethod]
    public void Decode_adds_domain_prefix_to_fixture_reader_paths()
    {
        var malformed = StateText.Replace("\"stock\":9", "\"stock\":-1", StringComparison.Ordinal);
        Assert.AreEqual("/domain/stock", Failure(new ResourceStateCodec().Decode(malformed)).Path);
    }

    [TestMethod]
    public void Decode_then_encode_round_trips_the_complete_public_document()
    {
        var codec = new ResourceStateCodec();
        var snapshot = ((DeterminismResult<CoreStateSnapshot>.Success)codec.Decode(StateText)).Value;
        Assert.AreEqual("fixture.resources", snapshot.StateKind);
        Assert.AreEqual(1U, snapshot.StateVersion);
        Assert.AreEqual(StateText, codec.Encode(snapshot));
        AssertState(State(), (ResourceState)snapshot.State);
    }

    [DataTestMethod]
    [DataRow("Command", true, true)]
    [DataRow("Boundary", false, false)]
    [DataRow("Evaluate", true, false)]
    [DataRow("Resolve", false, true)]
    [DataRow("Commit", true, true)]
    [DataRow("Validation", false, false)]
    public void Fault_phase_optional_context_and_entity_round_trip(string phaseName, bool hasContext, bool hasEntity)
    {
        var phase = Enum.Parse<SimulationPhase>(phaseName);
        var fault = new SimulationFault("fixture_failure", phase,
            hasContext ? new TransactionContext(7, SimulationOperation.Command, new(4, 3), 3) : null,
            hasEntity ? 10UL : null);
        var codec = new ResourceStateCodec();
        var text = codec.Encode(new("fixture.resources", 1, State() with { Fault = fault }));
        var decoded = ((DeterminismResult<CoreStateSnapshot>.Success)codec.Decode(text)).Value;
        Assert.AreEqual(fault, decoded.State.Fault);
        AssertState(State() with { Fault = fault }, (ResourceState)decoded.State);
    }

    [DataTestMethod]
    [DataRow("{", "invalid_json", "")]
    [DataRow("[]", "invalid_type", "")]
    [DataRow("{\"core_state_format_version\":1,\"state_kind\":\"fixture.resources\",\"state_version\":1,\"simulation\":{},\"domain\":{},\"extra\":0}", "unknown_field", "/extra")]
    [DataRow("{\"core_state_format_version\":1,\"core_state_format_version\":1,\"state_kind\":\"fixture.resources\",\"state_version\":1,\"simulation\":{},\"domain\":{}}", "duplicate_field", "/core_state_format_version")]
    [DataRow("{\"core_state_format_version\":1,\"state_version\":1,\"simulation\":{},\"domain\":{}}", "missing_field", "/state_kind")]
    [DataRow("{\"core_state_format_version\":1,\"state_kind\":\"fixture.resources\",\"state_version\":1,\"simulation\":{\"world_seed\":18446744073709551616},\"domain\":{}}", "invalid_uint64", "/simulation/world_seed")]
    [DataRow("{\"core_state_format_version\":1,\"state_kind\":\"fixture.resources\",\"state_version\":1,\"simulation\":{\"world_seed\":\"42\"},\"domain\":{}}", "invalid_type", "/simulation/world_seed")]
    public void Decode_rejects_malformed_or_invalid_public_envelopes(string json, string code, string path)
    {
        var error = Failure(new ResourceStateCodec().Decode(json));
        Assert.AreEqual(code, error.Code);
        Assert.AreEqual(path, error.Path);
    }

    [TestMethod]
    public void Decode_rejects_an_incomplete_unicode_string_at_its_path()
    {
        var json = StateText.Replace("test-generator-v1", "\\uD800", StringComparison.Ordinal);
        var error = Failure(new ResourceStateCodec().Decode(json));
        Assert.AreEqual("invalid_string", error.Code);
        Assert.AreEqual("/simulation/generator_version", error.Path);
    }

    [TestMethod]
    public void Initial_state_accepts_exhausted_allocator_sentinels_and_consistent_maximum_counts()
    {
        var state = new ResourceState
        {
            CompletedSteps = ulong.MaxValue - 1,
            BoundarySequence = 0,
            NextCommandId = 2,
            NextEntityId = ulong.MaxValue,
            TransactionCount = ulong.MaxValue,
            EntityIds = ImmutableSortedSet.Create(ulong.MaxValue - 1)
        };
        Assert.IsNull(new ResourceCalculations().CheckInitialState(state));
    }

    [DataTestMethod]
    [DataRow(0UL, 0UL, 0UL, 1UL, 0UL, "invalid_command_id", "NextCommandId")]
    [DataRow(0UL, 0UL, 1UL, 0UL, 0UL, "invalid_entity_id", "NextEntityId")]
    [DataRow(1UL, 2UL, 2UL, 1UL, 2UL, "invalid_boundary_sequence", "BoundarySequence")]
    [DataRow(1UL, 0UL, 2UL, 1UL, 1UL, "invalid_transaction_count", "TransactionCount")]
    public void Initial_state_rejects_invalid_common_boundaries(ulong steps, ulong sequence, ulong nextCommand,
        ulong nextEntity, ulong transactions, string code, string field)
    {
        var violation = new ResourceCalculations().CheckInitialState(new ResourceState
        {
            CompletedSteps = steps, BoundarySequence = sequence, NextCommandId = nextCommand,
            NextEntityId = nextEntity, TransactionCount = transactions
        });
        Assert.AreEqual(new InvariantViolation(code, field), violation);
    }

    [TestMethod]
    public void Initial_state_rejects_overflow_in_the_transaction_count_equation()
    {
        var violation = new ResourceCalculations().CheckInitialState(new ResourceState
        {
            CompletedSteps = ulong.MaxValue, NextCommandId = 2, NextEntityId = 1,
            TransactionCount = ulong.MaxValue
        });
        Assert.AreEqual("invalid_transaction_count", violation?.Code);
    }

    [DataTestMethod]
    [DataRow("entity_zero", "invalid_entity_id", "EntityIds")]
    [DataRow("entity_at_allocator", "invalid_entity_id", "EntityIds")]
    [DataRow("holding_reference", "missing_entity_reference", "Holdings")]
    [DataRow("demand_reference", "missing_entity_reference", "Demands")]
    [DataRow("duplicate_request", "duplicate_request_identity", "Demands")]
    public void Initial_state_rejects_invalid_domain_relationships(string scenario, string code, string field)
    {
        var state = new ResourceState { NextEntityId = 3, EntityIds = ImmutableSortedSet.Create(1UL) };
        state = scenario switch
        {
            "entity_zero" => state with { EntityIds = ImmutableSortedSet.Create(0UL) },
            "entity_at_allocator" => state with { EntityIds = ImmutableSortedSet.Create(3UL) },
            "holding_reference" => state with { Holdings = ImmutableDictionary<ulong, ulong>.Empty.Add(2, 1) },
            "demand_reference" => state with { Demands = ImmutableArray.Create(new ResourceDemand(2, 1, 0, 1)) },
            _ => state with { Demands = ImmutableArray.Create(new ResourceDemand(1, 1, 0, 1), new ResourceDemand(1, 1, 2, 3)) }
        };
        Assert.AreEqual(new InvariantViolation(code, field), new ResourceCalculations().CheckInitialState(state));
    }

    [TestMethod]
    public void Restore_failure_has_no_partial_session_and_cannot_change_the_current_session()
    {
        var current = new SimulationSession(State(), new ResourceCalculations(), new ResourceCommandCodec());
        var before = current.GetState();
        var invalid = new CoreStateSnapshot("fixture.resources", 1, State() with { NextEntityId = 0 });
        Assert.IsInstanceOfType<DeterminismResult<SimulationSession>.Failure>(
            new ResourceStateCodec().Restore(invalid, new ResourceCalculations(), new ResourceCommandCodec()));
        Assert.AreSame(before, current.GetState());
    }

    public static IEnumerable<object[]> InvalidFaultContexts()
    {
        yield return [new TransactionContext(7, SimulationOperation.Step, new(5, 0), 3)];
        yield return [new TransactionContext(6, SimulationOperation.Command, new(4, 3), 3)];
        yield return [new TransactionContext(7, SimulationOperation.Command, new(4, 3), 2)];
        yield return [new TransactionContext(7, SimulationOperation.Command, new(4, 2), 3)];
        yield return [new TransactionContext(7, SimulationOperation.Step, new(4, 0))];
    }

    [DataTestMethod]
    [DynamicData(nameof(InvalidFaultContexts), DynamicDataSourceType.Method)]
    public void Restore_rejects_fault_attempts_that_do_not_belong_to_the_frozen_boundary(TransactionContext context)
    {
        var state = State() with { Fault = new("fixture_failure", SimulationPhase.Validation, context) };
        var codec = new ResourceStateCodec();
        var decoded = ((DeterminismResult<CoreStateSnapshot>.Success)codec.Decode(
            codec.Encode(codec.Capture(state)))).Value;
        var error = Failure(codec.Restore(decoded, new ResourceCalculations(), new ResourceCommandCodec()));
        Assert.AreEqual("invalid_fault_context", error.Code);
        Assert.AreEqual("/simulation/fault", error.Path);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Restore_preserves_a_failed_step_or_a_failure_before_identity_preparation(bool attemptedStep)
    {
        var fault = new SimulationFault("fixture_failure", attemptedStep ? SimulationPhase.Commit : SimulationPhase.Boundary,
            attemptedStep ? new TransactionContext(7, SimulationOperation.Step, new(5, 0)) : null);
        var state = State() with { Fault = fault };
        var codec = new ResourceStateCodec();
        var decoded = ((DeterminismResult<CoreStateSnapshot>.Success)codec.Decode(
            codec.Encode(codec.Capture(state)))).Value;
        var session = ((DeterminismResult<SimulationSession>.Success)codec.Restore(
            decoded, new ResourceCalculations(), new ResourceCommandCodec())).Value;
        AssertState(state, (ResourceState)session.GetState());
        Assert.AreEqual(0UL, session.Advance(1).StepsAdvanced);
    }

    [TestMethod]
    public void Restore_preserves_fault_freezing_and_starts_a_fresh_empty_log()
    {
        var fault = new SimulationFault("fixture_failure", SimulationPhase.Command,
            new TransactionContext(7, SimulationOperation.Command, new(4, 3), 3), 10);
        var codec = new ResourceStateCodec();
        var decoded = ((DeterminismResult<CoreStateSnapshot>.Success)codec.Decode(
            codec.Encode(new("fixture.resources", 1, State() with { Fault = fault })))).Value;
        var result = codec.Restore(decoded, new ResourceCalculations(), new ResourceCommandCodec());
        var session = ((DeterminismResult<SimulationSession>.Success)result).Value;
        Assert.AreEqual(fault, session.GetState().Fault);
        Assert.IsInstanceOfType<SubmissionResult.NotAccepted>(session.Submit(new NoChangeCommand(), CommandSource.Ui));
        Assert.AreEqual(CommandLogSnapshot.Empty, session.GetCommandLog());
    }

    internal static ResourceState State() => new()
    {
        Stock = 9,
        Holdings = ImmutableDictionary<ulong, ulong>.Empty.Add(10, 3),
        EntityIds = ImmutableSortedSet.Create(10UL, 20UL),
        Demands = ImmutableArray.Create(new ResourceDemand(10, 1, 0, 3), new ResourceDemand(20, 1, 2, 4, false)),
        CompletedSteps = 4, BoundarySequence = 2, NextCommandId = 3, NextEntityId = 21, TransactionCount = 6
    };
    private static SimulationState Success(DeterminismResult<SimulationState> result) => ((DeterminismResult<SimulationState>.Success)result).Value;
    private static DeterminismError Failure<T>(DeterminismResult<T> result) where T : notnull => ((DeterminismResult<T>.Failure)result).Error;
    private static void AssertState(ResourceState expected, ResourceState actual)
    {
        Assert.AreEqual(expected.WorldSeed, actual.WorldSeed);
        Assert.AreEqual(expected.GeneratorVersion, actual.GeneratorVersion);
        Assert.AreEqual(expected.CompletedSteps, actual.CompletedSteps);
        Assert.AreEqual(expected.BoundarySequence, actual.BoundarySequence);
        Assert.AreEqual(expected.NextCommandId, actual.NextCommandId);
        Assert.AreEqual(expected.NextEntityId, actual.NextEntityId);
        Assert.AreEqual(expected.TransactionCount, actual.TransactionCount);
        Assert.AreEqual(expected.Fault, actual.Fault);
        Assert.AreEqual(expected.Stock, actual.Stock);
        CollectionAssert.AreEqual(expected.Holdings.OrderBy(x => x.Key).ToArray(), actual.Holdings.OrderBy(x => x.Key).ToArray());
        CollectionAssert.AreEqual(expected.EntityIds.ToArray(), actual.EntityIds.ToArray());
        CollectionAssert.AreEqual(expected.Demands.ToArray(), actual.Demands.ToArray());
    }
}
