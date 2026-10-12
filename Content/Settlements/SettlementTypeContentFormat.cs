using Train.Content.Voxels;

namespace Train.Content.Settlements;

internal sealed class SettlementTypeContentFormat : ContentFormat<SettlementTypeDefinitionId, SettlementTypeContentEntry>
{
    public IReadOnlyDictionary<SettlementTypeDefinitionId, SettlementTypeContentEntry> Entries => Candidates;
    public override string FileSuffix => ".settlement-types.json";
    public override string LogKind => "settlement-types";
    protected override SettlementTypeDefinitionId Identity(SettlementTypeContentEntry entry) => entry.Definition.Id;
    public override bool HasChanges(ContentSnapshot previous) =>
        HasChanges(Entries, previous.SettlementTypes, static entry => entry.PublicationFingerprint);

    protected override IReadOnlyList<SettlementTypeContentEntry> Parse(
        IContentSource source, ContentPackageLoad load, string file, byte[] bytes)
    {
        var definitions = SettlementTypesJsonParser.Parse(bytes, load.Package.Namespace, file, load.Diagnostics);
        if (definitions is null)
            return Array.Empty<SettlementTypeContentEntry>();
        return definitions.Select(definition =>
        {
            var semantic = SettlementTypeRevisions.ComputeSemanticRevision(definition);
            return new SettlementTypeContentEntry(definition, new ContentOrigin(load.Package.Namespace, file), semantic,
                SettlementTypeRevisions.ComputePublicationFingerprint(definition, semantic));
        }).ToArray();
    }

    public void ResolveReferences(IReadOnlyDictionary<VoxelDefinitionId, VoxelContentEntry> voxels)
    {
        foreach (var (id, entry) in Entries)
        {
            var owner = Owners[id];
            if (!owner.Diagnostics.Invalid)
                VoxelContentReferences.ValidateGoods(entry.Definition.Supply.Keys.Concat(entry.Definition.Demand.Keys),
                    voxels, owner, entry.Origin.SourceFile);
        }
    }
}
