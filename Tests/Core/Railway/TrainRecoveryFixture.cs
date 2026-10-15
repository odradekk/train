using System.Collections.Immutable;
using System.Text.Json;
using Train.Core.Contracts;
using Train.Core.Tests.World;
using static Train.Core.Tests.World.WorldFixture;

namespace Train.Core.Tests.Railway;

internal static class TrainRecoveryFixture
{
    internal const int ConsumptionIntervalTicks = 120;
    internal const int StatisticsPeriodTicks = 720;
    internal static CoreContentProjection Content => WorldFixture.Content with
    {
        PopulationRules = [WorldFixture.PopulationRule() with
            { ConsumptionIntervalTicks = ConsumptionIntervalTicks, StatisticsPeriodTicks = StatisticsPeriodTicks }],
        RailwayRules = [WorldFixture.Content.RailwayRules[0] with { BrakingDeceleration = 100, ApproachLengthMeters = 1, DepartureLengthMeters = 1 }],
        SettlementTemplates = WorldFixture.Content.SettlementTemplates.Select(t => t with { PlatformLengthMeters = 2 }).ToImmutableArray(),
        Designs = WorldFixture.Content.Designs.SetItem(0, WorldFixture.Content.Designs[0] with
        { Parts = [WorldFixture.Content.Designs[0].Parts[0] with
            { Capabilities = [new PartCapability.Control(), new PartCapability.Propulsion(100, 30), new PartCapability.Storage(100)] }] })
    };
    internal static RuntimeIdentity Runtime => WorldFixture.Options.Runtime;
    internal static CoreContentProjection ContentFor(string? boundary) => boundary == "waiting" ? Content with
    {
        Identity = new(new string('E', 64)),
        Designs = Content.Designs.Select(design => design.Design == WorldFixture.Station ? design with
        { Parts = design.Parts.Select(part => part with
            { Capabilities = part.Capabilities.Where(c => c is not PartCapability.Platform).ToImmutableArray() }).ToImmutableArray() }
            : design).ToImmutableArray()
    } : Content;

    internal static ICoreSession New(RuntimeIdentity? runtime = null, string? boundary = null)
    {
        var input = Input with { Configuration = Input.Configuration with
        { WidthMeters = 8, HeightMeters = 2, MinimumNodeSpacingMeters = 2, SettlementRatio = 1, LairRatio = 0, HostileSettlementRatio = 0,
            StartConstraints = Input.Configuration.StartConstraints with { RequireStation = boundary != "waiting" } } };
        using var candidate = Success(CoreFactory.Create().PrepareNew(input, ContentFor(boundary), new(runtime ?? Runtime, false, 32)));
        return candidate.Activate();
    }

    internal static TrainView Train(ICoreSession session) =>
        ((Lookup<TrainView>.Found)session.Read().FindTrain(session.Read().World.Trains.Single().Id)).Value;

