// 属于体素内容模块。
// 解析体素定义、校验 PNG 表现和体素数量，生成候选条目及其修订。
namespace Train.Core.Content.Voxels;

/// <summary>一轮加载中的 schema 2 体素格式；不发布目录，也不决定其他格式的隔离。</summary>
internal sealed class VoxelContentFormat : ContentFormat<VoxelDefinitionId, VoxelContentEntry>
{
    public override string FileSuffix => ".voxels.json";
    public override string LogKind => "voxels";
    public override bool Accepts(ContentPackage package, string file) =>
        package.Kind != ContentPackageKind.PlayerBlueprint && base.Accepts(package, file);
    protected override VoxelDefinitionId Identity(VoxelContentEntry entry) => entry.Definition.Id;
    protected override string Fingerprint(VoxelContentEntry entry) => entry.PublicationFingerprint;
    public override bool HasChanges(ContentSnapshot previous) => HasChanges(previous.Voxels);

    /// <summary>体素当前没有跨定义引用；其字段和资产在解析时完成校验。</summary>
    public override void ResolveReferences() { }

    /// <summary>严格解析文件，并用同一份已验证 PNG 字节计算资产修订。</summary>
    protected override IReadOnlyList<VoxelContentEntry> Parse(
        IContentSource source, ContentPackageLoad load, string file, byte[] bytes)
    {
        var entries = new List<VoxelContentEntry>();
        foreach (var parsed in VoxelsJsonParser.Parse(bytes, file, load.Diagnostics))
        {
            if (!TryLoadImages(source, load, parsed.Presentation, out var images, out var assets))
                continue;
            var definition = new VoxelDefinition(
                new VoxelDefinitionId(load.Package.Namespace, parsed.LocalId), parsed.Name, parsed.Description,
                parsed.Value, parsed.Mass, parsed.MaxDurability, parsed.Tags, parsed.Behaviors,
                parsed.AllowedOrientations, parsed.Presentation, parsed.Collision);
            var semantic = VoxelRevisions.ComputeSemanticRevision(definition);
            entries.Add(new VoxelContentEntry(definition, new ContentOrigin(load.Package.Namespace, file),
                semantic, assets, VoxelRevisions.ComputePublicationFingerprint(definition, semantic, assets), images));
        }
        if (load.Package.Kind == ContentPackageKind.Mod && CountFor(load) + entries.Count > ContentLimits.MaxVoxelsPerMod)
            load.Diagnostics.Warn(ContentDiagnosticCodes.ModVoxelLimitExceeded,
                "A Mod package exceeds the 1000 voxel definition limit.");
        return entries;
    }

    /// <summary>检查已隔离候选的体素总数；首次成功发布至少要有一个体素，后续允许清空。</summary>
    public void CheckGlobalLimits(DiagnosticBag diagnostics, bool initial)
    {
        if (Entries.Count > ContentLimits.MaxVoxels)
            diagnostics.Error(ContentDiagnosticCodes.GlobalVoxelLimitExceeded, "Content exceeds the 10000 voxel definition limit.");
        if (initial && Entries.Count == 0)
            diagnostics.Error(ContentDiagnosticCodes.EmptyInitialCatalog, "The initial content must contain at least one voxel definition.");
    }

    /// <summary>读取四种绑定引用的去重 PNG，检查解码及尺寸；失败使整个所属包无效。</summary>
    private static bool TryLoadImages(IContentSource source, ContentPackageLoad load, VoxelPresentation presentation,
        out Dictionary<string, DecodedImage> images, out Dictionary<string, string> assets)
    {
        images = new(StringComparer.Ordinal);
        assets = new(StringComparer.Ordinal);
        var paths = new[] { presentation.IconPath, presentation.TopPath, presentation.SidePath, presentation.BottomPath };
        var valid = true;
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            if (!TryReadImage(source, load, path, out var image, out var revision))
            {
                valid = false;
                continue;
            }
            images.Add(path, image!);
            assets.Add(path, revision!);
        }
        if (!valid)
            return false;
        var bag = load.Diagnostics;
        var icon = images[presentation.IconPath];
        if (!IsSquare(icon))
        {
            bag.Warn(ContentDiagnosticCodes.ImageInvalid, "Icons must be square.", presentation.IconPath);
            valid = false;
        }
        var faces = new[] { images[presentation.TopPath], images[presentation.SidePath], images[presentation.BottomPath] };
        if (faces.Any(image => !IsSquare(image) || !IsPowerOfTwo(image.Width)) ||
            faces.Select(image => image.Width).Distinct().Count() != 1)
        {
            bag.Warn(ContentDiagnosticCodes.ImageInvalid,
                "Voxel face images must be square, power-of-two, and have equal edge lengths.");
            valid = false;
        }
        return valid;
    }

    /// <summary>解码已读取的同一份图片字节，拒绝链接、超限和失败结果，并检查单边尺寸。</summary>
    private static bool TryReadImage(IContentSource source, ContentPackageLoad load, string path,
        out DecodedImage? image, out string? revision)
    {
        image = null;
        revision = null;
        var bag = load.Diagnostics;
        var package = load.Package;
        var bytes = source.ReadFile(package, path, ContentLimits.PngMaxBytes);
        if (bytes.UnsafeLink)
            bag.Add(package.IsRequired ? ContentDiagnosticSeverity.Error : ContentDiagnosticSeverity.Warning,
                ContentDiagnosticCodes.UnsafeLinkedPath, "Linked images are not allowed.", path);
        else if (bytes.TooLarge || bytes.Bytes?.Length > ContentLimits.PngMaxBytes)
            bag.Warn(ContentDiagnosticCodes.ImageTooLarge, "PNG exceeds the 4 MiB limit.", path);
        else if (bytes.Bytes is null)
            bag.Warn(ContentDiagnosticCodes.ImageInvalid, "PNG could not be read.", path);
        else
        {
            var decoded = source.DecodePng(package, path, bytes.Bytes);
            if (decoded.UnsafeLink)
                bag.Add(package.IsRequired ? ContentDiagnosticSeverity.Error : ContentDiagnosticSeverity.Warning,
                    ContentDiagnosticCodes.UnsafeLinkedPath, "Linked images are not allowed.", path);
            else if (decoded.TooLarge)
                bag.Warn(ContentDiagnosticCodes.ImageTooLarge, "PNG exceeds the 4 MiB limit.", path);
            else if (decoded.Failed || decoded.Image is null)
                bag.Warn(ContentDiagnosticCodes.ImageInvalid, "PNG could not be decoded.", path);
            else if (decoded.Image.Width <= 0 || decoded.Image.Height <= 0 ||
                     decoded.Image.Width > ContentLimits.ImageMaxDimension || decoded.Image.Height > ContentLimits.ImageMaxDimension)
                bag.Warn(ContentDiagnosticCodes.ImageTooLarge, "PNG dimensions are outside 1..4096.", path);
            else
            {
                image = decoded.Image;
                revision = VoxelRevisions.ComputeAssetRevision(bytes.Bytes);
                return true;
            }
        }
        return false;
    }

    private static bool IsSquare(DecodedImage image) => image.Width == image.Height;
    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
}
