using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Train.Core.Contracts;
using Train.Core.ContractSamples;

var broken = args.Contains("--mutable-snapshot");
try
{
    SharedCases.Run(new ScenarioSession(broken), new ScenarioFactory());
    ScenarioSession.CheckUnconfiguredCall();
    ScenarioSession.CheckOwnRecordingPayloads();
    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name is "Train.Core.Contracts" or "Train.Core.ContractSamples" or "Core"))
        Console.WriteLine($"loaded-assembly={assembly.FullName}; path={assembly.Location}");
    Console.WriteLine("PASS core finite scenario; shared assertions; real producer unavailable");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

static class SharedCases
{
    public static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("CONTRACT ASSERTION: " + message);
    }
    public static void Run(ICoreSession session, ICoreFactory factory)
    {
        var old = session.Read();
        var outputs = ConsumerExamples.ReadBatchOutputs(new ProcessProjection(
            new ProcessDetails.Extraction(new("core:extract"), new(ScenarioData.Good, 1)), new(ScenarioData.SemanticHash),
            1, 0, 0, FacilityTag.Extraction, ImmutableArray<TerrainTypeId>.Empty));
        Require(outputs.Length == 1 && outputs[0].Quantity == 1, "public process input is consumable");
        var boundary = ConsumerExamples.ReadSameBoundary(session, new(1), new WorldLocation.Node(new(1)));
        Require(boundary.Map.WorldSeed == old.World.WorldSeed && boundary.Map.Nodes.Length == 1 &&
            boundary.Map.Nodes[0].Id == new NodeId(1) &&
            boundary.Map.Nodes[0].Terrain.FeatureIds.SequenceEqual(old.World.Nodes[0].Terrain.FeatureIds) &&
            boundary.Scene is Lookup<LocationView>.Found && boundary.Panel is Lookup<TrainView>.Found,
            "consumer reads one fixed boundary");
        var change = ScenarioData.Changes[0];
        var accepted = ConsumerExamples.ApplyAppearanceChange(session, new InstanceId.Vehicle(new(1)), change);
        Require(accepted is SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted }, "design accepted");
        Require(session.Read().InputIdentity == old.InputIdentity, "dynamic design does not change external input identity");
        Require(session.Read().Revision.Value > old.Revision.Value, "accepted design changes persistent revision");
        var acceptedDesign = ((Lookup<InstanceView>.Found)session.Read().FindInstance(new InstanceId.Vehicle(new(1)))).Value.Design;
        Require(acceptedDesign == change.Target.Design.Design, "accepted design is visible");
        var rejected = ConsumerExamples.ApplyAppearanceChange(session, new InstanceId.Vehicle(new(1)), ScenarioData.Changes[1]);
        Require(rejected is SubmissionResult.Completed { Receipt.Decision: CommandDecision.Rejected { Problems: var problems } } && problems[0].Code == "invalid_state",
            "normal rejection is Completed with stable code");
        Require(session.Read().World.WorldSeed == 42, "rejection keeps business boundary");
        Require(session.Read().World.Nodes[0].Terrain.FeatureIds.SequenceEqual(old.World.Nodes[0].Terrain.FeatureIds),
            "rejection preserves nested business values");
        Require(((Lookup<InstanceView>.Found)session.Read().FindInstance(new InstanceId.Vehicle(new(1)))).Value.Design == acceptedDesign,
            "rejection retains accepted design");
        var beforeAdvance = session.Read();
        ConsumerExamples.RunHeadless(session, 1);
        Require(session.Read().Position.CompletedSteps == 1 && session.Read().Revision.Value > beforeAdvance.Revision.Value,
            "advance establishes new persistent boundary");
        Require(session.Read().World.WorldSeed == old.World.WorldSeed, "advance preserves world seed");
        Require(old.Position == new SimulationPosition(0, 0) && old.World.WorldSeed == 42, "retained old boundary unchanged");
        Require(((Lookup<InstanceView>.Found)old.FindInstance(new InstanceId.Vehicle(new(1)))).Value.Design == change.Before,
            "old instance retains preceding design after command and advance");
        Require(old.World.Nodes[0].Terrain.FeatureIds[0] == "old-feature", "retained nested collection unchanged");
        var beforeFault = session.Read();
        var faulted = ConsumerExamples.ApplyAppearanceChange(session, new InstanceId.Vehicle(new(1)), ScenarioData.Changes[2]);
        Require(faulted is SubmissionResult.Completed { Receipt.Decision: CommandDecision.Faulted }, "fault is Completed");
        Require(session.Read().Fault?.Code == "probe_fault" && session.Read().World.WorldSeed == 42 &&
            ((Lookup<InstanceView>.Found)session.Read().FindInstance(new InstanceId.Vehicle(new(1)))).Value.Design == acceptedDesign,
            "fault freezes preceding business boundary");
        Require(session.Read().Revision.Value > beforeFault.Revision.Value, "fault evidence changes persistent revision");
        Require(session.Read().World.Nodes[0].Terrain.FeatureIds.SequenceEqual(beforeFault.World.Nodes[0].Terrain.FeatureIds),
            "fault preserves preceding nested business values");
        Require(ConsumerExamples.CaptureSave(session) is CoreResult<string>.Failure { Problems: var saveProblems } && saveProblems[0].Code == "frozen", "frozen save explicitly fails");
        Require(ConsumerExamples.ApplyAppearanceChange(session, new InstanceId.Vehicle(new(1)), change) is SubmissionResult.NotAccepted,
            "frozen session does not acquire command number");
        VerifyRecordingAndReplay(session, factory);
        session.Dispose();
        Require(old.World.Nodes[0].Terrain.FeatureIds[0] == "old-feature", "dispose preserves retained values");
    }
    static void VerifyRecordingAndReplay(ICoreSession session, ICoreFactory factory)
    {
        var window = session.ReadRecording();
        Require(window.Commands.Length == 3 && window.EndPosition == session.Read().Position,
            "recording retains all Completed commands and precise cutoff");
        Require(window.Commands[0].Result.Decision is CommandDecision.Accepted &&
            window.Commands[1].Result.Decision is CommandDecision.Rejected &&
            window.Commands[2].Result.Decision is CommandDecision.Faulted, "recording distinguishes all three decisions");
        for (var index = 0; index < 3; index++)
        {
            var expected = ScenarioData.Changes[index].Target;
            Require(window.RequiredReferences.Designs.Contains(expected.Design.Design) && window.RequiredReferences.Inputs.Contains(expected.InputIdentity),
                "each Completed input remains declared for dependency handoff");
            Require(window.Commands[index].Result.Number == (ulong)index + 1 &&
                window.Commands[index].InputBefore == expected.InputIdentity && window.Commands[index].InputAfter == expected.InputIdentity,
                "recording retains command identity and external input identity");
        }
        var replayInput = new CoreReplayInput(2, ScenarioData.Runtime, window.Starts[0].State.Encode(), window.Commands,
            window.Checkpoints, window.EndPosition, session.Read().Fault);
        var prepared = factory.PrepareReplay(replayInput, ScenarioData.Runtime, [ScenarioData.Content]);
        Require(prepared is CoreResult<ICoreReplay>.Success, "public factory prepares fixed replay from retained dependencies");
        using var replay = ((CoreResult<ICoreReplay>.Success)prepared).Value;
        var firstStop = ConsumerExamples.InspectRecordedCommand(replay, window.Commands[0].Result.After);
        Require(firstStop.Stop == ReplayStop.RequestedPosition && firstStop.Position == new SimulationPosition(0, 1) && replay.Read().Fault is null,
            "replay stops after accepted command in same step");
        var replayDesign = ((Lookup<InstanceView>.Found)replay.Read().FindInstance(new InstanceId.Vehicle(new(1)))).Value.Design;
        Require(replayDesign == ScenarioData.Changes[0].Target.Design.Design, "replay acceptance updates observable design");
        var retainedView = replay.Read();
        var rejectionStop = ConsumerExamples.InspectRecordedCommand(replay, window.Commands[1].Result.After);
        Require(rejectionStop.Stop == ReplayStop.RequestedPosition && rejectionStop.Position == new SimulationPosition(0, 2) && replay.Read().Fault is null,
            "replay includes rejected command and respects exact stop");
        Require(((Lookup<InstanceView>.Found)replay.Read().FindInstance(new InstanceId.Vehicle(new(1)))).Value.Design == replayDesign,
            "replay rejection preserves accepted design");
        var last = replay.Advance(1);
        Require(last.Stop == ReplayStop.RecordedFault && last.Position == window.EndPosition && last.Fault == session.Read().Fault,
            "replay traverses completed step then stops at recorded fault");
        Require(retainedView.Position == new SimulationPosition(0, 1), "retained replay view remains fixed");
        Require(replay.CaptureBoundary().Position == window.EndPosition, "public replay boundary capture is available");
        Require(factory.PrepareReplay(replayInput, ScenarioData.Runtime, []) is CoreResult<ICoreReplay>.Failure { Problems: var missing } &&
            missing[0].Code == "content_missing", "missing replay dependencies fail explicitly");
        Console.WriteLine("PASS public recording/dependency handoff and replay stops");
    }
}

