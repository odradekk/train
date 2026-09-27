using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Domain.Playground;
using Train.Domain.Rails;
using Train.Domain.Voxels;

namespace Train.Tests;

[TestClass]
public sealed class PlaygroundTests
{
    private const double Tolerance = 1e-8;

    [TestMethod]
    public void Named_configuration_resolves_stable_identity_version_parameters_and_content_dependencies()
    {
        using var content = PlaygroundHarness.Content();

        Assert.AreEqual(
            PlaygroundEstablishStatus.Established,
            PlaygroundConfigurations.TryResolve(
                PlaygroundConfigurations.StraightRailTrainId,
                content.Catalog,
                content.Composites,
                out var source));

        Assert.AreEqual(WorldSource.FixedConfigurationKind, source.Kind);
        Assert.AreEqual("straight-rail-train", source.ConfigurationId);
        Assert.AreEqual(1, source.ConfigurationVersion);
        Assert.AreEqual("slice-4-straight-rail-v1", source.Parameter("base.generator_version"));
        Assert.AreEqual("4", source.Parameter("base.seed"));
        Assert.AreEqual("320,32,32", source.Parameter("base.bounds"));
        Assert.AreEqual("2", source.Parameter("railway.segments"));
        Assert.AreEqual("257", source.Parameter("railway.sleepers"));
        Assert.AreEqual("core:dev-locomotive", source.Parameter("train.locomotive"));
        Assert.AreEqual("core:dev-carriage", source.Parameter("train.carriage"));
        Assert.IsFalse(source.EffectiveParameters.Any(item => item.Name.Contains("step", StringComparison.Ordinal)));

        var rail = source.ContentDependencies.Single(item => item.Kind == "voxel" && item.Id == "core:rail");
        Assert.AreEqual(content.Catalog.Current!.Entries[new VoxelDefinitionId("core", "rail")].SemanticRevision, rail.SemanticRevision);
        var loco = source.ContentDependencies.Single(item => item.Kind == "composite" && item.Id == "core:dev-locomotive");
        Assert.AreEqual(content.Composites.Current.Composites[StaticTrainFixture.LocomotiveId].SemanticRevision, loco.SemanticRevision);
        CollectionAssert.AreEqual(
            source.ContentDependencies.OrderBy(item => item.Kind, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray(),
            source.ContentDependencies.ToArray());
    }

    [TestMethod]
    public void Unknown_configuration_and_missing_content_are_rejected_without_a_session()
    {
        using var content = PlaygroundHarness.Content();
        Assert.AreEqual(
            PlaygroundEstablishStatus.UnknownConfiguration,
            PlaygroundSession.TryEstablish("no-such-config", content.Catalog, content.Composites, content.Store, out var unknown));
        Assert.IsNull(unknown);

        using var withoutRail = PlaygroundHarness.Content(PlaygroundHarness.PackageWithoutRail());
        Assert.AreEqual(
            PlaygroundEstablishStatus.MissingContent,
            PlaygroundSession.TryEstablish(PlaygroundConfigurations.StraightRailTrainId, withoutRail.Catalog, withoutRail.Composites, withoutRail.Store, out var missing));
        Assert.IsNull(missing);
    }

    [TestMethod]
    public void Established_configuration_is_a_formal_world_paused_with_queryable_rail_train_and_collision()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = playground.Session;

        Assert.IsTrue(session.SimulationPaused);
        Assert.AreEqual(0UL, session.SimulationStep);
        Assert.AreEqual("straight-rail-train", session.WorldSource!.ConfigurationId);
        Assert.AreEqual(VoxelWorldIdentity.Slice4, session.Identity);
        Assert.AreEqual(2, session.Rails.Segments.Count);
        Assert.AreEqual(257, session.QueryRails().SleeperCount);
        Assert.AreEqual(VoxelCollisionQueryStatus.Hit, session.QueryCollision(StraightRailFixture.LeftRailSample).Status);
        var assembly = session.QueryTrainAssemblies().Single();
        Assert.AreEqual(80.0, assembly.Vehicles[0].FrontContact.X, Tolerance);
        Assert.AreEqual(2, session.QueryComposites().Count);
        Assert.AreEqual(0UL, session.CollectHostSteps(1.0));
        Assert.AreEqual(0UL, session.SimulationStep);
    }

