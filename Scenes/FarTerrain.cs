using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class FarTerrain(FarTerrain.Cache cache)
{
    internal readonly record struct TileKey(int X, int Z, int Spacing, string Inputs)
    {
        internal int Size => Spacing == TerrainTile.FarSpacingCells ? TerrainTile.ParentCells : TerrainTile.ParentCells / 4;
        internal (int X, int Z, int Spacing) Coordinate => (X, Z, Spacing);
    }
    internal sealed class Saved
    {
        internal ConcurrentDictionary<string, TerrainHeightCap> Caps { get; } = new(StringComparer.Ordinal);
        internal ConcurrentDictionary<TileKey, TerrainTile> Tiles { get; } = new();
        internal ConcurrentDictionary<(int X, int Z, int Spacing), TileKey> Keys { get; } = new();
        internal volatile FarTerrainView View = new([]);
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
                if (entry is not null)
                {
                    foreach (var tile in entry.Saved.Tiles) saved.Tiles.TryAdd(tile.Key, tile.Value);
                    saved.View = entry.Saved.View;
                }
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
    private sealed record Source(Func<ScenePoint, double> Height,
        Func<ScenePanBounds, IEnumerable<(string Key, TerrainHeightCap.Builder Work)>> Parts,
        SceneMaterial Material, int TextureSpan, Func<ScenePanBounds, string> Inputs);
    private Source? source;
    private Func<Source>? initialize;
    private Func<ScenePanBounds, string>? tileInputs;
    private string owner = "", generation = "";
    private object? bound;
    private ContentGeneration content;
    private FarTerrainView view = new([]);
    private bool fineBuildAllowed = true;
    internal FarTerrainView View { get { lock (gate) return view; } }
    internal bool HasRunnableWork { get { lock (gate) return initialize is not null || NextMissing(fineBuildAllowed) is not null; } }
    internal void SetFineBuildAllowed(bool allowed) { lock (gate) fineBuildAllowed = allowed; }
    internal bool HasPendingCoarse { get { lock (gate) return NextMissing(false) is not null; } }

    private TileKey? NextMissing(bool includeFine = true)
    {
        foreach (var key in wanted)
            if ((includeFine || key.Spacing == TerrainTile.FarSpacingCells) && !saved.Tiles.ContainsKey(key)) return key;
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
                RailwayScene.RepairIdentity(edge),
                NodeLayout.ReadInputs(snapshot, path.Start.Id), NodeLayout.ReadInputs(snapshot, path.End.Id), snapshot.Content.Generation });
            bound = path; content = snapshot.Content.Generation;
            if (!Reset("edge:" + edge.Id.Value, key)) return;
            var capCache = saved.Caps;
            initialize = () =>
            {
                var caps = new RailwayTerrainCaps(snapshot, path, key => capCache.GetValueOrDefault(key));
                var (material, span) = Appearance(snapshot.Content, caps.Terrain);
                return new(caps.Height, caps.Parts, material, span, caps.CreateInputs(material, span));
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
            var capCache = saved.Caps;
            var caps = new NodeTerrainCaps(prepared, key => capCache.GetValueOrDefault(key));
            var node = prepared.Snapshot.Core.World.Nodes.Single(item => item.Id == prepared.Global.Layout.Node);
            var (material, span) = Appearance(prepared.Snapshot.Content, prepared.Terrain);
            var groundInputs = RailwayDependencies.Values(new object?[] { prepared.Snapshot.Core.World.WorldSeed,
                node.Id, node.Position, node.Terrain.Relief, prepared.Terrain?.LocalHeightNoise, material, span });
            var tileGeneration = Fingerprint(key);
            groundInputs = Fingerprint(groundInputs);
            tileInputs = bounds => caps.UsesLayout(bounds) ? tileGeneration : groundInputs;
            var inputs = tileInputs;
            initialize = () => new(caps.Height, caps.Parts, material, span, inputs);
        }
    }

    private static string Fingerprint(string inputs) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inputs)));

    private bool Reset(string nextOwner, string nextGeneration)
    {
        if (owner == nextOwner && generation == nextGeneration) return false;
        foreach (var job in jobs.Values) if (!job.Processing) job.Parts?.Dispose();
        owner = nextOwner; generation = nextGeneration;
        saved = cache.Get(owner, generation);
        jobs.Clear(); wanted.Clear(); visibleParents.Clear(); fineParents.Clear(); prefetchParents.Clear(); objects.Clear();
        view = saved.View; source = null; initialize = null; tileInputs = null;
        return true;
    }

    private static (SceneMaterial Material, int Span) Appearance(IContentView contentView, TerrainAppearanceProjection? terrain)
    {
        if (terrain is not null && contentView.FindVoxelAppearance(terrain.SurfaceVoxel) is ContentLookup<VoxelAppearanceProjection>.Found found &&
            found.Value.Presentation is VoxelPresentation.Cube cube)
            return (new SceneMaterial.Voxel(cube.Top, cube.Material, null), cube.TileSpan);
        return (new SceneMaterial.BuiltIn(BuiltInMaterial.Sleeper), 1);
    }

    internal bool Demand(ScenePoint focus, double radius, ScenePanBounds? wholeDomain = null, IReadOnlyList<ScenePoint>? ahead = null)
    {
        lock (gate)
        {
            if (owner.Length == 0) return false;
            var nextVisible = new HashSet<(int X, int Z)>();
            if (wholeDomain is not null)
                AddParents(nextVisible, wholeDomain.Minimum.X - radius, wholeDomain.Minimum.Y - radius,
                    wholeDomain.Maximum.X + radius, wholeDomain.Maximum.Y + radius);
            else
                AddSweep(nextVisible, radius);
            var nextFine = new HashSet<(int X, int Z)>();
            AddParents(nextFine, focus.X - TerrainTile.ParentCells / 2, focus.Z - TerrainTile.ParentCells / 2, focus.X + TerrainTile.ParentCells / 2, focus.Z + TerrainTile.ParentCells / 2);
            var prefetch = new HashSet<(int X, int Z)>();
            if (ahead is not null) AddSweep(prefetch, TerrainTile.ParentCells / 2);
            if (visibleParents.SetEquals(nextVisible) && fineParents.SetEquals(nextFine) && prefetchParents.SetEquals(prefetch))
            { Publish(); return false; }
            visibleParents = nextVisible; fineParents = nextFine; prefetchParents = prefetch;
            wanted = [];
            foreach (var parent in visibleParents.OrderBy(p => Distance(p, focus))) wanted.Add(Key(parent.X, parent.Z, TerrainTile.FarSpacingCells));
            AddFine(fineParents.OrderBy(p => Distance(p, focus)));
            AddFine(prefetch.Except(fineParents).OrderBy(p => Distance(p, focus)));
            var required = wanted.ToHashSet();
            var coordinates = wanted.Select(key => key.Coordinate).ToHashSet();
            foreach (var coordinate in saved.Keys.Keys.Where(at => !coordinates.Contains(at))) saved.Keys.TryRemove(coordinate, out _);
            foreach (var key in jobs.Keys.Where(key => !required.Contains(key)).ToArray())
            { if (!jobs[key].Processing) jobs[key].Parts?.Dispose(); jobs.Remove(key); }
            foreach (var key in saved.Tiles.Keys.Where(key => !coordinates.Contains(key.Coordinate) ||
                saved.Keys.TryGetValue(key.Coordinate, out var current) && current != key)) saved.Tiles.TryRemove(key, out _);
            Publish();
            return true;
        }
        void AddFine(IEnumerable<(int X, int Z)> parents)
        {
            foreach (var parent in parents)
            for (var z = 0; z < 4; z++) for (var x = 0; x < 4; x++)
                wanted.Add(Key(parent.X + x * (TerrainTile.ParentCells / 4), parent.Z + z * (TerrainTile.ParentCells / 4), TerrainTile.NearSpacingCells));
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

    private static double Distance((int X, int Z) p, ScenePoint focus) => Math.Abs(p.X + TerrainTile.ParentCells / 2 - focus.X) + Math.Abs(p.Z + TerrainTile.ParentCells / 2 - focus.Z);
    private static void AddParents(HashSet<(int X, int Z)> result, double x0, double z0, double x1, double z1)
    {
        for (var z = (int)Math.Floor(z0 / TerrainTile.ParentCells) * TerrainTile.ParentCells; z < z1; z += TerrainTile.ParentCells)
        for (var x = (int)Math.Floor(x0 / TerrainTile.ParentCells) * TerrainTile.ParentCells; x < x1; x += TerrainTile.ParentCells) result.Add((x, z));
    }
    private TileKey Key(int x, int z, int spacing)
    {
        if (saved.Keys.TryGetValue((x, z, spacing), out var found)) return found;
        var key = new TileKey(x, z, spacing, "");
        if (tileInputs is null) return key;
        var margin = TerrainTile.ColumnMarginCells;
        return saved.Keys[(x, z, spacing)] = key with
        { Inputs = tileInputs!(new(new(x - margin, z - margin), new(x + key.Size + margin, z + key.Size + margin))) };
    }

    internal bool IntersectsDemand(SceneBounds bounds)
    {
        lock (gate)
            foreach (var key in wanted)
            {
                if (key.X < bounds.Maximum.X && key.X + key.Size > bounds.Minimum.X &&
                    key.Z < bounds.Maximum.Z && key.Z + key.Size > bounds.Minimum.Z) return true;
            }
        return false;
    }

    internal void ReuseTiles(FarTerrain previous)
    {
        KeyValuePair<TileKey, TerrainTile>[] tiles;
        FarTerrainView previousView;
        lock (previous.gate) { tiles = previous.saved.Tiles.ToArray(); previousView = previous.view; }
        lock (gate)
        {
            var required = wanted.Select(key => key.Coordinate).ToHashSet();
            foreach (var tile in tiles)
                if (required.Contains(tile.Key.Coordinate) && saved.Tiles.TryAdd(tile.Key, tile.Value) &&
                    jobs.Remove(tile.Key, out var job) && !job.Processing) job.Parts?.Dispose();
            Publish(previousView);
        }
    }

    internal bool Advance(int budget, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget);
        token.ThrowIfCancellationRequested();
        Func<Source>? prepare;
        Source? input;
        Saved target;
        TileKey? missing;
        lock (gate) { prepare = initialize; input = source; target = saved; missing = NextMissing(fineBuildAllowed); }
        if (prepare is not null)
        {
            var ready = prepare();
            lock (gate)
                if (ReferenceEquals(initialize, prepare)) { source = ready; initialize = null; }
            return false;
        }
        if (input is not null && missing is { Inputs.Length: 0 } unresolved)
        {
            // Content/model projections belong to far work; Demand and frame reads must not wait for them.
            var margin = TerrainTile.ColumnMarginCells;
            var key = unresolved with { Inputs = input.Inputs(new(new(unresolved.X - margin, unresolved.Z - margin),
                new(unresolved.X + unresolved.Size + margin, unresolved.Z + unresolved.Size + margin))) };
            token.ThrowIfCancellationRequested();
            lock (gate)
                if (ReferenceEquals(saved, target) && ReferenceEquals(source, input) && wanted.IndexOf(unresolved) is var index && index >= 0)
                {
                    saved.Keys[key.Coordinate] = key;
                    wanted[index] = key;
                    foreach (var old in saved.Tiles.Keys.Where(old => old.Coordinate == key.Coordinate && old != key))
                        saved.Tiles.TryRemove(old, out _);
                    Publish();
                }
            return false;
        }
        return AdvanceTile(budget, token);
    }

    internal bool AdvanceTile(int budget, CancellationToken token)
    {
        Job job;
        TileKey key;
        Source input;
        Saved target;
        lock (gate)
        {
            // Binding may have changed since Advance checked initialization outside this lock.
            if (initialize is not null || source is null) return false;
            if (NextMissing(fineBuildAllowed) is not { } next) return true;
            if (next.Inputs.Length == 0) return false;
            key = next;
            if (!jobs.TryGetValue(key, out job!)) jobs[key] = job = new();
            job.Processing = true;
            input = source;
            target = saved;
        }
        try
        {
            AdvanceJob(job, key, input, target, budget, token);
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                if (ReferenceEquals(saved, target) && jobs.TryGetValue(key, out var current) && ReferenceEquals(current, job) && job.Tile?.Complete == true)
                { saved.Tiles[key] = job.Tile.Result; jobs.Remove(key); Publish(); }
            }
        }
        finally
        {
            lock (gate)
            {
                job.Processing = false;
                if (!jobs.TryGetValue(key, out var current) || !ReferenceEquals(current, job)) job.Parts?.Dispose();
            }
        }
        return !HasRunnableWork;
    }

    // The scene owns one worker. Main-thread demand and frame reads never wait for a work unit.
    private static void AdvanceJob(Job job, TileKey key, Source input, Saved target, int budget, CancellationToken token)
    {
        var size = key.Size;
        while (budget > 0)
        {
            token.ThrowIfCancellationRequested();
            if (job.Parts is null)
            {
                var margin = TerrainTile.ColumnMarginCells;
                job.Parts = input.Parts(new(new(key.X - margin, key.Z - margin), new(key.X + size + margin, key.Z + size + margin))).GetEnumerator();
                budget--; continue;
            }
            if (!job.PartsComplete)
            {
                if (job.Current is null)
                {
                    if (!job.Parts.MoveNext()) { job.PartsComplete = true; job.Parts.Dispose(); }
                    else { job.Current = job.Parts.Current.Work; job.PartKey = job.Parts.Current.Key; }
                    budget--; continue;
                }
                if (!job.Current.Complete) { budget -= job.Current.Advance(budget, token); continue; }
                target.Caps[job.PartKey!] = job.Current.Result;
                job.Caps.Add(job.Current.Result); job.Current = null;
                budget--; continue;
            }
            if (job.Tile is null)
            {
                job.Tile = TerrainTile.Begin(new(key.X, 0, key.Z), size, key.Spacing, input.Height, input.Material, input.TextureSpan, job.Caps);
                budget--; continue;
            }
            budget -= job.Tile.Advance(budget, token);
            if (job.Tile.Complete) return;
        }
    }

    private void Publish(FarTerrainView? inherited = null)
    {
        var previous = view.Tiles.Concat(inherited?.Tiles ?? []).ToLookup(tile => (
            X: (int)Math.Floor(tile.Origin.X / TerrainTile.ParentCells) * TerrainTile.ParentCells,
            Z: (int)Math.Floor(tile.Origin.Z / TerrainTile.ParentCells) * TerrainTile.ParentCells));
        var selected = ImmutableArray.CreateBuilder<TerrainTile>();
        foreach (var parent in visibleParents)
        {
            var fine = new List<TerrainTile>(16);
            if (fineParents.Contains(parent))
                for (var z = 0; z < 4; z++) for (var x = 0; x < 4; x++)
                    if (saved.Tiles.TryGetValue(Key(parent.X + x * (TerrainTile.ParentCells / 4), parent.Z + z * (TerrainTile.ParentCells / 4), TerrainTile.NearSpacingCells), out var tile)) fine.Add(tile);
            if (fine.Count == 16) selected.AddRange(fine);
            else if (saved.Tiles.TryGetValue(Key(parent.X, parent.Z, TerrainTile.FarSpacingCells), out var coarse)) selected.Add(coarse);
            else
            {
                var older = previous[parent].Distinct().ToArray();
                if (older.FirstOrDefault(tile => tile.SpacingCells == TerrainTile.FarSpacingCells) is { } oldCoarse)
                    selected.Add(oldCoarse);
                else if (older.Length > 0) selected.AddRange(older.DistinctBy(tile => tile.Origin));
                else selected.AddRange(fine);
            }
        }
        var tiles = selected.ToImmutable();
        if (!view.Tiles.SequenceEqual(tiles)) view = new(tiles);
        saved.View = view;
        var retained = tiles.ToHashSet();
        foreach (var tile in objects.Keys.Where(tile => !retained.Contains(tile)).ToArray()) objects.Remove(tile);
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
        internal bool Processing;
        internal IEnumerator<(string Key, TerrainHeightCap.Builder Work)>? Parts;
        internal bool PartsComplete;
        internal TerrainHeightCap.Builder? Current;
        internal string? PartKey;
        internal List<TerrainHeightCap> Caps = [];
        internal TerrainTile.Builder? Tile;
    }
}
