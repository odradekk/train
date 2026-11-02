using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.App.Measurement;

internal sealed partial class MeasurementRun
{
    internal static bool ProbeCenterMatchesEdge(PickSubject? subject, WorldLocation? focus, EdgeId expected) =>
        subject is PickSubject.Terrain { Location: WorldLocation.Edge terrain } && terrain.Id == expected ||
        subject is PickSubject.WorldObject { Object: WorldObjectId.Instance { Id: InstanceId.Vehicle } } &&
        focus is WorldLocation.Edge edge && edge.Id == expected;
}
