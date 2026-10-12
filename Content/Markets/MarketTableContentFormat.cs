using Train.Content.Voxels;

namespace Train.Content.Markets;

internal sealed class MarketTableContentFormat : ContentFormat<MarketTableDefinitionId, MarketTableContentEntry>
{
    public IReadOnlyDictionary<MarketTableDefinitionId, MarketTableContentEntry> Entries => Candidates;
    public override string FileSuffix => ".market-tables.json";
    public override string LogKind => "market-tables";
    protected override MarketTableDefinitionId Identity(MarketTableContentEntry entry) => entry.Definition.Id;
    public override bool HasChanges(ContentSnapshot previous) =>
        HasChanges(Entries, previous.MarketTables, static entry => entry.PublicationFingerprint);

    protected override IReadOnlyList<MarketTableContentEntry> Parse(
        IContentSource source, ContentPackageLoad load, string file, byte[] bytes)
    {
        var definitions = MarketTablesJsonParser.Parse(bytes, load.Package.Namespace, file, load.Diagnostics);
        return definitions is null ? Array.Empty<MarketTableContentEntry>() : definitions.Select(definition =>
            new MarketTableContentEntry(definition, new ContentOrigin(load.Package.Namespace, file),
                MarketTableRevisions.ComputeSemanticRevision(definition))).ToArray();
    }

    public void ResolveReferences(IReadOnlyDictionary<VoxelDefinitionId, VoxelContentEntry> voxels)
    {
        foreach (var (id, entry) in Entries)
        {
            var owner = Owners[id];
            if (!owner.Diagnostics.Invalid)
                VoxelContentReferences.ValidateGoods(entry.Definition.Goods.Keys, voxels, owner, entry.Origin.SourceFile);
        }
    }
}
