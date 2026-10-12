using Train.Content.Voxels;

namespace Train.Content.Composites;

internal sealed record CompositeCandidate(
    CompositeDefinition Definition, ContentOrigin Origin, string? FileDisplayName, string SemanticRevision);

internal sealed class CompositeContentFormat : ContentFormat<CompositeDefinitionId, CompositeCandidate>
{
    private readonly Dictionary<CompositeDefinitionId, CompositeContentEntry> _entries = new();

    public IReadOnlyDictionary<CompositeDefinitionId, CompositeContentEntry> Entries => _entries;
    public override string FileSuffix => ".composites.json";
    public override string LogKind => "composites";
    protected override CompositeDefinitionId Identity(CompositeCandidate entry) => entry.Definition.Id;
    public override bool HasChanges(ContentSnapshot previous) =>
        HasChanges(Entries, previous.Blueprints, static entry => entry.PublicationFingerprint);

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
