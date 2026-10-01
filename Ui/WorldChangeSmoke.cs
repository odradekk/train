using Train.Domain.Voxels;

namespace Train;

internal static class WorldChangeSmoke
{
    public static void Finish(VoxelDevelopmentSession session)
    {
        while (session.IsChangingWorld)
        {
            var result = session.AdvanceWorldChange();
            if (result.Status == WorldChangeStatus.ChoiceRequired)
                throw new InvalidOperationException("world change still requires a choice");
            if (result.Status == WorldChangeStatus.Switching)
                Thread.Yield();
        }
    }
}
