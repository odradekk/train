using Train.Domain.Voxels;

namespace Train;

internal static class WorldChangeConsole
{
    public static bool TryParseChoice(string token, out WorldChangeChoice choice)
    {
        if (string.Equals(token, "discard", StringComparison.OrdinalIgnoreCase))
        {
            choice = WorldChangeChoice.Discard;
            return true;
        }

        if (string.Equals(token, "save", StringComparison.OrdinalIgnoreCase))
        {
            choice = WorldChangeChoice.SaveThenSwitch;
            return true;
        }

        if (string.Equals(token, "cancel", StringComparison.OrdinalIgnoreCase))
        {
            choice = WorldChangeChoice.Cancel;
            return true;
        }

        choice = default;
        return false;
    }

    public static string Format(VoxelDevelopmentSession session, WorldChangeResult result)
    {
        var count = $"Voxel count: {session.World.VoxelCount}.";
        var diagnostic = result.Diagnostic is { } reported
            ? $"stage {reported.Stage} subject {reported.Subject} relationship {reported.Relationship}"
            : "";
        return result.Status switch
        {
            WorldChangeStatus.Cancelled => $"Cancelled. The current world was kept.\n{count}",
            WorldChangeStatus.ChoiceRequired => $"Unsaved changes. Choose save, discard, or cancel.\n{count}",
            WorldChangeStatus.SaveFailed => $"Voxel {result.SaveStatus}.\n{diagnostic}\n{count}",
            WorldChangeStatus.CandidateFailed => $"Voxel {result.CandidateStatus}.\n{diagnostic}\n{count}",
            _ => $"Voxel {result.Status}.\n{count}"
        };
    }
}