static class ScenarioData
{
    public static readonly string InitialDesign = new('a', 64);
    public static readonly string Composition = new('e', 64);
    public static readonly string ExternalInput = new('1', 64);
    public static readonly string SemanticHash = new('f', 64);
    public static GoodId Good { get; } = new GoodId.Voxel(new("core:steel"));
    public static ImmutableArray<DesignChangeProjection> Changes { get; } =
        [CreateChange(new('b', 64), InitialDesign), CreateChange(new('c', 64), new('b', 64)), CreateChange(new('d', 64), new('b', 64))];
    static DesignChangeProjection CreateChange(string hash, string before)
    {
        var goods = ImmutableArray.Create(new GoodsQuantity(Good, 1));
        var design = new DesignProjection(new(hash), new(Composition), new DesignKind.Vehicle(SizeClass.Small, 1),
            ImmutableArray<PartProjection>.Empty, 1, 1, 1, new(1), goods, goods, 1,
            ImmutableArray.Create(new DesignReturnUnit(Good, 1, ImmutableArray<PartId>.Empty, goods, ImmutableArray<MissingCoreReference>.Empty)),
            true, ImmutableArray<MissingCoreReference>.Empty);
        return new(new(before), new(Composition), new(new(ExternalInput), design, ImmutableArray<GoodDefinitionProjection>.Empty),
            ImmutableArray<PartCorrespondence>.Empty, ImmutableArray<GoodsQuantity>.Empty, ImmutableArray<DesignReturnUnit>.Empty, 0);
    }
    public static RuntimeIdentity Runtime { get; } = new("probe", "fixed-scenario", "Windows", "X64", "net10.0");
    public static CoreContentProjection Content { get; } = new(new(ExternalInput),
        [new(Good, new(SemanticHash), new(1), 1, true, null)],
        Changes.Select(c => c.Target.Design).Prepend(Changes[0].Target.Design with { Design = new(InitialDesign) }).ToImmutableArray(),
        [], [], [], [], [], [new(new("core:railway"), new(SemanticHash), 1, 1, 1, new("core:steel"), 1, 1, 1, 1)], [], [], [], [], [], []);
    public static CoreReferenceManifest References(int commandCount) => new(
        Changes.Take(commandCount).Select(c => c.Target.Design.Design).Prepend(new DesignHash(InitialDesign)).ToImmutableArray(), [],
        [new(new CoreDefinitionReference.Voxel(new("core:steel")), null, "fixture.design", false),
            new(new CoreDefinitionReference.Rule(new("core:railway")), null, "fixture.world", false)], [new(ExternalInput)]);
    // 固定夹具携带完整公开输入；生产核心的命令编码器仍待实现。
    static JsonSerializerOptions FixtureOptions { get; } = CreateFixtureOptions();
    static JsonSerializerOptions CreateFixtureOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(GoodId)) info.PolymorphismOptions = new() { DerivedTypes = { new JsonDerivedType(typeof(GoodId.Voxel), "voxel") } };
            if (info.Type == typeof(DesignKind)) info.PolymorphismOptions = new() { DerivedTypes = { new JsonDerivedType(typeof(DesignKind.Vehicle), "vehicle") } };
            if (info.Type == typeof(InstanceId)) info.PolymorphismOptions = new() { DerivedTypes = { new JsonDerivedType(typeof(InstanceId.Vehicle), "vehicle") } };
        });
        return new() { TypeInfoResolver = resolver };
    }
    public static string FixturePayload(int index) => JsonSerializer.Serialize(new CoreCommand.ApplyInstanceDesign(new InstanceId.Vehicle(new(1)), Changes[index]), FixtureOptions);
    public static CoreCommand.ApplyInstanceDesign DecodeFixtureCommand(string payload) => JsonSerializer.Deserialize<CoreCommand.ApplyInstanceDesign>(payload, FixtureOptions)!;
}

