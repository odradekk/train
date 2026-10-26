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
        internal ScenePanBounds ReadBounds => new(
            new(X - TerrainTile.ColumnMarginCells, Z - TerrainTile.ColumnMarginCells),
            new(X + Size + TerrainTile.ColumnMarginCells, Z + Size + TerrainTile.ColumnMarginCells));
    }
    internal sealed class Saved
    {
        internal long Revision;
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
        internal FarTerrainView View(string owner)
        {
            lock (entries) return entries.TryGetValue(owner, out var entry) ? entry.Saved.View : new([]);
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
    private Saved saved = new();
    private sealed record Source(Func<ScenePoint, double> Height,
        Func<ScenePanBounds, IEnumerable<(string Key, TerrainHeightCap.Builder Work)>> Parts,
        SceneMaterial Material, int TextureSpan, Func<ScenePanBounds, string> Inputs);
    private Source? source;
    private sealed record Binding(string Owner, string Generation, Func<Saved, Source> Create);
    private Func<Binding>? initialize;
    private object? bound;
    private ContentGeneration content;
    private FarTerrainView view = new([]);
    private long publishedRevision = -1;
    private readonly record struct ParentRange(int Left, int Back, int Right, int Front)
    {
        internal static ParentRange From(double x0, double z0, double x1, double z1) => new(
            (int)Math.Floor(x0 / TerrainTile.ParentCells) * TerrainTile.ParentCells,
            (int)Math.Floor(z0 / TerrainTile.ParentCells) * TerrainTile.ParentCells,
            (int)Math.Ceiling(x1 / TerrainTile.ParentCells) * TerrainTile.ParentCells,
            (int)Math.Ceiling(z1 / TerrainTile.ParentCells) * TerrainTile.ParentCells);
    }
    private (ParentRange Visible, ParentRange Fine, ParentRange? Prefetch)? demandBounds;
    private bool fineBuildAllowed = true;
    internal FarTerrainView View { get { lock (gate) return view; } }
    internal bool HasRunnableWork { get { lock (gate) return initialize is not null || NextMissing(fineBuildAllowed) is not null; } }
    internal void SetFineBuildAllowed(bool allowed) { lock (gate) fineBuildAllowed = allowed; }
    internal bool HasPendingCoarse { get { lock (gate) return initialize is not null || NextMissing(false) is not null; } }

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
            bound = path; content = snapshot.Content.Generation;
            BeginBinding("edge:" + path.Edge.Id.Value);
            initialize = () =>
            {
                var edge = path.Edge;
                var key = RailwayDependencies.Values(new object?[] { path.Seed, edge.Id, edge.LengthMeters,
                    edge.Tracks, edge.TrackLimit, edge.Terrain, path.Start.Position, path.End.Position,
                    path.Start.Terrain, path.End.Terrain, path.Rules,
                    edge.Breaks.OrderBy(item => item.Id.Value).ThenBy(item => item.Track).Select(item => new object[] { item.Id, item.Track, item.DistanceMeters }).ToArray(),
                    RailwayScene.RepairIdentity(edge),
                    NodeLayout.ReadInputs(snapshot, path.Start.Id), NodeLayout.ReadInputs(snapshot, path.End.Id), snapshot.Content.Generation });
                return new("edge:" + edge.Id.Value, key, target =>
                {
                    var caps = new RailwayTerrainCaps(snapshot, path, key => target.Caps.GetValueOrDefault(key));
                    var (material, span) = Appearance(snapshot.Content, caps.Terrain);
                    return new(caps.Height, caps.Parts, material, span, caps.CreateInputs(material, span));
                });
            };
        }
    }

    internal void BindNode(NodeScene.Preparation prepared)
    {
        lock (gate)
        {
            if (ReferenceEquals(bound, prepared) && content == prepared.Snapshot.Content.Generation) return;
            bound = prepared; content = prepared.Snapshot.Content.Generation;
            BeginBinding("node:" + prepared.Global.Layout.Node.Value);
            initialize = () =>
            {
                var key = RailwayDependencies.Values(new object?[] { prepared.TerrainInputs, prepared.Global.Layout.Inputs,
                    prepared.Rails.OrderBy(item => item.Key).Select(item => item.Value.Inputs).ToArray(),
                    prepared.Global.Parts.Where(item => NodePartKeys.IsBoundary(item.Key)).OrderBy(item => item.Key).Select(item => item.Value.Inputs).ToArray(),
                    prepared.Snapshot.Content.Generation });
                return new("node:" + prepared.Global.Layout.Node.Value, key, target =>
                {
                    var caps = new NodeTerrainCaps(prepared, key => target.Caps.GetValueOrDefault(key));
                    var node = prepared.Snapshot.Core.World.Nodes.Single(item => item.Id == prepared.Global.Layout.Node);
                    var (material, span) = Appearance(prepared.Snapshot.Content, prepared.Terrain);
                    var groundInputs = RailwayDependencies.Values(new object?[] { prepared.Snapshot.Core.World.WorldSeed,
                        node.Id, node.Position, node.Terrain.Relief, prepared.Terrain?.LocalHeightNoise, material, span });
                    var tileGeneration = Fingerprint(key);
                    groundInputs = Fingerprint(groundInputs);
                    return new(caps.Height, caps.Parts, material, span,
                        bounds => caps.UsesLayout(bounds) ? tileGeneration : groundInputs);
                });
            };
        }
    }

    private static string Fingerprint(string inputs) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inputs)));

    private void BeginBinding(string owner)
    {
        foreach (var job in jobs.Values) if (!job.Processing) job.Parts?.Dispose();
        saved = new();
        view = cache.View(owner);
        jobs.Clear(); wanted.Clear(); visibleParents.Clear(); fineParents.Clear(); objects.Clear();
        demandBounds = null; publishedRevision = -1;
        source = null; initialize = null;
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
            if (bound is null) return false;
            var minX = focus.X; var maxX = focus.X;
            var minZ = focus.Z; var maxZ = focus.Z;
            if (ahead is not null) foreach (var point in ahead)
            {
                minX = Math.Min(minX, point.X); maxX = Math.Max(maxX, point.X);
                minZ = Math.Min(minZ, point.Z); maxZ = Math.Max(maxZ, point.Z);
            }
            var visible = wholeDomain is null ? ParentRange.From(minX - radius, minZ - radius, maxX + radius, maxZ + radius) :
                ParentRange.From(wholeDomain.Minimum.X - radius, wholeDomain.Minimum.Y - radius,
                    wholeDomain.Maximum.X + radius, wholeDomain.Maximum.Y + radius);
            var half = TerrainTile.ParentCells / 2;
            var fine = ParentRange.From(focus.X - half, focus.Z - half, focus.X + half, focus.Z + half);
            ParentRange? prefetched = ahead is null ? null : ParentRange.From(minX - half, minZ - half, maxX + half, maxZ + half);
            var bounds = (visible, fine, prefetched);
            if (demandBounds == bounds)
            {
                // Another scene can complete tiles in the shared cache without advancing this worker.
                if (publishedRevision != Volatile.Read(ref saved.Revision)) Publish();
                return false;
            }
            demandBounds = bounds;
            var nextVisible = new HashSet<(int X, int Z)>();
            AddParents(nextVisible, visible);
            var nextFine = new HashSet<(int X, int Z)>();
            AddParents(nextFine, fine);
            var prefetch = new HashSet<(int X, int Z)>();
            if (prefetched is { } prefetchRange) AddParents(prefetch, prefetchRange);
            visibleParents = nextVisible; fineParents = nextFine;
            wanted = [];
            foreach (var parent in visibleParents.OrderBy(p => Distance(p, focus))) wanted.Add(Key(parent.X, parent.Z, TerrainTile.FarSpacingCells));
            AddFine(fineParents.OrderBy(p => Distance(p, focus)));
            AddFine(prefetch.Except(fineParents).OrderBy(p => Distance(p, focus)));
            var required = wanted.ToHashSet();
            var coordinates = wanted.Select(key => key.Coordinate).ToHashSet();
            foreach (var coordinate in saved.Keys.Keys.Where(at => !coordinates.Contains(at)))
                if (saved.Keys.TryRemove(coordinate, out _)) Interlocked.Increment(ref saved.Revision);
            foreach (var key in jobs.Keys.Where(key => !required.Contains(key)).ToArray())
            { if (!jobs[key].Processing) jobs[key].Parts?.Dispose(); jobs.Remove(key); }
            foreach (var key in saved.Tiles.Keys.Where(key => !coordinates.Contains(key.Coordinate) ||
                saved.Keys.TryGetValue(key.Coordinate, out var current) && current != key))
                if (saved.Tiles.TryRemove(key, out _)) Interlocked.Increment(ref saved.Revision);
            Publish();
            return true;
        }
        void AddFine(IEnumerable<(int X, int Z)> parents)
        {
            foreach (var parent in parents)
            for (var z = 0; z < 4; z++) for (var x = 0; x < 4; x++)
                wanted.Add(Key(parent.X + x * (TerrainTile.ParentCells / 4), parent.Z + z * (TerrainTile.ParentCells / 4), TerrainTile.NearSpacingCells));
        }
    }

    private static double Distance((int X, int Z) p, ScenePoint focus) => Math.Abs(p.X + TerrainTile.ParentCells / 2 - focus.X) + Math.Abs(p.Z + TerrainTile.ParentCells / 2 - focus.Z);
    private static void AddParents(HashSet<(int X, int Z)> result, ParentRange range)
    {
        for (var z = range.Back; z < range.Front; z += TerrainTile.ParentCells)
        for (var x = range.Left; x < range.Right; x += TerrainTile.ParentCells) result.Add((x, z));
    }
    private TileKey Key(int x, int z, int spacing)
    {
        if (saved.Keys.TryGetValue((x, z, spacing), out var found)) return found;
        return new TileKey(x, z, spacing, "");
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
                if (required.Contains(tile.Key.Coordinate) && saved.Tiles.TryAdd(tile.Key, tile.Value))
                {
                    Interlocked.Increment(ref saved.Revision);
                    if (jobs.Remove(tile.Key, out var job) && !job.Processing) job.Parts?.Dispose();
                }
            Publish(previousView);
        }
    }

    internal bool Advance(int budget, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budget);
        token.ThrowIfCancellationRequested();
        Func<Binding>? prepare;
        Source? input;
        Saved target;
        TileKey[] unresolved = [];
        lock (gate)
        {
            prepare = initialize; input = source; target = saved;
            if (NextMissing(fineBuildAllowed) is { Inputs.Length: 0 } next)
            {
                var x = (int)Math.Floor(next.X / (double)TerrainTile.ParentCells) * TerrainTile.ParentCells;
                var z = (int)Math.Floor(next.Z / (double)TerrainTile.ParentCells) * TerrainTile.ParentCells;
                unresolved = wanted.Where(key => key.Inputs.Length == 0 &&
                    key.X >= x && key.X < x + TerrainTile.ParentCells &&
                    key.Z >= z && key.Z < z + TerrainTile.ParentCells).ToArray();
            }
        }
        if (prepare is not null)
        {
            var binding = prepare();
            Saved initialized;
            lock (gate)
            {
                if (!ReferenceEquals(initialize, prepare)) return false;
                initialized = cache.Get(binding.Owner, binding.Generation);
            }
            var ready = binding.Create(initialized);
            token.ThrowIfCancellationRequested();
            lock (gate)
                if (ReferenceEquals(initialize, prepare))
                {
                    foreach (var tile in saved.Tiles)
                        if (initialized.Tiles.TryAdd(tile.Key, tile.Value)) Interlocked.Increment(ref initialized.Revision);
                    saved = initialized;
                    source = ready; initialize = null;
                    demandBounds = null;
                    wanted = wanted.Select(key => Key(key.X, key.Z, key.Spacing)).ToList();
                    Publish();
                }
            return false;
        }
        if (input is not null && unresolved.Length > 0)
        {
            // Content/model projections belong to far work; Demand and frame reads must not wait for them.
            // Resolve a whole parent together so cached L1 never briefly falls back to its already-resolved L2.
            var keys = unresolved.Select(key => key with { Inputs = input.Inputs(key.ReadBounds) }).ToArray();
            token.ThrowIfCancellationRequested();
            lock (gate)
                if (ReferenceEquals(saved, target) && ReferenceEquals(source, input))
                {
                    foreach (var key in keys)
                        if (wanted.IndexOf(key with { Inputs = "" }) is var index && index >= 0)
                        {
                            saved.Keys[key.Coordinate] = key;
                            Interlocked.Increment(ref saved.Revision);
                            wanted[index] = key;
                        }
                    foreach (var old in saved.Tiles.Keys.Where(old =>
                        saved.Keys.TryGetValue(old.Coordinate, out var current) && old != current))
                        if (saved.Tiles.TryRemove(old, out _)) Interlocked.Increment(ref saved.Revision);
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
                { saved.Tiles[key] = job.Tile.Result; Interlocked.Increment(ref saved.Revision); jobs.Remove(key); Publish(); }
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
                job.Parts = input.Parts(key.ReadBounds).GetEnumerator();
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
        var observedRevision = Volatile.Read(ref saved.Revision);
        var previous = view.Tiles.ToLookup(Parent);
        var transferred = inherited?.Tiles.ToLookup(Parent);
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
                var older = transferred?[parent].Distinct().ToArray() ?? [];
                if (older.Length == 0) older = previous[parent].Distinct().ToArray();
                if (older.FirstOrDefault(tile => tile.SpacingCells == TerrainTile.FarSpacingCells) is { } oldCoarse)
                    selected.Add(oldCoarse);
                else if (older.Length > 0) selected.AddRange(older.DistinctBy(tile => tile.Origin));
                else selected.AddRange(fine);
            }
        }
        var tiles = selected.ToImmutable();
        if (!view.Tiles.SequenceEqual(tiles)) view = new(tiles);
        saved.View = view;
        publishedRevision = observedRevision;
        var retained = tiles.ToHashSet();
        foreach (var tile in objects.Keys.Where(tile => !retained.Contains(tile)).ToArray()) objects.Remove(tile);

        static (int X, int Z) Parent(TerrainTile tile) => (
            (int)Math.Floor(tile.Origin.X / TerrainTile.ParentCells) * TerrainTile.ParentCells,
            (int)Math.Floor(tile.Origin.Z / TerrainTile.ParentCells) * TerrainTile.ParentCells);
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
