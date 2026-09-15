using Train.Domain.Voxels;

namespace Train.Domain.Rails;

public static class TrainPersistenceFixtures
{
    public static Guid WriteHighSpeed(VoxelDevelopmentSession session, IWorldSaveStore store, double frontX)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(store);
        var saved = session.Save("High Speed Source");
        if (!saved.Succeeded || !saved.SaveId.HasValue)
            throw new InvalidOperationException($"High-speed fixture source save failed: {saved.Status}.");
        if (!store.TryRead(saved.SaveId.Value, out var json, out var readStatus))
            throw new InvalidOperationException($"High-speed fixture could not reread the source save: {readStatus}.");
        if (!WorldSaveCodec.TryDeserialize(json, out var document, out var parseStatus))
            throw new InvalidOperationException($"High-speed fixture could not parse the source save: {parseStatus}.");

        var id = Guid.NewGuid();
        var relocated = Relocate(document, id, frontX);
        if (!store.TryWrite(id, WorldSaveCodec.Serialize(relocated), out var writeStatus))
            throw new InvalidOperationException($"High-speed fixture write failed: {writeStatus}.");
        return id;
    }

    private static WorldSaveDocument Relocate(WorldSaveDocument document, Guid id, double frontX)
    {
        var assembly = document.Assemblies.Single();
        var locomotiveId = assembly.ActiveLocomotiveId
            ?? throw new InvalidOperationException("High-speed fixture requires an active locomotive.");
        var carriageId = assembly.VehicleIds.First(item => item != locomotiveId);
        var vehicles = document.Vehicles
            .Select(vehicle =>
            {
                if (vehicle.Id == locomotiveId)
                    return vehicle.WithTransform(StaticTrainFixture.FacingPositiveX(frontX));
                if (vehicle.Id == carriageId)
                    return vehicle.WithTransform(StaticTrainFixture.FacingPositiveX(
                        frontX - StaticTrainFixture.Wheelbase - StaticTrainFixture.CouplerGap - 2 * StaticTrainFixture.Overhang));
                return vehicle;
            })
            .ToArray();
        var segment = document.Railway.Segments.First(item =>
            item.Alignment.StartX <= frontX && frontX <= item.Alignment.EndX);
        var assemblies = new[]
        {
            new TrainAssembly(
                assembly.Id,
                assembly.ActiveLocomotiveId,
                new RailPosition(
                    segment.Id,
                    StaticTrainFixture.AlongTrackDistance(frontX, segment.Alignment),
                    RailHeading.AlongAlignment),
                assembly.VehicleIds,
                assembly.Connections,
                600,
                running: true)
        };
        return document.With(id, vehicles, assemblies);
    }
}