sealed class ScenarioSession(bool broken) : ICoreSession
{
    readonly string[] features = ["old-feature"];
    ulong commands;
    bool disposed;
    bool initialized;
    ulong revision;
    ImmutableArray<RecordedCommand> recording = [];
    Snapshot snapshot = Snapshot.Create(new(0, 0), 0, null, ["old-feature"]);
    public ICoreReadView Read()
    {
        Check();
        if (broken && !initialized) snapshot = Snapshot.Create(new(0, 0), 0, null, ImmutableCollectionsMarshal.AsImmutableArray(features));
        initialized = true;
        return snapshot;
    }
    public SubmissionResult Submit(CoreCommand command, CommandSource source)
    {
        Check();
        if (snapshot.Fault is not null) return new SubmissionResult.NotAccepted(Problem("frozen"));
        if (command is not CoreCommand.ApplyInstanceDesign { Instance: InstanceId.Vehicle { Id.Value: 1 } } design ||
            commands >= 3 || design.Change != ScenarioData.Changes[(int)commands] || source != CommandSource.UserInterface)
            throw new InvalidOperationException("UNCONFIGURED: command/input/source");
        var before = snapshot.Position;
        var after = before with { Sequence = before.Sequence + 1 };
        CommandDecision decision = ++commands switch
        {
            1 => new CommandDecision.Accepted([], []),
            2 => new CommandDecision.Rejected([Problem("invalid_state")]),
            3 => new CommandDecision.Faulted(new("probe_fault", before, "Submit", "scenario", null)),
            _ => throw new InvalidOperationException("UNCONFIGURED: command sequence")
        };
        snapshot = Snapshot.Create(after, ++revision, (decision as CommandDecision.Faulted)?.Fault, ["old-feature"], ScenarioData.Changes[0].Target.Design.Design);
        var receipt = new CommandReceipt(commands, before, after, decision);
        recording = recording.Add(new(source, ScenarioData.FixturePayload((int)commands - 1), receipt, snapshot.InputIdentity, snapshot.InputIdentity));
        return new SubmissionResult.Completed(receipt, [], null);
    }
    public AdvanceResult Advance(uint steps)
    {
        Check();
        if (steps != 1 || commands != 2) throw new InvalidOperationException("UNCONFIGURED: advance");
        if (broken) features[0] = "mutated-feature";
        snapshot = Snapshot.Create(new(1, 0), ++revision, null, ["old-feature"], ScenarioData.Changes[0].Target.Design.Design);
        return new(1, snapshot.Position, [], null, null);
    }
    public CoreResult<ICoreCapture> Capture(CapturePurpose purpose)
    {
        Check();
        if (purpose == CapturePurpose.Save && snapshot.Fault is not null) return new CoreResult<ICoreCapture>.Failure([Problem("frozen")]);
        throw new InvalidOperationException("UNCONFIGURED: capture");
    }
    public void Dispose() => disposed = true;
    void Check() { ObjectDisposedException.ThrowIf(disposed, this); }
    static CoreProblem Problem(string code) => new(code, "scenario", null, []);
    public CoreRecordingWindow ReadRecording()
    {
        Check();
        var references = ScenarioData.References((int)commands);
        var start = new RecordingStart(new(1), new ScenarioCapture(Snapshot.Create(new(0, 0), 0, null, ["old-feature"]), ScenarioData.References(0)));
        return new(snapshot.Position, [start], recording, [], snapshot.Fault is null ? [] : [new(start, recording, [], snapshot.Fault, references)], [], references);
    }
    public CoreResult<RecordingStart> CaptureRecordingStart() => throw new InvalidOperationException("UNCONFIGURED: recording start");
    public RecordingMaintenance ConfirmRecordingStart(RecordingPointId point, RecordingWriteOutcome outcome) => throw new InvalidOperationException("UNCONFIGURED: recording confirmation");
    public static void CheckUnconfiguredCall()
    {
        using var session = new ScenarioSession(false);
        try { session.Submit(new CoreCommand.StopTrain(new(1)), CommandSource.UserInterface); }
        catch (InvalidOperationException e) when (e.Message.StartsWith("UNCONFIGURED:")) { Console.WriteLine("PASS unconfigured call explicitly fails"); return; }
        throw new InvalidOperationException("CONTRACT ASSERTION: unconfigured call unexpectedly succeeded");
    }
    public static void CheckOwnRecordingPayloads()
    {
        using var session = new ScenarioSession(false);
        session.Submit(new CoreCommand.ApplyInstanceDesign(new InstanceId.Vehicle(new(1)), ScenarioData.Changes[0]), CommandSource.UserInterface);
        session.Submit(new CoreCommand.ApplyInstanceDesign(new InstanceId.Vehicle(new(1)), ScenarioData.Changes[1]), CommandSource.UserInterface);
        session.Advance(1);
        session.Submit(new CoreCommand.ApplyInstanceDesign(new InstanceId.Vehicle(new(1)), ScenarioData.Changes[2]), CommandSource.UserInterface);
        var window = session.ReadRecording();
        for (var index = 0; index < 3; index++)
        {
            var retained = ScenarioData.DecodeFixtureCommand(window.Commands[index].CanonicalPayload).Change.Target;
            var expected = ScenarioData.Changes[index].Target;
            SharedCases.Require(retained.InputIdentity == expected.InputIdentity && retained.Design.Design == expected.Design.Design &&
                retained.Design.Composition == expected.Design.Composition && retained.Design.Kind == expected.Design.Kind &&
                retained.Design.MassTons == expected.Design.MassTons && retained.Design.MaxStructureDurability == expected.Design.MaxStructureDurability &&
                retained.Design.StructureHitWeight == expected.Design.StructureHitWeight && retained.Design.StructuralBaseValue == expected.Design.StructuralBaseValue &&
                retained.Design.BillOfMaterials.SequenceEqual(expected.Design.BillOfMaterials) &&
                retained.Design.BillOfMaterialsItemCount == expected.Design.BillOfMaterialsItemCount &&
                retained.Design.StructuralGoods.SequenceEqual(expected.Design.StructuralGoods) &&
                retained.Design.Parts.SequenceEqual(expected.Design.Parts) && retained.Design.CanConstruct == expected.Design.CanConstruct &&
                retained.Design.MissingReferences.SequenceEqual(expected.Design.MissingReferences) &&
                retained.AssemblyGoods.SequenceEqual(expected.AssemblyGoods) && retained.Design.ReturnUnits.Length == 1 &&
                retained.Design.ReturnUnits[0].Good == expected.Design.ReturnUnits[0].Good &&
                retained.Design.ReturnUnits[0].Quantity == expected.Design.ReturnUnits[0].Quantity &&
                retained.Design.ReturnUnits[0].Parts.SequenceEqual(expected.Design.ReturnUnits[0].Parts) &&
                retained.Design.ReturnUnits[0].StructuralGoods.SequenceEqual(expected.Design.ReturnUnits[0].StructuralGoods) &&
                retained.Design.ReturnUnits[0].MissingReferences.SequenceEqual(expected.Design.ReturnUnits[0].MissingReferences),
                "substitute self-check retains complete DesignInput for accepted/rejected/faulted command");
        }
        Console.WriteLine("PASS substitute self-check complete DesignInput fixture recording");
    }
}