    internal static void Submit(ICoreSession session, CoreCommand command, List<string>? events = null)
    {
        var result = session.Submit(command, CommandSource.AutomationTest);
        if (result is not
            SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted } completed)
            throw new InvalidOperationException($"铁路命令未成功：{command}；{result}；{Query(session)}");
        events?.AddRange(completed.Events.Select(Event));
    }

    internal static void Configure(ICoreSession session, List<string>? events = null)
    {
        var original = Train(session);
        Submit(session, new CoreCommand.DisbandTrain(original.Id), events);
        Submit(session, new CoreCommand.FormTrain(original.Vehicles), events);
        var train = Train(session);
        var start = ((TrainPosition.Node)train.Position).Id;
        var destinations = session.Read().World.Nodes.Where(node => node.Id != start).OrderBy(node => node.Id.Value).ToArray();
        if (destinations.Length < 2) throw new InvalidOperationException("恢复用例需要至少三个节点。");
        Submit(session, new CoreCommand.ReplaceRunPlan(train.Id, new(false,
            [new PlanStop.Station(destinations[0].Id, 4, []), new PlanStop.Station(destinations[1].Id, 3, []),
             new PlanStop.Station(start, 2, [])])), events);
        Submit(session, new CoreCommand.StartTrain(train.Id), events);
    }

    internal static void Advance(ICoreSession session, List<string>? events = null)
    {
        var result = session.Advance(1);
        if (result.CompletedSteps != 1 || result.Fault is not null)
            throw new InvalidOperationException($"铁路推进失败：{result}");
        events?.AddRange(result.Events.Select(Event));
    }

    internal static void Boundary(ICoreSession session, string boundary, List<string>? events = null)
    {
        for (var step = 0; step < 1000; step++)
        {
            var train = Train(session);
            var reached = boundary switch
            {
                "approaching" => train.Position is TrainPosition.Node { Phase: NodeTravelPhase.Approaching, EnteredFrom: not null },
                "waiting" => train.Position is TrainPosition.Node { Phase: NodeTravelPhase.Waiting } && train.Activity == TrainActivity.Waiting,
                "docked" => train.Position is TrainPosition.Node { Phase: NodeTravelPhase.Docked } && train.Activity == TrainActivity.Servicing,
                "departing" => train.Position is TrainPosition.Node { Phase: NodeTravelPhase.Departing, EnteredFrom: not null },
                "edge" or "pending-stop" => train.Position is TrainPosition.Edge,
                _ => throw new ArgumentOutOfRangeException(nameof(boundary), boundary, "未知恢复边界。")
            };
            if (reached)
            {
                if (boundary == "pending-stop") Submit(session, new CoreCommand.StopTrain(train.Id), events);
                return;
            }
            Advance(session, events);
        }
        throw new InvalidOperationException($"未到达恢复边界：{boundary}；{Query(session)}");
    }

    internal static string Query(ICoreSession session)
    {
        var train = Train(session);
        WorldLocation location = train.Position switch
        {
            TrainPosition.Node node => new WorldLocation.Node(node.Id),
            TrainPosition.Edge edge => new WorldLocation.Edge(edge.Id, edge.DistanceMeters),
            _ => throw new InvalidOperationException()
        };
        var instances = train.Vehicles.Select(vehicle =>
            ((Lookup<InstanceView>.Found)session.Read().FindInstance(new InstanceId.Vehicle(vehicle))).Value).ToArray();
        return JsonSerializer.Serialize(new { Position = Position(train.Position), train.Activity,
            train.SpeedMetersPerSecond, train.NextStopIndex, train.RemainingDwellSteps, train.OrderFacing,
            train.LengthMeters, train.MassTons, train.TractionKilonewtons, train.Acceleration,
            train.MaximumSpeedMetersPerSecond, train.Vehicles, train.Inventory,
            Location = ((Lookup<LocationView>.Found)session.Read().FindLocation(location)).Value.Trains,
            Instances = instances.Select(instance => new { instance.Id, Location = JsonSerializer.Serialize(instance.Location, instance.Location.GetType()),
                Details = JsonSerializer.Serialize(instance.Details, instance.Details.GetType()) }).ToArray(),
            Map = session.Read().World.Trains.Select(item => new { item.Id, item.Activity, Position = Position(item.Position) }).ToArray() });
    }

    internal static string Position(TrainPosition position) => JsonSerializer.Serialize(position, position.GetType());
    internal static string Event(EventEnvelope envelope) => JsonSerializer.Serialize(new
    { envelope.Position, envelope.Ordinal, Kind = envelope.Event.GetType().Name,
        Payload = envelope.Event is CoreEvent.TrainActivityChanged changed
            ? JsonSerializer.Serialize(new { changed.Train, changed.Before, changed.After,
                Position = Position(changed.Position), changed.Reasons })
            : JsonSerializer.Serialize(envelope.Event, envelope.Event.GetType()) });
}
