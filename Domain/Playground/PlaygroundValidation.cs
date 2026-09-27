using System.Globalization;
using Train.Content;
using Train.Domain.Rails;
using Train.Domain.Voxels;

namespace Train.Domain.Playground;

public enum PlaygroundOperationKind
{
    Pause,
    Resume,
    Advance,
    StartTrain,
    StopTrain,
    DamageVoxel
}

/// <summary>A formal session operation shared by the manual Playground controls and the automatic validation runner.</summary>
public sealed record PlaygroundOperation(
    PlaygroundOperationKind Kind,
    ulong Steps = 0,
    RailHeading Heading = RailHeading.AlongAlignment,
    WorldVoxelCoord Cell = default,
    int Amount = 0)
{
    public static PlaygroundOperation Pause() => new(PlaygroundOperationKind.Pause);
    public static PlaygroundOperation Resume() => new(PlaygroundOperationKind.Resume);
    public static PlaygroundOperation Advance(ulong steps) => new(PlaygroundOperationKind.Advance, steps);
    public static PlaygroundOperation Start(RailHeading heading = RailHeading.AlongAlignment) =>
        new(PlaygroundOperationKind.StartTrain, Heading: heading);
    public static PlaygroundOperation Stop() => new(PlaygroundOperationKind.StopTrain);
    public static PlaygroundOperation Damage(WorldVoxelCoord cell, int amount) =>
        new(PlaygroundOperationKind.DamageVoxel, Cell: cell, Amount: amount);

    public string Apply(VoxelDevelopmentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Kind switch
        {
            PlaygroundOperationKind.Pause => session.PauseSimulation().Status.ToString(),
            PlaygroundOperationKind.Resume => session.ResumeSimulation().Status.ToString(),
            PlaygroundOperationKind.Advance => session.AdvanceSimulation(Steps).Status.ToString(),
            PlaygroundOperationKind.StartTrain => session.StartTrain(Heading).Status.ToString(),
            PlaygroundOperationKind.StopTrain => session.StopTrain().Status.ToString(),
            PlaygroundOperationKind.DamageVoxel => session.Damage(Cell, Amount).Status.ToString(),
            _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null)
        };
    }

    public string Describe() => Kind switch
    {
        PlaygroundOperationKind.Advance => $"advance {Steps.ToString(CultureInfo.InvariantCulture)}",
        PlaygroundOperationKind.StartTrain => $"start {Heading}",
        PlaygroundOperationKind.DamageVoxel => $"damage {Cell.X},{Cell.Y},{Cell.Z} {Amount.ToString(CultureInfo.InvariantCulture)}",
        _ => Kind.ToString().ToLowerInvariant()
    };
}

public enum PlaygroundObservation
{
    SimulationStep,
    Paused,
    AssemblyCount,
    LeadFrontContactX,
    Speed,
    Running,
    HaltReason
}

public sealed record PlaygroundCheck(PlaygroundObservation Observation, string Expected)
{
    public static string Observe(VoxelDevelopmentSession session, PlaygroundObservation observation)
    {
        ArgumentNullException.ThrowIfNull(session);
        var assembly = session.QueryTrainAssemblies().FirstOrDefault();
        return observation switch
        {
            PlaygroundObservation.SimulationStep => session.SimulationStep.ToString(CultureInfo.InvariantCulture),
            PlaygroundObservation.Paused => session.SimulationPaused ? "true" : "false",
            PlaygroundObservation.AssemblyCount => session.QueryTrainAssemblies().Count.ToString(CultureInfo.InvariantCulture),
            PlaygroundObservation.LeadFrontContactX => assembly is null ? "none" : Number(assembly.Vehicles[0].FrontContact.X),
            PlaygroundObservation.Speed => assembly is null ? "none" : Number(assembly.Speed),
            PlaygroundObservation.Running => assembly is null ? "none" : assembly.Running ? "true" : "false",
            PlaygroundObservation.HaltReason => assembly is null ? "none" : assembly.HaltReason.ToString(),
            _ => throw new ArgumentOutOfRangeException(nameof(observation), observation, null)
        };
    }

    private static string Number(double value) =>
        Math.Round(value, 6, MidpointRounding.AwayFromZero).ToString("0.######", CultureInfo.InvariantCulture);
}

public sealed record PlaygroundValidationStep(
    PlaygroundOperation Operation,
    string ExpectedStatus,
    IReadOnlyList<PlaygroundCheck> Checkpoint);

/// <summary>Validation steps live outside the fixed configuration and only reference it by id.</summary>
public sealed record PlaygroundSequence(
    string Id,
    string ConfigurationId,
    IReadOnlyList<PlaygroundValidationStep> Steps);

public sealed record PlaygroundCheckResult(PlaygroundObservation Observation, string Expected, string Observed)
{
    public bool Passed => string.Equals(Expected, Observed, StringComparison.Ordinal);
}

public sealed record PlaygroundStepResult(
    int Index,
    string Operation,
    string ExpectedStatus,
    string ActualStatus,
    IReadOnlyList<PlaygroundCheckResult> Checks)
{
    public bool Passed =>
        string.Equals(ExpectedStatus, ActualStatus, StringComparison.Ordinal) &&
        Checks.All(check => check.Passed);
}