sealed class ScenarioCapture(ICoreReadView view, CoreReferenceManifest references) : ICoreCapture
{
    public SimulationPosition Position => view.Position;
    public CoreRevision Revision => view.Revision;
    public CoreInputId InputIdentity => view.InputIdentity;
    public CoreReferenceManifest References => references;
    public CoreCheckpoint Checkpoint
    {
        get
        {
            var text = $"fixed-scenario-boundary:{Position.CompletedSteps}:{Position.Sequence}:{Revision.Value}";
            return new(Position, text, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))));
        }
    }
    // 只提供不透明情景标识，不实现生产存档编解码。
    public string Encode() => Position == new SimulationPosition(0, 0) ? "fixed-scenario-start" : "fixed-scenario-end";
}

sealed class ScenarioFactory : ICoreFactory
{
    public CoreResult<ICoreReplay> PrepareReplay(CoreReplayInput input, RuntimeIdentity environment,
        ImmutableArray<CoreContentProjection> contentHistory)
    {
        if (input.FormatVersion != 2 || input.OriginalRuntime != ScenarioData.Runtime || environment != ScenarioData.Runtime)
            return new CoreResult<ICoreReplay>.Failure([new("incompatible_replay", "fixture.runtime", null, [])]);
        var required = ScenarioData.References(3);
        if (contentHistory.Length != 1 || contentHistory[0].Identity != new CoreInputId(ScenarioData.ExternalInput) ||
            required.Designs.Any(hash => !contentHistory[0].Designs.Any(design => design.Design == hash)) ||
            !contentHistory[0].Goods.Any(good => good.Good == ScenarioData.Good) ||
            !contentHistory[0].RailwayRules.Any(rule => rule.Id == new RuleDefinitionId("core:railway")))
            return new CoreResult<ICoreReplay>.Failure([new("content_missing", "fixture.dependencies", null, [])]);
        if (input.InitialStatePayload != "fixed-scenario-start" || input.Commands.Length != 3 ||
            input.Commands.Where((command, index) => command.CanonicalPayload != ScenarioData.FixturePayload(index) ||
                command.Result.Number != (ulong)index + 1 || command.InputBefore != new CoreInputId(ScenarioData.ExternalInput) ||
                command.InputAfter != new CoreInputId(ScenarioData.ExternalInput)).Any() || input.Checkpoints.Length != 0 ||
            input.Commands[0].Result is not { Before.CompletedSteps: 0, Before.Sequence: 0, After.CompletedSteps: 0, After.Sequence: 1, Decision: CommandDecision.Accepted } ||
            input.Commands[1].Result is not { Before.CompletedSteps: 0, Before.Sequence: 1, After.CompletedSteps: 0, After.Sequence: 2, Decision: CommandDecision.Rejected } ||
            input.Commands[2].Result is not { Before.CompletedSteps: 1, Before.Sequence: 0, After.CompletedSteps: 1, After.Sequence: 1, Decision: CommandDecision.Faulted } ||
            input.EndPosition != new SimulationPosition(1, 1) || input.ExpectedFault != (input.Commands[2].Result.Decision as CommandDecision.Faulted)?.Fault)
            throw new InvalidOperationException("UNCONFIGURED: replay input does not match fixed scenario");
        return new CoreResult<ICoreReplay>.Success(new ScenarioReplay(input));
    }
    public CoreResult<ICoreSessionCandidate> PrepareNew(WorldGenerationInput input, CoreContentProjection content, CoreSessionOptions options) => throw Unconfigured();
    public CoreResult<ICoreCapture> DecodeState(string canonicalPayload) => throw Unconfigured();
    public CoreResult<ICoreSessionCandidate> PrepareRestore(ICoreCapture saved, CoreContentProjection content, CoreSessionOptions options) => throw Unconfigured();
    public CoreResult<CheckpointComparison> CompareCheckpoints(CoreCheckpoint expected, CoreCheckpoint actual) => throw Unconfigured();
    static Exception Unconfigured() => new InvalidOperationException("UNCONFIGURED: factory operation");
}

