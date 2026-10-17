using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.World;

namespace Train.Core.Sessions;

internal sealed class CoreRecording
{
    private sealed record StartEntry(RecordingStart Start, int CommandOffset);
    private sealed record GapEntry(RecordingPointId Point, RecordingGap Gap);
    private readonly int _retainedStarts;
    private readonly List<StartEntry> _starts = [];
    private readonly List<RecordedCommand> _commands = [];
    private readonly List<FaultRecordingSegment> _faults = [];
    private readonly List<GapEntry> _gaps = [];
    private readonly Dictionary<RecordingPointId, SimulationPosition> _pending = [];
    private readonly HashSet<RecordingPointId> _published = [];
    private ulong _nextPoint = 1;
    private RecordingPointId _latestPublished;

    internal CoreRecording(CoreState state, int retainedStarts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retainedStarts, 1);
        _retainedStarts = retainedStarts;
        CaptureStart(state);
    }

    internal RecordingStart CaptureStart(CoreState state)
    {
        var start = new RecordingStart(new(_nextPoint), new CoreCapture(state));
        _nextPoint = checked(_nextPoint + 1);
        _starts.Add(new(start, _commands.Count));
        _pending.Add(start.Id, start.State.Position);
        return start;
    }

    internal void Record(RecordedCommand command) => _commands.Add(command);

    internal void FreezeFault(CoreState state)
    {
        var start = _starts[0].Start;
        var checkpoints = Checkpoints(new CoreCapture(state).Checkpoint);
        var commands = _commands.ToImmutableArray();
        _faults.Add(new(start, commands, checkpoints, state.Fault!,
            References(_starts.Select(entry => entry.Start), commands, [])));
    }

    internal CoreRecordingWindow Read(CoreState state)
    {
        var starts = _starts.Select(entry => entry.Start).ToImmutableArray();
        var commands = _commands.ToImmutableArray();
        var faults = _faults.ToImmutableArray();
        return new(state.Position, starts, commands, Checkpoints(new CoreCapture(state).Checkpoint),
            faults, Gaps(), References(starts, commands, faults));
    }

    internal RecordingMaintenance Confirm(RecordingPointId point, RecordingWriteOutcome outcome)
    {
        if (!Enum.IsDefined(outcome))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        if (point.Value == 0 || point.Value >= _nextPoint)
            throw new ArgumentException("起点不属于这个会话。", nameof(point));
        if (!_pending.Remove(point, out var position))
            throw new InvalidOperationException("起点已经确认。");
        var before = References(_starts.Select(entry => entry.Start), _commands, _faults);
        if (outcome == RecordingWriteOutcome.Failed)
        {
            var until = _latestPublished.Value > point.Value
                ? _starts.Single(entry => entry.Start.Id == _latestPublished).Start.State.Position
                : (SimulationPosition?)null;
            _gaps.Add(new(point, new(position, until, "recording_write_failed")));
        }
        else
        {
            for (var index = 0; index < _gaps.Count; index++)
                if (_gaps[index].Gap.Until is null && _gaps[index].Point.Value < point.Value)
                    _gaps[index] = _gaps[index] with { Gap = _gaps[index].Gap with { Until = position } };
            if (_starts.Any(entry => entry.Start.Id == point))
                _published.Add(point);
            if (point.Value > _latestPublished.Value)
                _latestPublished = point;
            Trim();
        }
        var after = References(_starts.Select(entry => entry.Start), _commands, _faults);
        return new(before.Inputs.Except(after.Inputs).ToImmutableArray(), Gaps());
    }

    private void Trim()
    {
        var published = _starts.Where(entry => _published.Contains(entry.Start.Id)).ToArray();
        if (published.Length < _retainedStarts)
            return;
        var first = published[^_retainedStarts];
        var removed = first.CommandOffset;
        _commands.RemoveRange(0, removed);
        _starts.RemoveAll(entry => entry.Start.Id.Value < first.Start.Id.Value);
        _published.RemoveWhere(point => point.Value < first.Start.Id.Value);
        for (var index = 0; index < _starts.Count; index++)
            _starts[index] = _starts[index] with { CommandOffset = _starts[index].CommandOffset - removed };
    }

    private ImmutableArray<RecordingGap> Gaps() => _gaps.Select(entry => entry.Gap).ToImmutableArray();

    internal void Clear()
    {
        _starts.Clear();
        _commands.Clear();
        _faults.Clear();
        _gaps.Clear();
        _pending.Clear();
        _published.Clear();
    }

    private ImmutableArray<CoreCheckpoint> Checkpoints(CoreCheckpoint end)
    {
        var checkpoints = _starts.Select(entry => entry.Start.State.Checkpoint).ToList();
        if (checkpoints[^1] != end)
            checkpoints.Add(end);
        return checkpoints.ToImmutableArray();
    }

    private static CoreReferenceManifest References(IEnumerable<RecordingStart> starts,
        IEnumerable<RecordedCommand> commands, IEnumerable<FaultRecordingSegment> faults)
    {
        var manifests = starts.Select(start => start.State.References)
            .Concat(faults.Select(fault => fault.RequiredReferences)).ToArray();
        var decoded = commands.Select(command => ((CoreResult<DecodedCommand>.Success)CoreCommandEncoding.Decode(command.CanonicalPayload)).Value).ToArray();
        var designs = decoded.Select(command => command.Command).OfType<CoreCommand.StartOwnedJob>()
            .Select(command => command.Request).OfType<JobRequest.BuildVehicle>().Select(request => request.Design).ToArray();
        var commandDefinitions = designs.SelectMany(input =>
        {
            var path = $"Recording.Designs[{input.Design.Design.Value}]";
            var missing = input.Design.MissingReferences.Select(reference => reference.Reference).ToHashSet();
            return input.Design.Parts.Select(part => new RequiredDefinition(part.Definition, null,
                    path + $".Parts[{part.Id.Value}].Definition", part.Missing || missing.Contains(part.Definition)))
                .Concat(WorldReferences.DesignGoods(input.Design)
                    .Concat(input.AssemblyGoods.Select(good => (good.Good, ".AssemblyGoods")))
                    .Where(item => WorldReferences.GoodReference(item.Good) is not null)
                    .Select(item => new RequiredDefinition(WorldReferences.GoodReference(item.Good)!, null,
                        path + item.Item2, missing.Contains(WorldReferences.GoodReference(item.Good)!))));
        });
        var inputs = manifests.SelectMany(manifest => manifest.Inputs)
            .Concat(commands.SelectMany(command =>
            {
                var target = ((CoreResult<DecodedCommand>.Success)CoreCommandEncoding.Decode(command.CanonicalPayload)).Value;
                return target.InputIdentity is { } identity
                    ? new[] { command.InputBefore, command.InputAfter, identity }
                    : [command.InputBefore, command.InputAfter];
            }))
            .Concat(designs.Select(input => input.InputIdentity))
            .Distinct().OrderBy(input => input.Value, StringComparer.Ordinal).ToImmutableArray();
        return new(
            manifests.SelectMany(manifest => manifest.Designs).Concat(designs.Select(input => input.Design.Design)).Distinct()
                .OrderBy(design => design.Value, StringComparer.Ordinal).ToImmutableArray(),
            manifests.SelectMany(manifest => manifest.Templates).DistinctBy(template => template.Owner)
                .OrderBy(template => template.Owner.Value).ToImmutableArray(),
            manifests.SelectMany(manifest => manifest.Definitions).Concat(commandDefinitions).Distinct()
                .OrderBy(definition => WorldReferences.DefinitionKey(definition.Definition), StringComparer.Ordinal)
                .ThenBy(definition => definition.Field, StringComparer.Ordinal).ToImmutableArray(), inputs);
    }
}
