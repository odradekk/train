// 属于蓝图内容模块。
// 解析蓝图布局，按本轮体素候选校验引用并计算设计属性。
using Train.Content.Voxels;

namespace Train.Content.Composites;

/// <summary>表示完成本地解析的蓝图布局、来源和语义修订；不含尚未计算的设计属性或发布指纹。</summary>
internal sealed record CompositeCandidate(
    CompositeDefinition Definition, ContentOrigin Origin, string? FileDisplayName, string SemanticRevision);

/// <summary>一轮加载中的 schema 2 蓝图格式；引用解析始终使用同轮体素索引。</summary>
internal sealed class CompositeContentFormat : ContentFormat<CompositeDefinitionId, CompositeCandidate>
{
    private readonly Dictionary<CompositeDefinitionId, CompositeContentEntry> _entries = new();

    public IReadOnlyDictionary<CompositeDefinitionId, CompositeContentEntry> Entries => _entries;
    public override string FileSuffix => ".composites.json";
    public override string LogKind => "composites";
    protected override CompositeDefinitionId Identity(CompositeCandidate entry) => entry.Definition.Id;
    public override bool HasChanges(ContentSnapshot previous) =>
        HasChanges(Entries, previous.Blueprints, static entry => entry.PublicationFingerprint);

    /// <summary>严格解析布局；引用及派生属性留给合并后的候选检查。</summary>
    protected override IReadOnlyList<CompositeCandidate> Parse(
        IContentSource source, ContentPackageLoad load, string file, byte[] bytes)
    {
        var document = CompositesJsonParser.Parse(bytes, load.Package.Namespace, file, load.Diagnostics);
        if (document is null)
            return Array.Empty<CompositeCandidate>();
        return document.Composites.Select(definition => new CompositeCandidate(
            definition, new ContentOrigin(load.Package.Namespace, file), document.DisplayName,
            CompositeRevisions.ComputeSemanticRevision(definition))).ToArray();
    }

    /// <summary>按已合并的体素索引校验候选布局并计算属性，只为成功候选建立完整蓝图条目。</summary>
    /// <remarks>效果：重建本轮 Entries；失败使实际所属包无效，已发布快照不受影响。</remarks>
    public void ResolveReferences(IReadOnlyDictionary<VoxelDefinitionId, VoxelContentEntry> voxels)
    {
        _entries.Clear();
        foreach (var (id, candidate) in Candidates)
        {
            var owner = Owners[id];
            if (owner.Diagnostics.Invalid || CalculateAttributes(candidate, voxels, owner) is not { } attributes)
                continue;
            _entries.Add(id, new CompositeContentEntry(candidate.Definition, candidate.Origin, candidate.FileDisplayName,
                candidate.SemanticRevision, CompositeRevisions.ComputePublicationFingerprint(candidate.Definition,
                    candidate.FileDisplayName, candidate.SemanticRevision, attributes), attributes));
        }
    }

    /// <summary>逐格校验引用和朝向并汇总核心定义；失败返回 null，原因记录到实际所属包。</summary>
    private static DesignAttributes? CalculateAttributes(CompositeCandidate composite,
        IReadOnlyDictionary<VoxelDefinitionId, VoxelContentEntry> voxels, ContentPackageLoad owner)
    {
        var bag = owner.Diagnostics;
        bag.Source = composite.Origin.SourceFile;
        var resolved = new List<ICoreVoxelDefinition>();
        var valid = true;
        foreach (var voxel in composite.Definition.Voxels)
        {
            if (!voxels.TryGetValue(voxel.DefinitionId, out var entry))
            {
                bag.Reject(ContentDiagnosticCodes.UnresolvedVoxelReference,
                    "A blueprint references a voxel missing from the candidate content.", voxel.DefinitionId.Value);
                valid = false;
                continue;
            }
            var allowed = entry.Definition.AllowedOrientations switch
            {
                VoxelOrientationConstraint.None => voxel.Orientation == VoxelOrientation.Identity,
                VoxelOrientationConstraint.FourWay => VoxelOrientation.FourWay.Contains(voxel.Orientation),
                VoxelOrientationConstraint.All24 => VoxelOrientation.All.Contains(voxel.Orientation),
                _ => false
            };
            if (!allowed)
            {
                bag.Reject(ContentDiagnosticCodes.OrientationInvalid,
                    "A blueprint voxel uses an orientation disallowed by its definition.", voxel.DefinitionId.Value);
                valid = false;
            }
            resolved.Add(entry.Definition);
        }
        if (!valid)
            return null;
        try
        {
            return DesignAttributeCalculator.Calculate(resolved);
        }
        catch (OverflowException)
        {
            bag.Reject(ContentDiagnosticCodes.LayoutInvalid,
                "Blueprint design attributes exceed the representable range.", composite.Definition.Id.Value);
            return null;
        }
    }
}