sealed class ScenarioReplay(CoreReplayInput input) : ICoreReplay
{
    bool disposed;
    int boundary;
    Snapshot snapshot = Snapshot.Create(new(0, 0), 0, null, ["old-feature"]);
    public ICoreReadView Read() { Check(); return snapshot; }
    public ReplayAdvanceResult Advance(uint maximumSteps, SimulationPosition? stopAt = null)
    {
        Check();
        if (maximumSteps != 1) throw new InvalidOperationException("UNCONFIGURED: replay budget");
        if (boundary == 0 && stopAt == input.Commands[0].Result.After)
        {
            boundary = 1;
            snapshot = Snapshot.Create(stopAt.Value, 1, null, ["old-feature"], ScenarioData.Changes[0].Target.Design.Design);
            return new(ReplayStop.RequestedPosition, snapshot.Position, [], null, null);
        }
        if (boundary == 1 && stopAt == input.Commands[1].Result.After)
        {
            boundary = 2;
            snapshot = Snapshot.Create(stopAt.Value, 2, null, ["old-feature"], ScenarioData.Changes[0].Target.Design.Design);
            return new(ReplayStop.RequestedPosition, snapshot.Position, [], null, null);
        }
        if (boundary == 2 && stopAt is null)
        {
            boundary = 3;
            snapshot = Snapshot.Create(input.EndPosition, 4, input.ExpectedFault, ["old-feature"], ScenarioData.Changes[0].Target.Design.Design);
            return new(ReplayStop.RecordedFault, snapshot.Position, [], null, input.ExpectedFault);
        }
        throw new InvalidOperationException("UNCONFIGURED: replay operation/stop sequence");
    }
    public ICoreCapture CaptureBoundary()
    {
        Check();
        if (boundary != 3) throw new InvalidOperationException("UNCONFIGURED: replay capture boundary");
        return new ScenarioCapture(snapshot, ScenarioData.References(3));
    }
    public void Dispose() => disposed = true;
    void Check() { ObjectDisposedException.ThrowIf(disposed, this); }
}

