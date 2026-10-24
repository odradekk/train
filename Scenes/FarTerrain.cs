using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class FarTerrain(FarTerrain.Cache cache)
{
    internal readonly record struct TileKey(int X, int Z, int Spacing);
    internal sealed class Saved
    {
        internal ConcurrentDictionary<string, TerrainHeightCap> Caps { get; } = new(StringComparer.Ordinal);
        internal ConcurrentDictionary<TileKey, TerrainTile> Tiles { get; } = new();
    }
    internal sealed class Cache
    {
        private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
        internal Saved Get(string owner, string inputs)
        {
            lock (entries)
            {
                if (entries.TryGetValue(owner, out var entry) && entry.Inputs == inputs) return entry.Saved;
                var saved = new Saved();
                entries[owner] = new(inputs, saved);
                return saved;
            }
        }
        internal void Retain(IEnumerable<string> owners)
        {
            var retained = owners.ToHashSet(StringComparer.Ordinal);
            lock (entries)
                foreach (var key in entries.Keys.Where(key => !retained.Contains(key)).ToArray()) entries.Remove(key);
        }
        private sealed record Entry(string Inputs, Saved Saved);
    }

    private readonly object gate = new();
    private readonly Dictionary<TileKey, Job> jobs = [];
    private readonly Dictionary<TerrainTile, SceneObject> objects = [];
    private List<TileKey> wanted = [];
    private HashSet<(int X, int Z)> visibleParents = [];
    private HashSet<(int X, int Z)> fineParents = [];
    private HashSet<(int X, int Z)> prefetchParents = [];
    private Saved saved = new();
    private Func<ScenePanBounds, IEnumerable<(string Key, TerrainHeightCap.Builder Work)>>? parts;
    private Func<ScenePoint, double>? height;
    private Func<bool>? initialize;
    private SceneMaterial material = new SceneMaterial.BuiltIn(BuiltInMaterial.Sleeper);
    private int textureSpan = 1;
    private string owner = "", generation = "";
    private object? bound;
    private ContentGeneration content;
    private FarTerrainView view = new([]);
    private bool fineBuildAllowed = true;
    internal FarTerrainView View { get { lock (gate) return view; } }
    internal bool HasPendingWork { get { lock (gate) return initialize is not null || NextMissing() is not null; } }
    internal bool HasRunnableWork { get { lock (gate) return initialize is not null || NextMissing(fineBuildAllowed) is not null; } }
    internal void SetFineBuildAllowed(bool allowed) { lock (gate) fineBuildAllowed = allowed; }
    internal bool HasPendingCoarse
    {
        get
        {
            lock (gate)
                foreach (var key in wanted)
                    if (key.Spacing == 32 && !saved.Tiles.ContainsKey(key)) return true;
            return false;
        }
    }

    private TileKey? NextMissing(bool includeFine = true)
    {
        foreach (var key in wanted)
            if ((includeFine || key.Spacing == 32) && !saved.Tiles.ContainsKey(key)) return key;
        return null;
    }

    internal void BindRailway(SceneWorldSnapshot snapshot, RailwayPath path)
    {
        lock (gate)
        {
            if (ReferenceEquals(bound, path) && content == snapshot.Content.Generation) return;
            var edge = path.Edge;
            var key = RailwayDependencies.Values(new object?[] { path.Seed, edge.Id, edge.LengthMeters,
                edge.Tracks, edge.TrackLimit, edge.Terrain, path.Start.Position, path.End.Position,
                path.Start.Terrain, path.End.Terrain, path.Rules,
                edge.Breaks.OrderBy(item => item.Id.Value).ThenBy(item => item.Track).Select(item => new object[] { item.Id, item.Track, item.DistanceMeters }).ToArray(),
                edge.Works.Where(RailwayCoordinates.IsBreakRepair).OrderBy(item => item.Id.Value).Select(item => new object?[] { item.Id, item.Track, item.RepairingBreak }).ToArray(),
                NodeLayout.ReadInputs(snapshot, path.Start.Id), NodeLayout.ReadInputs(snapshot, path.End.Id), snapshot.Content.Generation });
            bound = path; content = snapshot.Content.Generation;
            if (!Reset("edge:" + edge.Id.Value, key)) return;
            initialize = () =>
            {
                var appearances = new Dictionary<TerrainTypeId, TerrainAppearanceProjection?>();
                foreach (var id in new[] { edge.Terrain.TypeId, path.Start.Terrain.TypeId, path.End.Terrain.TypeId })
                    appearances[id] = (snapshot.Content.FindTerrain(id) as ContentLookup<TerrainAppearanceProjection>.Found)?.Value;
                var surface = new TerrainSurface.Railway(path, id => appearances[id]);
                var source = new RailwayTerrainCaps(snapshot, path, SavedCap);
                height = surface.FarHeight; parts = source.Parts;
                Appearance(snapshot.Content, appearances[edge.Terrain.TypeId]);
                return true;
            };
        }
    }

    internal void BindNode(NodeScene.Preparation prepared)
    {
        lock (gate)
        {
            if (ReferenceEquals(bound, prepared) && content == prepared.Snapshot.Content.Generation) return;
            var key = RailwayDependencies.Values(new object?[] { prepared.TerrainInputs, prepared.Global.Layout.Inputs,
                prepared.Rails.OrderBy(item => item.Key).Select(item => item.Value.Inputs).ToArray(),
                prepared.Global.Parts.Where(item => NodePartKeys.IsBoundary(item.Key)).OrderBy(item => item.Key).Select(item => item.Value.Inputs).ToArray(),
                prepared.Snapshot.Content.Generation });
            bound = prepared; content = prepared.Snapshot.Content.Generation;
            if (!Reset("node:" + prepared.Global.Layout.Node.Value, key)) return;
            initialize = () =>
            {
                var source = new NodeTerrainCaps(prepared, SavedCap);
                var node = prepared.Snapshot.Core.World.Nodes.Single(item => item.Id == prepared.Global.Layout.Node);
                parts = source.Parts;
                height = point =>
                {
                    var column = new GridColumn((int)Math.Floor(point.X), (int)Math.Floor(point.Z));
                    var size = VoxelGrid.ChunkSize;
                    var chunk = new GridColumn((int)Math.Floor(point.X / size) * size, (int)Math.Floor(point.Z / size) * size);
                    var bed = prepared.BedChunks.GetValueOrDefault(chunk)?.GetValueOrDefault(column);
                    return TerrainSurface.NodeHeight(prepared.Snapshot, node, prepared.Global.Layout, point, bed, prepared.Terrain);
                };
                Appearance(prepared.Snapshot.Content, prepared.Terrain);
                return true;
            };
        }
    }

    private bool Reset(string nextOwner, string nextGeneration)
    {
        if (owner == nextOwner && generation == nextGeneration) return false;
        foreach (var job in jobs.Values) job.Parts?.Dispose();
        owner = nextOwner; generation = nextGeneration;
        saved = cache.Get(owner, generation);
        jobs.Clear(); wanted.Clear(); visibleParents.Clear(); fineParents.Clear(); prefetchParents.Clear(); objects.Clear();
        view = new([]); parts = null; height = null;
        return true;
    }

    private TerrainHeightCap? SavedCap(string part) => saved.Caps.GetValueOrDefault(part);
    private void Appearance(IContentView contentView, TerrainAppearanceProjection? terrain)
    {
        material = new SceneMaterial.BuiltIn(BuiltInMaterial.Sleeper); textureSpan = 1;
        if (terrain is not null && contentView.FindVoxelAppearance(terrain.SurfaceVoxel) is ContentLookup<VoxelAppearanceProjection>.Found found &&
            found.Value.Presentation is VoxelPresentation.Cube cube)
        { material = new SceneMaterial.Voxel(cube.Top, cube.Material, null); textureSpan = cube.TileSpan; }
    }

    internal void Demand(ScenePoint focus, double radius, ScenePanBounds? wholeDomain = null, IReadOnlyList<ScenePoint>? ahead = null)
    {
        lock (gate)
        {
            if (owner.Length == 0) return;
            var nextVisible = new HashSet<(int X, int Z)>();
            if (wholeDomain is not null)
                AddParents(nextVisible, wholeDomain.Minimum.X - radius, wholeDomain.Minimum.Y - radius,
                    wholeDomain.Maximum.X + radius, wholeDomain.Maximum.Y + radius);
            else
                AddSweep(nextVisible, radius);
            var nextFine = new HashSet<(int X, int Z)>();
            AddParents(nextFine, focus.X - 512, focus.Z - 512, focus.X + 512, focus.Z + 512);
            var prefetch = new HashSet<(int X, int Z)>();
            if (ahead is not null) AddSweep(prefetch, 512);
            if (visibleParents.SetEquals(nextVisible) && fineParents.SetEquals(nextFine) && prefetchParents.SetEquals(prefetch))
            { Publish(); return; }
            visibleParents = nextVisible; fineParents = nextFine; prefetchParents = prefetch;
            wanted = [];
            foreach (var parent in visibleParents.OrderBy(p => Distance(p, focus))) wanted.Add(Key(parent.X, parent.Z, 32));
            AddFine(fineParents.OrderBy(p => Distance(p, focus)));
            AddFine(prefetch.Except(fineParents).OrderBy(p => Distance(p, focus)));
            var required = wanted.ToHashSet();
            foreach (var key in jobs.Keys.Where(key => !required.Contains(key)).ToArray())
            { jobs[key].Parts?.Dispose(); jobs.Remove(key); }
            Publish();
        }
        void AddFine(IEnumerable<(int X, int Z)> parents)
        {
            foreach (var parent in parents)
            for (var z = 0; z < 4; z++) for (var x = 0; x < 4; x++)
                wanted.Add(Key(parent.X + x * 256, parent.Z + z * 256, 4));
        }
        void AddSweep(HashSet<(int X, int Z)> parents, double extent)
        {
            var minX = focus.X; var maxX = focus.X;
            var minZ = focus.Z; var maxZ = focus.Z;
            if (ahead is not null) foreach (var point in ahead)
            {
                minX = Math.Min(minX, point.X); maxX = Math.Max(maxX, point.X);
                minZ = Math.Min(minZ, point.Z); maxZ = Math.Max(maxZ, point.Z);
            }
            AddParents(parents, minX - extent, minZ - extent, maxX + extent, maxZ + extent);
        }
    }

    private static double Distance((int X, int Z) p, ScenePoint focus) => Math.Abs(p.X + 512 - focus.X) + Math.Abs(p.Z + 512 - focus.Z);
    private static void AddParents(HashSet<(int X, int Z)> result, double x0, double z0, double x1, double z1)
    {
        for (var z = (int)Math.Floor(z0 / 1024) * 1024; z < z1; z += 1024)
        for (var x = (int)Math.Floor(x0 / 1024) * 1024; x < x1; x += 1024) result.Add((x, z));
    }
    private static TileKey Key(int x, int z, int spacing) => new(x, z, spacing);

    internal bool Advance(int budget, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget);
        while (budget > 0)
        {
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                // Initialization runs in the worker slot, just like cap and mesh work.
                if (initialize is not null) { initialize(); initialize = null; budget--; continue; }
                var candidate = NextMissing(fineBuildAllowed);
                if (candidate is not { } key) return true;
                if (!jobs.TryGetValue(key, out var job))
                { jobs[key] = new(); budget--; continue; }
                var unit = Math.Min(128, budget);
                var remaining = unit;
                var size = key.Spacing == 32 ? 1024 : 256;
                while (remaining > 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (job.Parts is null)
                    {
                        var margin = TerrainTile.ColumnMarginCells;
                        job.Parts = parts!(new(new(key.X - margin, key.Z - margin), new(key.X + size + margin, key.Z + size + margin))).GetEnumerator();
                        remaining--; continue;
                    }
                    if (!job.PartsComplete)
                    {
                        if (job.Current is null)
                        {
                            if (!job.Parts.MoveNext()) { job.PartsComplete = true; job.Parts.Dispose(); }
                            else { job.Current = job.Parts.Current.Work; job.PartKey = job.Parts.Current.Key; }
                            remaining--; continue;
                        }
                        if (!job.Current.Complete)
                        { remaining -= job.Current.Advance(remaining, token); continue; }
                        saved.Caps[job.PartKey!] = job.Current.Result;
                        job.Caps.Add(job.Current.Result); job.Current = null;
                        remaining--; continue;
                    }
                    if (job.Tile is null)
                    { job.Tile = TerrainTile.Begin(new(key.X, 0, key.Z), size, key.Spacing, height!, material, textureSpan, job.Caps); remaining--; continue; }
                    remaining -= job.Tile.Advance(remaining, token);
                    if (job.Tile.Complete)
                    { saved.Tiles[key] = job.Tile.Result; jobs.Remove(key); Publish(); break; }
                }
                budget -= unit - remaining;
            }
        }
        return !HasRunnableWork;
    }

    private void Publish()
    {
        var selected = ImmutableArray.CreateBuilder<TerrainTile>();
        foreach (var parent in visibleParents)
        {
            var fine = new List<TerrainTile>(16);
            if (fineParents.Contains(parent))
                for (var z = 0; z < 4; z++) for (var x = 0; x < 4; x++)
                    if (saved.Tiles.TryGetValue(Key(parent.X + x * 256, parent.Z + z * 256, 4), out var tile)) fine.Add(tile);
            if (fine.Count == 16) selected.AddRange(fine);
            else if (saved.Tiles.TryGetValue(Key(parent.X, parent.Z, 32), out var coarse)) selected.Add(coarse);
        }
        var tiles = selected.ToImmutable();
        if (!view.Tiles.SequenceEqual(tiles)) view = new(tiles);
    }

    internal ImmutableArray<SceneObject> Objects(ScenePoint origin, Func<string, SceneObjectId> objectId,
        FarTerrainView? visible = null)
    {
        lock (gate)
        {
            var selected = visible ?? view;
            var result = ImmutableArray.CreateBuilder<SceneObject>(selected.Tiles.Length);
            foreach (var tile in selected.Tiles)
            {
                var position = new Vector3((float)(tile.Origin.X - origin.X), (float)(tile.Origin.Y - origin.Y), (float)(tile.Origin.Z - origin.Z));
                if (!objects.TryGetValue(tile, out var item))
                    item = new(objectId($"far:{tile.SpacingCells}:{tile.Origin.X}:{tile.Origin.Z}"), null,
                        new(position, Quaternion.Identity), null, [new SceneVisual.Geometry(tile.Mesh, new(false, 0, 0, false))], [], []);
                else if (item.Pose.Position != position) item = item with { Pose = new(position, Quaternion.Identity) };
                objects[tile] = item; result.Add(item);
            }
            return result.ToImmutable();
        }
    }

    private sealed class Job
    {
        internal IEnumerator<(string Key, TerrainHeightCap.Builder Work)>? Parts;
        internal bool PartsComplete;
        internal TerrainHeightCap.Builder? Current;
        internal string? PartKey;
        internal List<TerrainHeightCap> Caps = [];
        internal TerrainTile.Builder? Tile;
    }
}
