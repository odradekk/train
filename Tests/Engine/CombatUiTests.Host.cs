using System.Collections.Immutable;
using Train.App;
using Train.App.Logging;
using Train.App.Notifications;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;

namespace Train.Engine.Tests;

public partial class CombatUiTests
{
    private sealed class CombatHost(bool rich)
    {
        private readonly FixedCoreCapture _capture = new(new(new(0, 0), "combat", "combat"),
            HostCoreScenario.References, "combat core", HostCoreScenario.Input, new(1));
        private readonly CoreSessionOptions _options = new(new("fixture", "fixture", "fixture", "fixture", "fixture"), true, 1);
        internal static readonly InstanceId SecondVehicle = new InstanceId.Vehicle(new(12));
        internal GateSession? Gate { get; private set; }
        internal ScriptedCoreSession Session => Gate!.Inner;

        internal HostCoordinator Create(TrainLogger logger, Func<CoreEvent, NotificationAction>? policy = null)
        {
            var core = new ScriptedCoreFactory
            {
                DecodeStateCall = payload => payload == "combat core"
                    ? new CoreResult<ICoreCapture>.Success(_capture)
                    : throw new InvalidOperationException("UNCONFIGURED: core payload"),
                PrepareRestoreCall = (_, projection, _) =>
                {
                    if (projection.Identity != HostCoreScenario.Input)
                        throw new InvalidOperationException("UNCONFIGURED: projection");
                    var session = new ScriptedCoreSession(View(new(0, 0)));
                    session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Success(_capture));
                    session.ReadRecordingCall = () => new(new(0, 0), [new(new(1), _capture)], [], [_capture.Checkpoint], [], [], _capture.References);
                    session.ConfirmRecordingStartCall = (point, _) => point.Value == 1
                        ? new([], []) : throw new InvalidOperationException("UNCONFIGURED: recording point");
                    Gate = new(session);
                    return new CoreResult<ICoreSessionCandidate>.Success(new ScriptedCoreCandidate(Gate, HostCoreScenario.References));
                }
            };
            var content = new ScriptedContentFactory
            {
                CaptureReplayCall = (history, _) => new ContentResult<IContentReplayCapture>.Success(
                    new FixedContentReplayCapture(history, "combat recording")),
                CaptureCall = (_, _) => new ContentResult<IContentCapture>.Success(new FixedContentCapture("combat content")),
                DecodeCaptureCall = payload => payload == "combat content"
                    ? new ContentResult<IContentCapture>.Success(new FixedContentCapture(payload))
                    : throw new InvalidOperationException("UNCONFIGURED: content payload"),
                PrepareLoadCall = (request, _, _) => request.Purpose is ContentLoadPurpose.Restore
                    ? new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(Content()))
                    : throw new InvalidOperationException("UNCONFIGURED: purpose")
            };
            var host = new HostCoordinator(core, content, new ScriptedContentFiles(), new UnconfiguredAssetInspector(),
                new(new("builtin"), new("mods"), null, "combat-ui", []), logger, policy, sessionOptions: _options);
            if (host.BeginRestore("combat core", "combat content", _options).Problem is { } problem)
                throw new InvalidOperationException(problem.Code);
            return host;
        }

        internal FixedCoreReadView View(SimulationPosition position, bool combatPresent = true, bool settlementPresent = true) =>
            rich ? Battlefield(position, combatPresent, settlementPresent) : new(position, HostCoreScenario.Input, null, new(1))
            {
                Economy = UiWorldFixture.Economy,
                World = new(0, new("fixture:railway"), 100, 100, [], [], [], [], [])
            };

        private static FixedCoreReadView Battlefield(SimulationPosition position, bool combatPresent, bool settlementPresent)
        {
            var terrain = new TerrainView(new("fixture:plain"), 0, []);
            NodeOccupant occupant = settlementPresent
                ? new NodeOccupant.Settlement(new(4), SettlementRelation.Hostile) : new NodeOccupant.Empty();
            var settlementNode = new NodeMapItem(new(1), new(250, 300), terrain, occupant, [], 0, 2, null);
            var far = settlementNode with { Id = new(2), Position = new(750, 300), Occupant = new NodeOccupant.Empty(), Platforms = 0 };
            var edge = new EdgeMapItem(new(1), new(1), new(2), 600, terrain, 1, 1, [], []);
            var trainPosition = new TrainPosition.Node(new(1), null, null, null, null, NodeTravelPhase.Waiting, 80, null);
            var train = new TrainView(new(7), trainPosition, TrainActivity.Stopped, [new(11)], new(1),
                0, 20, 10, 100, 0, 30, null, null, 0,
                new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0.5),
                [new("no_route", "route", new WorldObjectId.Train(new(7)), [])], null, [],
                TrainOrderFacing.FirstEndLeading, null);
            var nodeLocation = new WorldLocation.Node(new(1));
            var vehicle = new InstanceView(new InstanceId.Vehicle(new(11)), new("wagon"), new("fixture"),
                nodeLocation, SettlementRelation.PlayerOwned, new InstanceDetails.Vehicle(SizeClass.Medium, new(7)),
                InstanceLock.None, new(100, 100), [], []);
            var building = new InstanceView(new InstanceId.Building(new(13)), new("station"), new("fixture"),
                nodeLocation, SettlementRelation.Hostile, new InstanceDetails.Building(new(1), PlotSize.SmallStation),
                InstanceLock.None, new(100, 100), [], []);
            var edgeLocation = new WorldLocation.Edge(new(1), 100);
            var combat = new CombatView(new(5), edgeLocation, 3,
            [
                new(new CombatUnitId.Instance(vehicle.Id), false, 0, null),
                new(new CombatUnitId.Instance(building.Id), false, 0, null)
            ]);
            var listed = combatPresent
                ? ImmutableArray.Create(new CombatMapItem(combat.Id, edgeLocation))
                : ImmutableArray.Create(new CombatMapItem(new(9), nodeLocation));
            var settlement = new SettlementView(new(4), new(1), new("preview:town"), new("preview:town"),
                SettlementRelation.Hostile, new(2),
                [new(new(1), 1, PlotSize.SmallStation, [BuildingKind.Station], new PlotOccupant.Building(new(13)))],
                [], 0, 2, 0, [], [], [], new(0, 0, 0, 0),
                new(new(new("preview:population"), null, []), 0, 0, 0, 0, new PopulationExecution.NotStarted([]), null, []),
                new(TargetPriority.LowestDurability, TargetScope.ArmedOnly), 0.4, true, [], [], [],
                new(new(new("preview:market"), null, []), new(0, 0, 0, 0, 0, 0, 0), []));
            return new(position, HostCoreScenario.Input, null, new(1))
            {
                Economy = UiWorldFixture.Economy,
                World = new(1, new("fixture:railway"), 1000, 600, [settlementNode, far], [edge],
                    [new(train.Id, trainPosition, train.Activity)], [], listed),
                FoundingPreviews = new Dictionary<NodeId, CoreResult<SettlementFoundingPreview>>
                { [far.Id] = new CoreResult<SettlementFoundingPreview>.Failure([new("content_missing", "ConstructionRule", null, [])]) },
                Nodes = new Dictionary<NodeId, Lookup<NodeView>>
                {
                    [settlementNode.Id] = new Lookup<NodeView>.Found(new(settlementNode, [edge.Id], 80, 0, 80)),
                    [far.Id] = new Lookup<NodeView>.Found(new(far, [edge.Id], 80, 0, 80))
                },
                Edges = new Dictionary<EdgeId, Lookup<EdgeView>>
                {
                    [edge.Id] = new Lookup<EdgeView>.Found(new(edge, [], null))
                },
                Locations = new Dictionary<WorldLocation, Lookup<LocationView>>
                {
                    [nodeLocation] = new Lookup<LocationView>.Found(new(nodeLocation, [train.Id],
                        [vehicle.Id, building.Id], [], [], occupant)),
                    [new WorldLocation.Node(far.Id)] = new Lookup<LocationView>.Found(
                        new(new WorldLocation.Node(far.Id), [], [], [], [], far.Occupant))
                },
                Trains = new Dictionary<TrainId, Lookup<TrainView>> { [train.Id] = new Lookup<TrainView>.Found(train) },
                Instances = new Dictionary<InstanceId, Lookup<InstanceView>>
                {
                    [vehicle.Id] = new Lookup<InstanceView>.Found(vehicle),
                    [SecondVehicle] = new Lookup<InstanceView>.Found(vehicle with { Id = SecondVehicle }),
                    [building.Id] = new Lookup<InstanceView>.Found(building)
                },
                Inventories = new Dictionary<InventoryId, Lookup<InventoryView>>
                { [settlement.Inventory] = new Lookup<InventoryView>.Found(new(settlement.Inventory,
                    new InventoryOwner.Settlement(settlement.Id), nodeLocation, InventoryAccess.ReadWrite, 100, 0, 0, 100, 0, [], [])) },
                Settlements = new Dictionary<SettlementId, Lookup<SettlementView>>
                {
                    [settlement.Id] = settlementPresent
                        ? new Lookup<SettlementView>.Found(settlement) : new Lookup<SettlementView>.Missing()
                },
                Combats = new Dictionary<CombatId, Lookup<CombatView>>
                {
                    [combat.Id] = combatPresent
                        ? new Lookup<CombatView>.Found(combat) : new Lookup<CombatView>.Missing(),
                    [new(9)] = new Lookup<CombatView>.Found(new(new(9), nodeLocation, 4, []))
                }
            };
        }
    }

    private sealed class GateSession(ScriptedCoreSession inner) : ICoreSession
    {
        internal ScriptedCoreSession Inner { get; } = inner;
        internal bool ThrowUnimplemented { get; set; }
        public ICoreReadView Read() => Inner.Read();
        public AdvanceResult Advance(uint steps) => Inner.Advance(steps);
        public SubmissionResult Submit(CoreCommand command, CommandSource source)
        {
            if (ThrowUnimplemented && command is CoreCommand.DeclareWar or CoreCommand.OccupySettlement)
                throw new NotImplementedException();
            return Inner.Submit(command, source);
        }
        public CoreResult<ICoreCapture> Capture(CapturePurpose purpose) => Inner.Capture(purpose);
        public CoreRecordingWindow ReadRecording() => Inner.ReadRecording();
        public CoreResult<RecordingStart> CaptureRecordingStart() => Inner.CaptureRecordingStart();
        public RecordingMaintenance ConfirmRecordingStart(RecordingPointId point, RecordingWriteOutcome outcome)
            => Inner.ConfirmRecordingStart(point, outcome);
        public void Dispose() => Inner.Dispose();
    }
}