    [TestMethod]
    public void Pausing_keeps_speed_traction_eligibility_and_pending_commands()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = playground.Session;
        Assert.IsTrue(session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(session.AdvanceSimulation(300).Succeeded);
        Assert.IsTrue(session.StopTrain(400).Succeeded);
        var before = WorldSaveAuthority.Fingerprint(session);

        Assert.IsTrue(session.ResumeSimulation().Succeeded);
        Assert.IsTrue(session.PauseSimulation().Succeeded);

        Assert.AreEqual(before, WorldSaveAuthority.Fingerprint(session));
        var assembly = session.QueryTrainAssemblies().Single();
        Assert.AreEqual(4.0, assembly.Speed, Tolerance);
        Assert.IsTrue(assembly.Running);
        Assert.AreEqual(TrainHaltReason.None, assembly.HaltReason);
        Assert.AreEqual(400UL, session.QueryScheduledTrainCommands().Single().AtStep);
        Assert.AreEqual(0UL, session.CollectHostSteps(2.0));
        Assert.AreEqual(300UL, session.SimulationStep);
        Assert.AreEqual(before, WorldSaveAuthority.Fingerprint(session));
    }

    [TestMethod]
    public void Single_and_n_step_advance_exactly_while_paused_and_run_collects_host_steps()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = playground.Session;
        Assert.IsTrue(session.StartTrain(RailHeading.AlongAlignment).Succeeded);

        Assert.AreEqual(1UL, session.AdvanceSimulation(1).AdvancedSteps);
        Assert.AreEqual(1UL, session.SimulationStep);
        Assert.AreEqual(299UL, session.AdvanceSimulation(299).AdvancedSteps);
        Assert.AreEqual(300UL, session.SimulationStep);
        Assert.IsTrue(session.SimulationPaused);
        Assert.AreEqual(96.0, session.QueryTrainAssemblies().Single().Vehicles[0].FrontContact.X, Tolerance);

