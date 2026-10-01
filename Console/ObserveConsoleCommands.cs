using System.Globalization;
using Train.Domain.Composites;
using Train.Domain.WorldEditor;
using Train.Domain.Voxels;

namespace Train;

/// <summary>World editor observation commands. They only read the session and never pause, advance or edit it.</summary>
public sealed class ObserveConsoleCommands
{
    private readonly VoxelDevelopmentSession _session;
    private readonly WorldEditorObserver _observer;

    public ObserveConsoleCommands(VoxelDevelopmentSession session, WorldEditorObserver observer)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(observer);
        _session = session;
        _observer = observer;
    }

    public string Execute(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
            return Report();

        var verb = arguments[0].ToLowerInvariant();
        if (arguments.Count == 1)
        {
            switch (verb)
            {
                case "list":
                    return FormatList();
                case "focus":
                    return FormatMode(_observer.FocusSelection());
                case "follow":
                    return FormatMode(_observer.FollowSelection());
                case "free":
                    return FormatMode(_observer.Free());
            }
        }

        if (verb == "select" && TrySelection(arguments, out var selection))
        {
            if (selection is not { } item)
                return FormatMode(_observer.ClearSelection());
            var status = _observer.Select(item);
            return status == WorldEditorObservationStatus.Selected
                ? Report($"Selected {_observer.Selection!.Value.Describe()}")
                : $"{status} {item.Describe()}";
        }

        return Usage();
    }

    private static string Usage() =>
        "Usage: observe | observe list | observe select vehicle <id>|segment <id>|cell <x> <y> <z>|none | observe focus | observe follow | observe free.";

    private string Report(string? headline = null)
    {
        _observer.Reconcile();
        var lines = new List<string>();
        if (headline is not null)
            lines.Add(headline);
        lines.Add(_observer.Describe());
        lines.Add("camera " + _observer.Pose.Position + " look " + _observer.Pose.Look);
        if (_observer.LastNotice is { } notice)
            lines.Add("notice " + notice.Describe());
        if (_observer.Selection is { } selected && WorldEditorInspector.Inspect(_session, selected) is { } inspection)
            lines.AddRange(inspection.Lines());
        return string.Join('\n', lines);
    }

    private string FormatMode(WorldEditorObservationStatus status) => $"{status} {_observer.Describe()}";

    private string FormatList()
    {
        var entries = WorldEditorInspector.List(_session);
        if (entries.Count == 0)
            return "objects none";
        return string.Join('\n', entries.Select(entry => entry.Object.Describe() + " · " + entry.Label));
    }

    private static bool TrySelection(IReadOnlyList<string> arguments, out ObservedObject? selection)
    {
        selection = null;
        if (arguments.Count == 2 && string.Equals(arguments[1], "none", StringComparison.OrdinalIgnoreCase))
            return true;
        if (arguments.Count == 3 && TryId(arguments[2], out var id))
        {
            if (string.Equals(arguments[1], "vehicle", StringComparison.OrdinalIgnoreCase))
                selection = ObservedObject.Vehicle(id);
            else if (string.Equals(arguments[1], "segment", StringComparison.OrdinalIgnoreCase))
                selection = ObservedObject.Segment(id);
            return selection is not null;
        }

        if (arguments.Count == 5 &&
            string.Equals(arguments[1], "cell", StringComparison.OrdinalIgnoreCase) &&
            TryInt(arguments[2], out var x) &&
            TryInt(arguments[3], out var y) &&
            TryInt(arguments[4], out var z))
        {
            selection = ObservedObject.WorldCell(new WorldVoxelCoord(x, y, z));
            return true;
        }

        return false;
    }

    private static bool TryId(string text, out EntityId id)
    {
        id = default;
        if (!ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value == 0)
            return false;
        id = new EntityId(value);
        return true;
    }

    private static bool TryInt(string text, out int value) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
}