sealed class Snapshot(SimulationPosition position, CoreRevision revision, WorldSummary world, CoreFault? fault, DesignHash design) : ICoreReadView
{
    public static Snapshot Create(SimulationPosition position, ulong revision, CoreFault? fault, ImmutableArray<string> features, DesignHash? design = null) => new(position, new(revision),
        new(42, new("core:railway"), 100, 100,
            [new(new(1), new(0, 0), new(new("core:plain"), 0, features), new NodeOccupant.Empty(), [], 0, 1, null)], [], [], [], []), fault, design ?? new(ScenarioData.InitialDesign));
    public SimulationPosition Position => position;
    public CoreRevision Revision => revision;
    public CoreInputId InputIdentity => new(ScenarioData.ExternalInput);
    public CoreFault? Fault => fault;
    public WorldSummary World => world;
    public EconomyView Economy => throw Unconfigured();
    public Lookup<LocationView> FindLocation(WorldLocation location) => location is WorldLocation.Node { Id.Value: 1 }
        ? new Lookup<LocationView>.Found(new(location, [new(1)], [new InstanceId.Vehicle(new(1))], [], [], new NodeOccupant.Empty())) : throw Unconfigured();
    public Lookup<TrainView> FindTrain(TrainId id) => id.Value == 1 ? new Lookup<TrainView>.Found(new(id,
        new TrainPosition.Node(new(1)), TrainActivity.Stopped, [new(1)], new(1), 0, 1, 1, 0, 0, 1, null, null, 0,
        new(TargetPriority.ArmedFirst, TargetScope.ArmedOnly, EncounterAction.Retreat, 0.5), [], null, [], new(null, null), TrainOrderFacing.FirstEndLeading)) : throw Unconfigured();
    static Exception Unconfigured() => new InvalidOperationException("UNCONFIGURED: read view query");
    public Lookup<NodeView> FindNode(NodeId id) => throw Unconfigured();
    public Lookup<EdgeView> FindEdge(EdgeId id) => throw Unconfigured();
    public Lookup<InstanceView> FindInstance(InstanceId id) => id is InstanceId.Vehicle { Id.Value: 1 }
        ? new Lookup<InstanceView>.Found(new(id, design, new(ScenarioData.Composition), new WorldLocation.Node(new(1)), SettlementRelation.PlayerOwned,
            new InstanceDetails.Vehicle(SizeClass.Small, new TrainId(1)), InstanceLock.None, new(1, 1), [], [])) : throw Unconfigured();
    public Lookup<SettlementView> FindSettlement(SettlementId id) => throw Unconfigured();
    public Lookup<InventoryView> FindInventory(InventoryId id) => throw Unconfigured();
    public Lookup<LeaseView> FindLease(LeaseId id) => throw Unconfigured();
    public Lookup<FacilityView> FindFacility(FacilityKey key) => throw Unconfigured();
    public Lookup<JobView> FindJob(JobId id) => throw Unconfigured();
    public Lookup<CombatView> FindCombat(CombatId id) => throw Unconfigured();
    public Lookup<CreatureGroupView> FindCreatureGroup(CreatureGroupId id) => throw Unconfigured();
    public Lookup<CreatureView> FindCreature(CreatureId id) => throw Unconfigured();
    public Lookup<LairView> FindLair(LairId id) => throw Unconfigured();
    public CoreResult<TradeQuote> QuoteTrade(TradeQuoteRequest request) => throw Unconfigured();
    public CoreResult<VehicleTradeQuote> QuoteVehicle(VehicleTradeQuoteRequest request) => throw Unconfigured();
    public CoreResult<LeaseQuote> QuoteLease(LeaseRequest request) => throw Unconfigured();
    public CoreResult<JobQuote> GetJobQuote(JobRequest request, JobFunding funding) => throw Unconfigured();
    public CoreResult<SettlementFoundingPreview> PreviewSettlement(NodeId node) => throw Unconfigured();
}