        Assert.IsTrue(session.ResumeSimulation().Succeeded);
        var collected = session.CollectHostSteps(0.5);
        Assert.AreEqual(30UL, collected);
        Assert.IsTrue(session.AdvanceSimulation(collected).Succeeded);
        Assert.AreEqual(330UL, session.SimulationStep);
    }

    [TestMethod]
    public void World_without_vehicles_establishes_and_advances_on_the_global_clock()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailEmptyId);
        var session = playground.Session;

        Assert.IsTrue(session.SimulationPaused);
        Assert.AreEqual(0, session.QueryComposites().Count);
        Assert.AreEqual(0, session.QueryTrainAssemblies().Count);
        Assert.AreEqual(2, session.Rails.Segments.Count);
        Assert.AreEqual(TrainMotionStatus.Advanced, session.AdvanceSimulation(1).Status);
        Assert.AreEqual(TrainMotionStatus.Advanced, session.AdvanceSimulation(119).Status);
        Assert.AreEqual(120UL, session.SimulationStep);
        Assert.IsTrue(session.ResumeSimulation().Succeeded);
        Assert.AreEqual(TrainMotionStatus.Advanced, session.AdvanceSimulation(session.CollectHostSteps(1.0)).Status);
        Assert.AreEqual(180UL, session.SimulationStep);
    }

    [TestMethod]
    public void Step_overflow_is_rejected_without_changing_the_session()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailEmptyId);
        var session = playground.Session;
        Assert.IsTrue(session.AdvanceSimulation(5).Succeeded);
        var before = WorldSaveAuthority.Fingerprint(session);

        Assert.AreEqual(TrainMotionStatus.StepOverflow, session.AdvanceSimulation(ulong.MaxValue).Status);

        Assert.AreEqual(5UL, session.SimulationStep);
        Assert.AreEqual(before, WorldSaveAuthority.Fingerprint(session));
    }

    [TestMethod]
    public void Registered_legal_and_rejection_sequences_pass_on_fresh_sessions()
    {
        using var content = PlaygroundHarness.Content();
        foreach (var id in new[] { PlaygroundSequences.StraightRailRunAndHaltId, PlaygroundSequences.StraightRailRejectionsId })
        {
            var report = PlaygroundValidation.Run(PlaygroundSequences.Find(id)!, content.Catalog, content.Composites);
            Assert.IsTrue(report.Passed, string.Join(Environment.NewLine, report.Lines()));
        }

        var rejections = PlaygroundSequences.Find(PlaygroundSequences.StraightRailRejectionsId)!;
        CollectionAssert.IsSubsetOf(
            new[] { "ReverseUnsupported", "DisconnectedPath", "StepOverflow" },
            rejections.Steps.Select(step => step.ExpectedStatus).ToArray());
    }

    [TestMethod]
    public void Automatic_runs_and_manual_operations_on_the_same_configuration_reach_the_same_state()
    {
        using var content = PlaygroundHarness.Content();
        var sequence = PlaygroundSequences.Find(PlaygroundSequences.StraightRailRunAndHaltId)!;
        var first = PlaygroundValidation.Run(sequence, content.Catalog, content.Composites);
        var second = PlaygroundValidation.Run(sequence, content.Catalog, content.Composites);
        CollectionAssert.AreEqual(first.Lines().ToArray(), second.Lines().ToArray());

        Assert.AreEqual(
            PlaygroundEstablishStatus.Established,
            PlaygroundSession.TryEstablish(sequence.ConfigurationId, content.Catalog, content.Composites, content.Store, out var manual));
        content.Attach(manual!);
        foreach (var step in sequence.Steps)
            Assert.AreEqual(step.ExpectedStatus, step.Operation.Apply(manual!));
        Assert.AreEqual(first.FinalFingerprint, WorldSaveAuthority.Fingerprint(manual!));
    }

    [TestMethod]
    public void Checkpoint_or_status_mismatch_fails_the_report_and_wrong_configuration_is_refused()
    {
        using var content = PlaygroundHarness.Content();
        var wrong = new PlaygroundSequence(
            "wrong-expectation",
            PlaygroundConfigurations.StraightRailTrainId,
            new[]
            {
                new PlaygroundValidationStep(PlaygroundOperation.Advance(10), "Advanced",
                    new[] { new PlaygroundCheck(PlaygroundObservation.SimulationStep, "11") }),
                new PlaygroundValidationStep(PlaygroundOperation.Start(RailHeading.AgainstAlignment), "Started",
                    Array.Empty<PlaygroundCheck>())
            });

        var report = PlaygroundValidation.Run(wrong, content.Catalog, content.Composites);

        Assert.IsFalse(report.Passed);
        Assert.AreEqual("10", report.Steps[0].Checks.Single().Observed);
        Assert.AreEqual("ReverseUnsupported", report.Steps[1].ActualStatus);

        Assert.AreEqual(
            PlaygroundEstablishStatus.Established,
            PlaygroundSession.TryEstablish(PlaygroundConfigurations.StraightRailEmptyId, content.Catalog, content.Composites, null, out var empty));
        content.Attach(empty!);
        var refused = PlaygroundValidation.Execute(wrong, empty!);
        Assert.AreEqual(PlaygroundEstablishStatus.UnknownConfiguration, refused.EstablishStatus);
        Assert.AreEqual(0UL, empty!.SimulationStep);
    }

    [TestMethod]
    public void Source_survives_ordinary_save_and_restore_in_a_new_session()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        var session = playground.Session;
        Assert.IsTrue(session.StartTrain(RailHeading.AlongAlignment).Succeeded);
        Assert.IsTrue(session.AdvanceSimulation(300).Succeeded);
        var source = session.WorldSource!;
        var fingerprint = WorldSaveAuthority.Fingerprint(session);
        var saved = session.Save("Playground Save");
        Assert.AreEqual(WorldSaveStatus.Saved, saved.Status);
        var json = File.ReadAllText(playground.Store.PathFor(saved.SaveId!.Value));
        StringAssert.Contains(json, "\"world_source\"");
        StringAssert.Contains(json, "\"save_format_version\":3");

        using var restarted = PlaygroundHarness.Content(userData: playground.UserData);
        var loaded = new VoxelDevelopmentSession(restarted.Catalog, VoxelWorldIdentity.Slice4, restarted.Store, restarted.Composites);
        restarted.Attach(loaded);
        Assert.AreEqual(WorldSaveStatus.Loaded, loaded.Load(saved.SaveId!.Value).Status);

        Assert.AreEqual(source, loaded.WorldSource);
        Assert.IsTrue(loaded.SimulationPaused);
        Assert.AreEqual(300UL, loaded.SimulationStep);
        Assert.AreEqual(fingerprint, WorldSaveAuthority.Fingerprint(loaded));
        Assert.IsTrue(loaded.QueryTrainAssemblies().Single().Running);
    }

    [TestMethod]
    public void Unknown_source_kind_is_rejected_and_saves_without_source_still_load_unpaused()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailEmptyId);
        var saved = playground.Session.Save("Source Kind");
        var path = playground.Store.PathFor(saved.SaveId!.Value);
        var json = File.ReadAllText(path);
        File.WriteAllText(path, json.Replace("\"kind\":\"fixed_configuration\"", "\"kind\":\"generated\"", StringComparison.Ordinal));
        var before = WorldSaveAuthority.Fingerprint(playground.Session);

        Assert.AreEqual(WorldSaveStatus.UnknownSource, playground.Session.Load(saved.SaveId!.Value).Status);
        Assert.AreEqual(before, WorldSaveAuthority.Fingerprint(playground.Session));

        var legacy = new VoxelDevelopmentSession(playground.Catalog, VoxelWorldIdentity.Slice4, playground.Store, playground.Composites);
        playground.Attach(legacy);
        Assert.IsTrue(legacy.ConstructRails().Succeeded);
        var legacySave = legacy.Save("Legacy");
        Assert.IsFalse(File.ReadAllText(playground.Store.PathFor(legacySave.SaveId!.Value)).Contains("world_source", StringComparison.Ordinal));
        Assert.AreEqual(WorldSaveStatus.Loaded, legacy.Load(legacySave.SaveId!.Value).Status);
        Assert.IsNull(legacy.WorldSource);
        Assert.IsFalse(legacy.SimulationPaused);
    }

    [TestMethod]
    public void Reset_drops_the_source_so_later_saves_do_not_claim_the_configuration()
    {
        using var playground = PlaygroundHarness.Establish(PlaygroundConfigurations.StraightRailTrainId);
        playground.Session.Reset();
        Assert.IsNull(playground.Session.WorldSource);
        Assert.IsFalse(playground.Session.SimulationPaused);
    }
}

