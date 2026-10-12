using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using C = Train.Content.Contracts;

namespace Train.Content.Tests.Loading;

internal sealed class ContentFixture : C.IContentFiles, C.IAssetInspector
{
    internal const string VoxelJson = """
        {"schema_version":3,"id":"block","simulation":{"value":20,"mass":2,"max_durability":100,"tags":["raw_good"]},
         "appearance":{"allowed_orientations":"four_way","presentation":{"kind":"cube","faces":{"top":"tile.png","side":"tile.png","bottom":"tile.png"}}},
         "display":{"name":"方块","description":"测试结构体素","icon":"icon.png"}}
        """;
    internal static readonly C.ContentRoot BuiltIn = new("builtin");
    internal static readonly C.ContentRoot Mods = new("mods");
    internal C.ContentInstallation Installation { get; set; } = new(BuiltIn, Mods, null, "0.0.56", []);
    internal bool ReverseEnumeration { get; set; }
    private readonly Dictionary<C.ContentPath, List<C.ContentFileEntry>> _directories = [];
    private readonly Dictionary<C.ContentPath, C.ContentIoResult<ImmutableArray<byte>>> _files = [];
    private readonly List<(C.AssetKind Kind, ImmutableArray<byte> Bytes, C.AssetInspection Result)> _assets = [];

    internal ContentFixture()
    {
        AddDirectory(BuiltIn, "resources");
        AddDirectory(Mods, "");
    }

    internal void AddDirectory(C.ContentRoot root, string path)
    {
        var key = new C.ContentPath(root, path);
        if (!_directories.TryAdd(key, [])) return;
        if (path.Length == 0) return;
        var slash = path.LastIndexOf('/');
        var parent = slash < 0 ? "" : path[..slash];
        AddDirectory(root, parent);
        SetEntry(root, parent, new(path[(slash + 1)..], C.ContentEntryKind.Directory));
    }

    internal void AddFile(C.ContentRoot root, string path, string text) =>
        AddFile(root, path, Encoding.UTF8.GetBytes(text));

    internal void AddFile(C.ContentRoot root, string path, byte[] bytes)
    {
        var slash = path.LastIndexOf('/');
        var parent = slash < 0 ? "" : path[..slash];
        AddDirectory(root, parent);
        SetEntry(root, parent, new(path[(slash + 1)..], C.ContentEntryKind.File));
        _files[new(root, path)] = new C.ContentIoResult<ImmutableArray<byte>>.Success(bytes.ToImmutableArray());
    }

    internal void SetReadFailure(C.ContentRoot root, string path, C.ContentIoFailure failure)
    {
        AddFile(root, path, Array.Empty<byte>());
        _files[new(root, path)] = new C.ContentIoResult<ImmutableArray<byte>>.Failure(failure, "fixture read failure");
    }

    internal void SetEntry(C.ContentRoot root, string parent, C.ContentFileEntry entry)
    {
        _directories[new(root, parent)].RemoveAll(item => item.Name == entry.Name);
        _directories[new(root, parent)].Add(entry);
    }

    internal void AddAsset(C.ContentRoot root, string path, byte[] bytes, C.AssetMetadata metadata)
    {
        AddFile(root, path, bytes);
        var kind = path.EndsWith(".png", StringComparison.Ordinal) ? C.AssetKind.Png : C.AssetKind.Glb;
        _assets.RemoveAll(asset => asset.Kind == kind && asset.Bytes.AsSpan().SequenceEqual(bytes));
        _assets.Add((kind, bytes.ToImmutableArray(), new C.AssetInspection.Success(metadata)));
    }

    internal void AddCube(string json = VoxelJson)
    {
        AddFile(BuiltIn, "resources/voxels/block.voxel.json", json);
        AddAsset(BuiltIn, "resources/voxels/block/tile.png", [1, 2, 3], new C.AssetMetadata.Png(32, 32));
        AddAsset(BuiltIn, "resources/voxels/block/icon.png", [4, 5, 6], new C.AssetMetadata.Png(128, 128));
    }

    internal C.ContentResult<C.IContentCandidate> Prepare() => ContentFactory.Create().PrepareLoad(
        new(Installation, new C.ContentLoadPurpose.Initial()), this, this);

    internal C.IContentCandidate Load()
    {
        var result = ContentFactory.Create().PrepareLoad(new(Installation, new C.ContentLoadPurpose.Initial()), this, this);
        Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Success>(result, result.ToString());
        return ((C.ContentResult<C.IContentCandidate>.Success)result).Value;
    }

    public C.ContentIoResult<ImmutableArray<C.ContentFileEntry>> Enumerate(C.ContentPath directory)
    {
        if (!_directories.TryGetValue(directory, out var entries))
            throw new InvalidOperationException("UNCONFIGURED enumeration: " + directory);
        return new C.ContentIoResult<ImmutableArray<C.ContentFileEntry>>.Success(
            (ReverseEnumeration ? entries.AsEnumerable().Reverse() : entries).ToImmutableArray());
    }

    public C.ContentIoResult<ImmutableArray<byte>> Read(C.ContentPath file, long maximumBytes)
    {
        if (!_files.TryGetValue(file, out var result))
            throw new InvalidOperationException("UNCONFIGURED read: " + file);
        if (result is C.ContentIoResult<ImmutableArray<byte>>.Success success && success.Value.Length > maximumBytes)
            return new C.ContentIoResult<ImmutableArray<byte>>.Failure(C.ContentIoFailure.LimitExceeded, "fixture limit");
        return result;
    }

    public C.AssetInspection Inspect(C.AssetKind kind, C.AssetRevision revision, ImmutableArray<byte> bytes)
    {
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(bytes.AsSpan())), revision.Value);
        foreach (var asset in _assets)
            if (asset.Kind == kind && asset.Bytes.SequenceEqual(bytes)) return asset.Result;
        throw new InvalidOperationException("UNCONFIGURED inspection: " + revision);
    }
}
