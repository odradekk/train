
using System.Collections.Immutable;
using System.Text.Json;

namespace Train.Core.Simulation;

public sealed record CoreStateSnapshot(string StateKind, uint StateVersion, SimulationState State);

public abstract class SimulationStateCodec
{
    public const uint CoreStateFormatVersion = 1;

    public abstract string StateKind { get; }

    public abstract uint StateVersion { get; }

    public CoreStateSnapshot Capture(SimulationState state)
        => new(StateKind, StateVersion, state);

    public string Encode(CoreStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var error = CheckSnapshotFormat(snapshot);
        if (error is not null)
            throw new ArgumentException($"捕获值不属于此状态格式：{error.Code} {error.Path}", nameof(snapshot));
        return SimulationJson.Format(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("core_state_format_version", CoreStateFormatVersion);
            writer.WriteString("state_kind", snapshot.StateKind);
            writer.WriteNumber("state_version", snapshot.StateVersion);
            writer.WritePropertyName("simulation");
            SimulationStateDocument.Write(writer, snapshot.State);
            writer.WritePropertyName("domain");
            writer.WriteStartObject();
            WriteDomain(writer, snapshot.State);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    public DeterminismResult<CoreStateSnapshot> Decode(string json)
    {
        var envelope = ReadEnvelope(json);
        if (envelope is DeterminismResult<(SimulationState Boundary, JsonElement Domain)>.Failure failure)
            return new DeterminismResult<CoreStateSnapshot>.Failure(failure.Error);

        var value = ((DeterminismResult<(SimulationState Boundary, JsonElement Domain)>.Success)envelope).Value;
        var result = ReadDomain(value.Boundary, value.Domain);
        return result switch
        {
            DeterminismResult<SimulationState>.Success domain when SameBoundary(value.Boundary, domain.Value) =>
                new DeterminismResult<CoreStateSnapshot>.Success(new(StateKind, StateVersion, domain.Value)),
            DeterminismResult<SimulationState>.Success =>
                new DeterminismResult<CoreStateSnapshot>.Failure(new(
                    DeterminismErrorKind.InvalidData, "domain_changed_boundary", "/domain")),
            DeterminismResult<SimulationState>.Failure domain =>
                new DeterminismResult<CoreStateSnapshot>.Failure(domain.Error with
                { Path = "/domain" + domain.Error.Path }),
            _ => throw new InvalidOperationException("未知的领域读取结果。")
        };
    }

    public DeterminismResult<SimulationSession> Restore(
        CoreStateSnapshot snapshot, SimulationCalculations calculations, SimulationCommandCodec commands)
    {
        var formatError = CheckSnapshotFormat(snapshot);
        if (formatError is not null)
            return new DeterminismResult<SimulationSession>.Failure(formatError);
        var violation = calculations.CheckInitialState(snapshot.State);
        return violation is null
            ? new DeterminismResult<SimulationSession>.Success(new(snapshot.State, calculations, commands))
            : new DeterminismResult<SimulationSession>.Failure(LocateInitialViolation(violation));
    }

    public AuthoritativeCheckpoint CreateCheckpoint(SimulationState state) =>
        SimulationCheckpoints.Encode(ProjectDomain(state).Add(ProjectBoundary(state)));

    private DeterminismResult<(SimulationState Boundary, JsonElement Domain)> ReadEnvelope(string json)
    {
        try
        {
            var root = SimulationJson.ParseObject(json);
            SimulationJson.CheckKnownFields(root, "", "core_state_format_version", "state_kind", "state_version",
                "simulation", "domain");

            var coreVersion = SimulationJson.UInt32(EnvelopeProperty(root, "core_state_format_version"),
                "/core_state_format_version");
            if (coreVersion != CoreStateFormatVersion)
                return Failure<(SimulationState, JsonElement)>(DeterminismErrorKind.UnknownFormat,
                    "unsupported_core_state_format", "/core_state_format_version");

            var kind = SimulationJson.String(EnvelopeProperty(root, "state_kind"), "/state_kind", true);
            if (!string.Equals(kind, StateKind, StringComparison.Ordinal))
                return Failure<(SimulationState, JsonElement)>(DeterminismErrorKind.UnknownFormat,
                    "unsupported_state_kind", "/state_kind");

            var version = SimulationJson.UInt32(EnvelopeProperty(root, "state_version"), "/state_version");
            if (version != StateVersion)
                return Failure<(SimulationState, JsonElement)>(DeterminismErrorKind.UnknownFormat,
                    "unsupported_state_version", "/state_version");

            var boundary = SimulationStateDocument.Read(EnvelopeProperty(root, "simulation"), "/simulation");
            var domain = EnvelopeProperty(root, "domain");
            SimulationJson.RequireObject(domain, "/domain");
            return new DeterminismResult<(SimulationState, JsonElement)>.Success((boundary, domain.Clone()));
        }
        catch (JsonException)
        {
            return Failure<(SimulationState, JsonElement)>(DeterminismErrorKind.InvalidData, "invalid_json", "");
        }
        catch (SimulationJsonException exception)
        {
            return new DeterminismResult<(SimulationState, JsonElement)>.Failure(exception.Error);
        }
    }

    private static JsonElement EnvelopeProperty(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property)
            ? property
            : throw SimulationJson.Invalid("missing_field", "/" + SimulationJson.Escape(name));

    private DeterminismError? CheckSnapshotFormat(CoreStateSnapshot snapshot)
    {
        if (!string.Equals(snapshot.StateKind, StateKind, StringComparison.Ordinal))
            return new(DeterminismErrorKind.UnknownFormat, "unsupported_state_kind", "/state_kind");
        return snapshot.StateVersion != StateVersion
            ? new(DeterminismErrorKind.UnknownFormat, "unsupported_state_version", "/state_version")
            : null;
    }

    private static DeterminismError LocateInitialViolation(InvariantViolation violation)
    {
        var commonField = violation.Field switch
        {
            nameof(SimulationState.WorldSeed) => "world_seed",
            nameof(SimulationState.GeneratorVersion) => "generator_version",
            nameof(SimulationState.CompletedSteps) => "completed_steps",
            nameof(SimulationState.BoundarySequence) => "boundary_sequence",
            nameof(SimulationState.NextCommandId) => "next_command_id",
            nameof(SimulationState.NextEntityId) => "next_entity_id",
            nameof(SimulationState.TransactionCount) => "transaction_count",
            nameof(SimulationState.Fault) => "fault",
            nameof(SimulationState) => "",
            _ => null
        };
        var path = commonField is null
            ? "/domain/" + SnakeCase(violation.Field)
            : "/simulation" + (commonField.Length == 0 ? "" : "/" + commonField);
        return new(DeterminismErrorKind.InvalidData, violation.Code, path);
    }

    private static CheckpointObject ProjectBoundary(SimulationState state)
    {
        var fields = SimulationJson.ParseObject(SimulationJson.Format(writer =>
            SimulationStateDocument.Write(writer, state)));
        return new(new CheckpointObjectId("simulation", 0), fields);
    }

    private static bool SameBoundary(SimulationState expected, SimulationState actual) =>
        expected.WorldSeed == actual.WorldSeed &&
        string.Equals(expected.GeneratorVersion, actual.GeneratorVersion, StringComparison.Ordinal) &&
        expected.CompletedSteps == actual.CompletedSteps &&
        expected.BoundarySequence == actual.BoundarySequence &&
        expected.NextCommandId == actual.NextCommandId &&
        expected.NextEntityId == actual.NextEntityId &&
        expected.TransactionCount == actual.TransactionCount &&
        expected.Fault == actual.Fault;

    private static string SnakeCase(string value) => string.Concat(value.Select((character, index) =>
        char.IsUpper(character) && index > 0
            ? "_" + char.ToLowerInvariant(character)
            : char.ToLowerInvariant(character).ToString()));

    private static DeterminismResult<T>.Failure Failure<T>(
        DeterminismErrorKind kind, string code, string path) where T : notnull =>
        new(new DeterminismError(kind, code, path));

    protected abstract void WriteDomain(Utf8JsonWriter writer, SimulationState state);

    protected abstract DeterminismResult<SimulationState> ReadDomain(SimulationState boundary, JsonElement fields);

    protected abstract ImmutableArray<CheckpointObject> ProjectDomain(SimulationState state);
}