internal sealed class PlaygroundHarness : IDisposable
{
    private readonly LoggerSession _logger;

    public string UserData { get; }
    public FileWorldSaveStore Store { get; }
    public MemoryContentSource Source { get; }
    public ContentReloader Reloader { get; }
    public VoxelCatalog Catalog => Reloader.Catalog;
    public CompositeCatalog Composites => Reloader.Composites;
    public VoxelDevelopmentSession? EstablishedSession { get; private set; }
    public VoxelDevelopmentSession Session => EstablishedSession ?? throw new InvalidOperationException("No Playground session.");
    private readonly bool _ownsUserData;

    private PlaygroundHarness(LoggerSession logger, string userData, MemoryContentSource source, ContentReloader reloader, bool ownsUserData)
    {
        _logger = logger;
        UserData = userData;
        Store = new FileWorldSaveStore(userData);
        Source = source;
        Reloader = reloader;
        _ownsUserData = ownsUserData;
    }

    public static PlaygroundHarness Content(MemoryContentSource? source = null, string? userData = null)
    {
        var logger = LoggerSession.Create();
        var content = source ?? StaticTrainHarness.Package();
        var reloader = ContentFixtures.Create(content, logger.Logger);
        Assert.AreEqual(ContentReloadStatus.Committed, reloader.Reload().Status);
        var directory = userData ?? Directory.CreateTempSubdirectory("train-playground-").FullName;
        return new PlaygroundHarness(logger, directory, content, reloader, userData is null);
    }

    public static PlaygroundHarness Establish(string configurationId, string? userData = null)
    {
        var harness = Content(userData: userData);
        Assert.AreEqual(
            PlaygroundEstablishStatus.Established,
            PlaygroundSession.TryEstablish(configurationId, harness.Catalog, harness.Composites, harness.Store, out var session));
        harness.Attach(session!);
        return harness;
    }

    public void Attach(VoxelDevelopmentSession session)
    {
        EstablishedSession?.Detach();
        EstablishedSession = session;
        Reloader.SetWorldReloadAdapter(VoxelWorldReloadAdapter.For(session));
    }

    public static MemoryContentSource PackageWithoutRail() => CompositeFixtures.FormalFixturePackage();

    public void Dispose()
    {
        EstablishedSession?.Detach();
        _logger.Dispose();
        if (_ownsUserData && Directory.Exists(UserData))
            Directory.Delete(UserData, true);
    }
}
