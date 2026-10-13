using Train.Core.Contracts;
using Train.Core.Sessions;

namespace Train.Core.Tests.World;

internal static partial class WorldFixture
{
    public static CoreState State(SimulationPosition? position = null, ulong next = 1,
        CoreRevision? revision = null, CoreContentProjection? content = null)
    {
        using var candidate = Prepare(content: content);
        using var session = (CoreSession)candidate.Activate();
        return session.State with { Position = position ?? session.State.Position, NextCommandNumber = next,
            Revision = revision ?? session.State.Revision };
    }

}