public sealed record PlaygroundSequenceReport(
    string SequenceId,
    string ConfigurationId,
    PlaygroundEstablishStatus EstablishStatus,
    IReadOnlyList<PlaygroundStepResult> Steps,
    string? FinalFingerprint)
{
    public bool Passed =>
        EstablishStatus == PlaygroundEstablishStatus.Established &&
        Steps.All(step => step.Passed);

    public IEnumerable<string> Lines()
    {
        yield return $"sequence {SequenceId} config {ConfigurationId} establish {EstablishStatus}";
        foreach (var step in Steps)
        {
            var checks = string.Join(' ', step.Checks.Select(check =>
                $"{check.Observation}={check.Observed}{(check.Passed ? "" : $"(expected {check.Expected})")}"));
            yield return $"step {step.Index} {step.Operation} -> {step.ActualStatus}" +
                $"{(step.ActualStatus == step.ExpectedStatus ? "" : $" (expected {step.ExpectedStatus})")}" +
                $"{(checks.Length == 0 ? "" : " | " + checks)} {(step.Passed ? "ok" : "FAIL")}";
        }

        yield return $"result {(Passed ? "passed" : "failed")} fingerprint {FinalFingerprint ?? "none"}";
    }
}

public static class PlaygroundValidation
{
    public static PlaygroundSequenceReport Run(
        PlaygroundSequence sequence,
        VoxelCatalog catalog,
        CompositeCatalog composites,
        IWorldSaveStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        var status = PlaygroundSession.TryEstablish(sequence.ConfigurationId, catalog, composites, store, out var session);
        if (status != PlaygroundEstablishStatus.Established)
            return new PlaygroundSequenceReport(sequence.Id, sequence.ConfigurationId, status, Array.Empty<PlaygroundStepResult>(), null);

        try
        {
            return Execute(sequence, session!);
        }
        finally
        {
            session!.Detach();
        }
    }

    public static PlaygroundSequenceReport Execute(PlaygroundSequence sequence, VoxelDevelopmentSession session)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(session);
        if (!string.Equals(session.WorldSource?.ConfigurationId, sequence.ConfigurationId, StringComparison.Ordinal))
            return new PlaygroundSequenceReport(
                sequence.Id,
                sequence.ConfigurationId,
                PlaygroundEstablishStatus.UnknownConfiguration,
                Array.Empty<PlaygroundStepResult>(),
                null);

        var results = new List<PlaygroundStepResult>();
        for (var index = 0; index < sequence.Steps.Count; index++)
        {
            var step = sequence.Steps[index];
            var actual = step.Operation.Apply(session);
            var checks = step.Checkpoint
                .Select(check => new PlaygroundCheckResult(check.Observation, check.Expected, PlaygroundCheck.Observe(session, check.Observation)))
                .ToArray();
            results.Add(new PlaygroundStepResult(index, step.Operation.Describe(), step.ExpectedStatus, actual, checks));
        }

        return new PlaygroundSequenceReport(
            sequence.Id,
            sequence.ConfigurationId,
            PlaygroundEstablishStatus.Established,
            results,
            WorldSaveAuthority.Fingerprint(session));
    }
}

public static class PlaygroundSequences
{
    public const string StraightRailRunAndHaltId = "straight-rail-run-and-halt";
    public const string StraightRailRejectionsId = "straight-rail-rejections";

    public static IReadOnlyList<PlaygroundSequence> All { get; } = new[]
    {
        new PlaygroundSequence(
            StraightRailRunAndHaltId,
            PlaygroundConfigurations.StraightRailTrainId,
            new[]
            {
                Step(PlaygroundOperation.Start(), "Started",
                    Check(PlaygroundObservation.SimulationStep, "0"),
                    Check(PlaygroundObservation.Paused, "true")),
                Step(PlaygroundOperation.Advance(1), "Advanced",
                    Check(PlaygroundObservation.SimulationStep, "1")),
                Step(PlaygroundOperation.Advance(299), "Advanced",
                    Check(PlaygroundObservation.SimulationStep, "300"),
                    Check(PlaygroundObservation.LeadFrontContactX, "96"),
                    Check(PlaygroundObservation.Speed, "4"),
                    Check(PlaygroundObservation.Running, "true")),
                Step(PlaygroundOperation.Damage(StraightRailFixture.FirstSegmentDamageCell, 100), "Removed"),
                Step(PlaygroundOperation.Advance(2000), "Advanced",
                    Check(PlaygroundObservation.SimulationStep, "2300"),
                    Check(PlaygroundObservation.Speed, "0"),
                    Check(PlaygroundObservation.Running, "false"),
                    Check(PlaygroundObservation.HaltReason, "ForwardGap"))
            }),
        new PlaygroundSequence(
            StraightRailRejectionsId,
            PlaygroundConfigurations.StraightRailTrainId,
            new[]
            {
                Step(PlaygroundOperation.Start(RailHeading.AgainstAlignment), "ReverseUnsupported",
                    Check(PlaygroundObservation.Running, "false"),
                    Check(PlaygroundObservation.LeadFrontContactX, "80")),
                Step(PlaygroundOperation.Start(), "Started"),
                Step(PlaygroundOperation.Damage(StraightRailFixture.FirstSegmentDamageCell, 100), "Removed"),
                Step(PlaygroundOperation.Advance(2000), "Advanced",
                    Check(PlaygroundObservation.HaltReason, "ForwardGap")),
                Step(PlaygroundOperation.Start(), "DisconnectedPath",
                    Check(PlaygroundObservation.Speed, "0"),
                    Check(PlaygroundObservation.Running, "false"),
                    Check(PlaygroundObservation.HaltReason, "ForwardGap")),
                Step(PlaygroundOperation.Advance(ulong.MaxValue), "StepOverflow",
                    Check(PlaygroundObservation.SimulationStep, "2000"))
            })
    };

    public static PlaygroundSequence? Find(string id) =>
        All.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));

    private static PlaygroundValidationStep Step(PlaygroundOperation operation, string expectedStatus, params PlaygroundCheck[] checks) =>
        new(operation, expectedStatus, checks);

    private static PlaygroundCheck Check(PlaygroundObservation observation, string expected) => new(observation, expected);
}
