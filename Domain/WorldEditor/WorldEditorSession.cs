using Train.Core.Content;
using Train.Domain.Voxels;

namespace Train.Domain.WorldEditor;

public static class WorldEditorSession
{
    /// <summary>
    /// Builds the declared initial world through the formal edit entry, then opens the established session's input
    /// order at the establish boundary and leaves the global clock paused.
    /// </summary>
    public static WorldEditorEstablishStatus TryEstablish(
        string configurationId,
        VoxelCatalog catalog,
        CompositeCatalog composites,
        IWorldSaveStore? store,
        out VoxelDevelopmentSession? session)
    {
        session = null;
        var status = WorldEditorConfigurations.TryResolve(configurationId, catalog, composites, out var source);
        if (status != WorldEditorEstablishStatus.Established)
            return status;

        var configuration = WorldEditorConfigurations.Find(configurationId)!;
        var candidate = new VoxelDevelopmentSession(catalog, configuration.BaseWorld, store, composites);
        if ((configuration.Railway is { } railway && !candidate.ConstructRails(railway).Succeeded) ||
            (configuration.Train is not null && !candidate.AssembleStaticTrain().Succeeded))
        {
            candidate.Detach();
            return WorldEditorEstablishStatus.InitialStateRejected;
        }

        candidate.BindEstablishedSource(source);
        session = candidate;
        return WorldEditorEstablishStatus.Established;
    }
}
