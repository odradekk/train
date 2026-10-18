using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.World;
using Train.Core.Railway;
using Train.Core.Inventory;
using Train.Core.Settlements;
using Train.Core.Production;
using Train.Core.Trading;
using Train.Core.Construction;

namespace Train.Core.Sessions;

internal sealed record CoreState(SimulationPosition Position, ulong NextCommandNumber,
    CoreRevision Revision, CoreContentProjection Content, WorldState World, CoreFault? Fault = null);

internal abstract record CommandEffect
{
    private CommandEffect() { }
    internal sealed record Accepted(CoreContentProjection Content, ImmutableArray<CoreEvent> Events,
        WorldState? World = null, ImmutableArray<WorldObjectId> Created = default) : CommandEffect;
    internal sealed record Rejected(ImmutableArray<CoreProblem> Problems) : CommandEffect;
}

internal sealed class CoreSession(CoreState state, int retainedRecordingStarts = 1) : ICoreSession
{
    private bool _disposed;
    private readonly CoreRecording _recording = new(state, retainedRecordingStarts);
    internal CoreState State { get; private set; } = state;

    public ICoreReadView Read()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new CoreReadView(State.Position, State.Revision, State.Content.Identity, State.Fault, State.World, State.Content);
    }

    public SubmissionResult Submit(CoreCommand command, CommandSource source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(command);
        if (!Enum.IsDefined(source))
            throw new ArgumentOutOfRangeException(nameof(source));
        if (command is CoreCommand.PublishContent publish)
        {
            WorldInputs.ValidateContent(publish.Content);
            if (publish.Content.Identity == State.Content.Identity)
                throw new ArgumentException("发布内容的输入身份必须不同于当前身份。", nameof(command));
            return ExecuteCommand(publish, source, before => WorldContentPublication.Execute(State.World, before, publish.Content,
                new(State.Position.CompletedSteps, checked(State.Position.Sequence + 1))));
        }
        if (command is CoreCommand.BuyVehicle or CoreCommand.SellVehicle)
        {
            VehicleTrades.Validate(command);
            return ExecuteCommand(command, source, content => VehicleTrades.Execute(State.World, content, command));
        }
        if (command is CoreCommand.StartOwnedJob or CoreCommand.CommissionVehicleWork)
        {
            var (request, funding, maximum) = command switch
            {
                CoreCommand.StartOwnedJob start => (start.Request, JobFunding.OwnInventory, (Money?)null),
                CoreCommand.CommissionVehicleWork commission => (commission.Request, JobFunding.NpcCommission, (Money?)commission.MaximumPayment),
                _ => throw new InvalidOperationException()
            };
            ConstructionCommands.Validate(request, State.World, State.Content, funding);
            if (maximum is { Value: < 0 }) throw new ArgumentException("付款上限必须非负。", nameof(command));
            return ExecuteCommand(command, source, content => ConstructionCommands.Execute(State.World, content, request,
                State.Position.CompletedSteps, funding, maximum));
        }
        if (command is CoreCommand.LoadGoods or CoreCommand.UnloadGoods or CoreCommand.AcquireLease
            or CoreCommand.BuyGoods or CoreCommand.SellGoods)
        {
            InventoryCommands.Validate(command);
            return ExecuteCommand(command, source, content => InventoryCommands.Execute(State.World, content, command, State.Position.CompletedSteps));
        }
        TrainCommands.Validate(command);
        return ExecuteCommand(command, source, content => TrainCommands.Execute(State.World, content, command,
            new(State.Position.CompletedSteps, checked(State.Position.Sequence + 1))));
    }

    internal SubmissionResult ExecuteCommand(CoreCommand command, CommandSource source,
        Func<CoreContentProjection, CommandEffect> body)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var before = State;
        if (before.Fault is not null)
            return new SubmissionResult.NotAccepted(new("frozen", "Fault", null, []));
        if (before.NextCommandNumber == ulong.MaxValue || before.Position.Sequence == ulong.MaxValue)
            return new SubmissionResult.NotAccepted(new("identity_exhausted",
                before.NextCommandNumber == ulong.MaxValue ? "NextCommandNumber" : "Position.Sequence", null, []));

        var payload = CoreCommandEncoding.Encode(command);
        var position = new SimulationPosition(before.Position.CompletedSteps, checked(before.Position.Sequence + 1));
        CoreState after;
        SubmissionResult.Completed result;
        try
        {
            var effect = body(before.Content);
            var decision = effect switch
            {
                CommandEffect.Accepted acceptance => (CommandDecision)new CommandDecision.Accepted(
                    acceptance.Created.IsDefault ? [] : acceptance.Created, []),
                CommandEffect.Rejected rejected => new CommandDecision.Rejected(rejected.Problems),
                _ => throw new InvalidOperationException("命令结果无效。")
            };
            var events = effect is CommandEffect.Accepted accepted
                ? Envelopes(position, accepted.Events.AddRange((accepted.World ?? before.World).Settlements.SelectMany(s =>
                    ProductionQueries.StateChanges(before.World, before.Content,
                        accepted.World ?? before.World, accepted.Content, s.Id))))
                : ImmutableArray<EventEnvelope>.Empty;
            after = before with
            {
                Position = position,
                NextCommandNumber = checked(before.NextCommandNumber + 1),
                Revision = new(checked(before.Revision.Value + 1)),
                Content = effect is CommandEffect.Accepted change ? change.Content : before.Content,
                World = effect is CommandEffect.Accepted { World: { } world } ? world : before.World
            };
            result = new SubmissionResult.Completed(new(before.NextCommandNumber, before.Position,
                after.Position, decision), events, null);
        }
        catch (Exception exception)
        {
            var fault = new CoreFault("internal_error", position, nameof(Submit), "", null);
            after = before with
            {
                Fault = fault,
                Revision = before.Revision.Value == ulong.MaxValue
                    ? before.Revision : new(checked(before.Revision.Value + 1))
            };
            result = new SubmissionResult.Completed(new(before.NextCommandNumber, before.Position,
                before.Position, new CommandDecision.Faulted(fault)), [],
                new(exception.Message, exception.StackTrace));
        }
        _recording.Record(new(source, payload, result.Receipt, before.Content.Identity, after.Content.Identity));
        State = after;
        if (after.Fault is not null)
            _recording.FreezeFault(after);
        return result;
    }

    public AdvanceResult Advance(uint steps)
    {
        PopulationInputs? inputs = null;
        return AdvanceWorld(steps, before =>
        {
            var (world, events) = TrainOperations.Advance(before.World, before.Content, before.Position.CompletedSteps);
            // Content and definitions cannot change within this public Advance call.
            inputs ??= PopulationOperations.ReadInputs(world, before.Content);
            foreach (var settlement in before.World.Settlements)
            {
                var population = PopulationOperations.Begin(world, before.Content, settlement.Id, before.Position.CompletedSteps, inputs);
                var consumption = population.Consumption;
                var construction = ConstructionOperations.Requests(world, before.Content, settlement.Id);
                var requests = ProductionOperations.Requests(world, before.Content, settlement.Id).AddRange(construction);
                SettlementResolution? resolution = null;
                if (consumption is not null || !requests.IsEmpty)
                {
                    resolution = SettlementResources.Resolve(world, before.Content, settlement.Id, population.AvailableLabor,
                        consumption?.BasicRequests ?? [], requests, granted => consumption?.PrepareImprovement(granted) ?? []);
                    world = resolution.World;
                }
                if (!settlement.ProductionFacilities.IsEmpty)
                {
                    var production = ProductionOperations.Complete(world, before.Content, settlement.Id,
                        requests, resolution ?? new(world, [], [], [], []), before.Position.CompletedSteps);
                    world = production.World;
                    events = events.AddRange(production.Events);
                }
                if (world.ConstructionJobs.Any(j => j.Settlement == settlement.Id) || settlement.LastConstructionPowerDemand != 0
                    || settlement.LastConstructionActualSupply != 0 || settlement.LastConstructionAllocatedLabor != 0)
                {
                    var jobs = ConstructionOperations.Complete(world, before.Content, settlement.Id, construction,
                        resolution ?? new(world, [], [], [], []));
                    world = jobs.World;
                    events = events.AddRange(jobs.Events);
                }
                var result = PopulationOperations.Complete(world, settlement.Id, population,
                    resolution?.ImprovementGrantedUnits ?? [], new(checked(before.Position.CompletedSteps + 1), 0));
                world = result.World;
                events = events.AddRange(result.Events);
            }
            var resources = WorldResources.Advance(before.World, world, before.Position.CompletedSteps);
            world = resources.World;
            events = events.AddRange(resources.Events);
            events = events.AddRange(world.Settlements.Where(s => !s.ProductionFacilities.IsEmpty).SelectMany(s =>
                ProductionQueries.StateChanges(before.World, before.Content, world, before.Content, s.Id)));
            return (before.Content, world, InventoryLeases.ExpirationEvents(before.World, before.Position.CompletedSteps).AddRange(events));
        });
    }

    internal AdvanceResult Advance(uint steps,
        Func<CoreContentProjection, (CoreContentProjection Content, ImmutableArray<CoreEvent> Events)> stepBody)
        => AdvanceWorld(steps, before =>
        {
            var (content, events) = stepBody(before.Content);
            return (content, before.World, events);
        });

    private AdvanceResult AdvanceWorld(uint steps,
        Func<CoreState, (CoreContentProjection Content, WorldState World, ImmutableArray<CoreEvent> Events)> stepBody)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var completed = 0u;
        var events = ImmutableArray.CreateBuilder<EventEnvelope>();
        CoreDiagnostic? diagnostic = null;
        while (completed < steps && State.Fault is null)
        {
            var before = State;
            var position = before.Position;
            try
            {
                position = new(checked(before.Position.CompletedSteps + 1), 0);
                var change = stepBody(before);
                var stepEvents = Envelopes(position, change.Events);
                var after = before with
                {
                    Position = position,
                    Revision = new(checked(before.Revision.Value + 1)),
                    Content = change.Content,
                    World = change.World
                };
                events.AddRange(stepEvents);
                State = after;
                completed = checked(completed + 1);
            }
            catch (Exception exception)
            {
                var fault = new CoreFault("internal_error", position, nameof(Advance), "", null);
                State = before with
                {
                    Fault = fault,
                    Revision = before.Revision.Value == ulong.MaxValue
                        ? before.Revision : new(checked(before.Revision.Value + 1))
                };
                _recording.FreezeFault(State);
                diagnostic = new(exception.Message, exception.StackTrace);
            }
        }
        return new(completed, State.Position, events.ToImmutable(), State.Fault, diagnostic);
    }

    private static ImmutableArray<EventEnvelope> Envelopes(SimulationPosition position, ImmutableArray<CoreEvent> events)
        => events.Select((value, ordinal) => new EventEnvelope(position, ordinal, value)).ToImmutableArray();

    public CoreResult<ICoreCapture> Capture(CapturePurpose purpose)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Enum.IsDefined(purpose))
            throw new ArgumentOutOfRangeException(nameof(purpose));
        return purpose == CapturePurpose.Save && State.Fault is not null
            ? new CoreResult<ICoreCapture>.Failure([new("frozen", "Fault", null, [])])
            : new CoreResult<ICoreCapture>.Success(new CoreCapture(State));
    }

    public CoreRecordingWindow ReadRecording()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _recording.Read(State);
    }

    public CoreResult<RecordingStart> CaptureRecordingStart()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return State.Fault is not null
            ? new CoreResult<RecordingStart>.Failure([new("frozen", "Fault", null, [])])
            : new CoreResult<RecordingStart>.Success(_recording.CaptureStart(State));
    }

    public RecordingMaintenance ConfirmRecordingStart(RecordingPointId point, RecordingWriteOutcome outcome)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _recording.Confirm(point, outcome);
    }

    public void Dispose()
    {
        _disposed = true;
        _recording.Clear();
    }
}
