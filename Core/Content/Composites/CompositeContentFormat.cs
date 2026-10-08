// 属于蓝图内容模块。
// 解析蓝图布局，按本轮体素候选校验引用并计算设计属性。
using Train.Core.Content.Voxels;

namespace Train.Core.Content.Composites;

/// <summary>一轮加载中的 schema 2 蓝图格式；引用解析始终使用同轮体素索引。</summary>
internal sealed class CompositeContentFormat(IReadOnlyDictionary<VoxelDefinitionId, VoxelContentEntry> voxels)
    : ContentFormat<CompositeDefinitionId, CompositeContentEntry>
{
    public override string FileSuffix => ".composites.json";
    public override string LogKind => "composites";
    public override bool Accepts(ContentPackage package, string file) =>
        base.Accepts(package, file) && (package.Kind != ContentPackageKind.PlayerBlueprint ||
                                      file == package.Namespace + FileSuffix);
    protected override CompositeDefinitionId Identity(CompositeContentEntry entry) => entry.Definition.Id;
    protected override string Fingerprint(CompositeContentEntry entry) => entry.PublicationFingerprint;
    public override bool HasChanges(ContentSnapshot previous) => HasChanges(previous.Blueprints);

    /// <summary>严格解析布局；引用及派生属性留给合并后的候选检查。</summary>
    protected override IReadOnlyList<CompositeContentEntry> Parse(
        IContentSource source, ContentPackageLoad load, string file, byte[] bytes)
    {
        var document = CompositesJsonParser.Parse(bytes, load.Package.Namespace, file, load.Diagnostics);
        if (document is null)
            return Array.Empty<CompositeContentEntry>();
        return document.Composites.Select(definition =>
        {
            var semantic = CompositeRevisions.ComputeSemanticRevision(definition);
            return new CompositeContentEntry(definition, new ContentOrigin(load.Package.Namespace, file),
                document.DisplayName, semantic, CompositeRevisions.ComputePublicationFingerprint(
                    definition, document.DisplayName, semantic, DesignAttributes.Empty), DesignAttributes.Empty);
        }).ToArray();
    }

    public override void ResolveReferences()
    {
        foreach (var (id, entry) in Entries.ToArray())
        {
            var owner = Owners[id];
            if (owner.Diagnostics.Invalid || !TryCalculate(entry, owner, out var attributes))
                continue;
            Entries[id] = new CompositeContentEntry(entry.Definition, entry.Origin, entry.FileDisplayName,
                entry.SemanticRevision, CompositeRevisions.ComputePublicationFingerprint(entry.Definition,
                    entry.FileDisplayName, entry.SemanticRevision, attributes), attributes);
        }
    }

    /// <summary>逐格解析引用和允许朝向，再汇总核心定义；任何失败记录到实际所属包。</summary>
    private bool TryCalculate(CompositeContentEntry composite, ContentPackageLoad owner, out DesignAttributes attributes)
    {
        attributes = DesignAttributes.Empty;
        var bag = owner.Diagnostics;
        bag.Source = composite.Origin.SourceFile;
        var severity = owner.Package.IsRequired ? ContentDiagnosticSeverity.Error : ContentDiagnosticSeverity.Warning;
        var resolved = new List<ICoreVoxelDefinition>();
        var valid = true;
        foreach (var voxel in composite.Definition.Voxels)
        {
            if (!voxels.TryGetValue(voxel.DefinitionId, out var entry))
            {
                bag.Add(severity, ContentDiagnosticCodes.UnresolvedVoxelReference,
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
                bag.Add(severity, ContentDiagnosticCodes.OrientationInvalid,
                    "A blueprint voxel uses an orientation disallowed by its definition.", voxel.DefinitionId.Value);
                valid = false;
            }
            resolved.Add(entry.Definition);
        }
        if (!valid)
            return false;
        try
        {
            attributes = DesignAttributeCalculator.Calculate(resolved);
            return true;
        }
        catch (OverflowException)
        {
            bag.Add(severity, ContentDiagnosticCodes.LayoutInvalid,
                "Blueprint design attributes exceed the representable range.", composite.Definition.Id.Value);
            return false;
        }
    }
}
