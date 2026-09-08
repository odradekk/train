using Godot;
using Train.Content;
using Train.Domain.Voxels;

namespace Train.Presentation;

public sealed class VoxelWorldMeshPresenter
{
    private readonly Dictionary<ChunkCoord, VoxelChunkMesh> _meshes = new();

    public IReadOnlyList<ChunkCoord> LastRebuiltChunks { get; private set; } = Array.Empty<ChunkCoord>();
    public int FaceCount => _meshes.Values.Sum(mesh => mesh.FaceCount);
    public int LastWorldVoxelCount { get; private set; }
    public string DisplayFingerprint { get; private set; } = "";
    public VoxelChunkMesh PrimaryMesh => MeshFor(new ChunkCoord(0, 0, 0));
    public IEnumerable<VoxelChunkMesh> Meshes => _meshes.Values;

    public VoxelChunkMesh MeshFor(ChunkCoord chunk) =>
        _meshes.TryGetValue(chunk, out var mesh)
            ? mesh
            : throw new InvalidOperationException($"No mesh for chunk {chunk}.");

    public VoxelChunkMesh MeshContaining(WorldVoxelCoord position, int chunkSize) =>
        MeshFor(VoxelChunks.Of(position, chunkSize));

    public void Rebuild(Node parent, VoxelWorldIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(parent);
        foreach (var mesh in _meshes.Values)
            mesh.QueueFree();
        _meshes.Clear();
        foreach (var chunk in VoxelChunks.All(identity))
        {
            var mesh = new VoxelChunkMesh();
            parent.AddChild(mesh);
            _meshes[chunk] = mesh;
        }
    }

    public void Refresh(VoxelDevelopmentSession session, VoxelCatalogSnapshot? catalog)
    {
        ArgumentNullException.ThrowIfNull(session);
        var dirty = session.TakePendingDirtyChunks();
        foreach (var chunk in dirty)
            MeshFor(chunk).Refresh(session.World, chunk, catalog);
        LastRebuiltChunks = dirty;
        LastWorldVoxelCount = session.World.VoxelCount;
        DisplayFingerprint = string.Join("|", _meshes
            .OrderBy(pair => pair.Key)
            .Select(pair => $"{pair.Key.X},{pair.Key.Y},{pair.Key.Z}:{pair.Value.DisplayFingerprint}"));
    }
}
