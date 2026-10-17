using System.Collections.Frozen;
using System.Collections.Immutable;
using Train.Content.Designs;
using Train.Content.Persistence;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Loading;

internal sealed partial class ContentView
{
    private ContentView(ContentView basis)
    {
        _voxels = basis._voxels; _assets = basis._assets; _blueprints = basis._blueprints;
        _designs = basis._designs; _layouts = basis._layouts; _world = basis._world;
        Core = basis.Core; Packages = basis.Packages; Voxels = basis.Voxels; Blueprints = basis.Blueprints;
        GenerationProfiles = basis.GenerationProfiles; Generation = basis.Generation;
        PersistentRevision = basis.PersistentRevision; SavedPackages = basis.SavedPackages;
        SavedDesigns = basis.SavedDesigns; AssemblyDisplays = basis.AssemblyDisplays; Source = basis.Source;
        LocalDrafts = basis.LocalDrafts; UnreadableBlueprints = basis.UnreadableBlueprints;
        UserLibraryFingerprint = basis.UserLibraryFingerprint;
    }

    private ContentView(ContentView basis, ImmutableArray<C.SavedContentDraft> drafts) : this(basis)
    {
        LocalDrafts = drafts.OrderBy(d => d.Id.Value).ToImmutableArray();
        PersistentRevision = new(checked(basis.PersistentRevision.Value + 1));
    }

    private ContentView(ContentView basis, C.BlueprintAddress address, C.BlueprintRecord? record) : this(basis)
    {
        var records = _blueprints.ToDictionary();
        records.Remove(address);
        if (record is not null) records.Add(address, record);
        _blueprints = records.ToFrozenDictionary();
        UnreadableBlueprints = UnreadableBlueprints.Where(b => b.Address != address).ToImmutableArray();
        Blueprints = Summaries(records.Values, UnreadableBlueprints);
        if (address.Library == C.BlueprintLibrary.User)
        {
            Source = Source?.WithUserBlueprint(address.Id, record);
            if (Source?.Installation.UserLibrary is { } root)
                UserLibraryFingerprint = UserLibraryLoader.Fingerprint(root, Source);
        }
        if (record is not null)
        {
            var designs = _designs.ToDictionary();
            if (record.Evaluation is C.DesignEvaluation.Complete complete) designs[complete.Design] = complete;
            (SavedDesigns, AssemblyDisplays) = Introduce(record.Definition, designs);
            _designs = designs.ToFrozenDictionary();
        }
        Generation = new(checked(basis.Generation.Value + 1));
        if (address.Library == C.BlueprintLibrary.Local || AssemblyDisplays.Length != basis.AssemblyDisplays.Length)
            PersistentRevision = new(checked(basis.PersistentRevision.Value + 1));
    }

    private ContentView(ContentView basis, C.DesignEvaluation.Complete target) : this(basis)
    {
        var designs = _designs.ToDictionary();
        designs[target.Design] = target;
        var saved = SavedDesigns;
        if (!SavedDesigns.Any(d => d.Hash == target.Design))
            saved = saved.Add(new(target.Design, target.Definition, null));
        SavedDesigns = saved;
        (SavedDesigns, AssemblyDisplays) = Introduce(target.Definition, designs);
        _designs = designs.ToFrozenDictionary();
        Generation = new(checked(basis.Generation.Value + 1));
        PersistentRevision = new(checked(basis.PersistentRevision.Value + 1));
    }

    private ContentView(ContentView basis, UserLibraryLoad library, ContentSourceSnapshot? source) : this(basis)
    {
        _blueprints = basis._blueprints.Values.Where(b => b.Address.Library != C.BlueprintLibrary.User)
            .Concat(library.Records).ToFrozenDictionary(b => b.Address);
        UnreadableBlueprints = library.Summaries;
        Blueprints = Summaries(_blueprints.Values, UnreadableBlueprints);
        UserLibraryFingerprint = library.Fingerprint;
        Source = source;
        var designs = _designs.ToDictionary();
        foreach (var record in library.Records)
            if (record.Evaluation is C.DesignEvaluation.Complete complete) designs[complete.Design] = complete;
        foreach (var record in library.Records.OrderBy(r => r.Address.Id.Value, StringComparer.Ordinal))
            if (record.Evaluation is C.DesignEvaluation.Complete { IsValid: true })
                (SavedDesigns, AssemblyDisplays) = Introduce(record.Definition, designs);
        _designs = designs.ToFrozenDictionary();
    }

    internal ContentView WithDrafts(ImmutableArray<C.SavedContentDraft> drafts) => new(this, drafts);
    internal ContentView WithBlueprint(C.BlueprintAddress address, C.BlueprintRecord? record) => new(this, address, record);
    internal ContentView WithTarget(C.DesignEvaluation.Complete target) =>
        SavedDesigns.Any(d => d.Hash == target.Design) || Core.Designs.Any(d => d.Design == target.Design) ? this : new(this, target);
    internal ContentView WithUserLibrary(UserLibraryLoad library, ContentSourceSnapshot? source) => new(this, library, source);

    private (ImmutableArray<CapturedDesign>, ImmutableArray<CapturedAssembly>) Introduce(C.DesignDraft draft,
        Dictionary<K.DesignHash, C.DesignEvaluation> designs)
    {
        var saved = SavedDesigns.ToDictionary(d => d.Hash);
        var displays = AssemblyDisplays.ToDictionary(d => d.Composition);
        foreach (var assembly in DesignTraversal.Assemblies(draft))
        {
            var complete = (C.DesignEvaluation.Complete)DesignEvaluator.Evaluate(this, assembly);
            designs[complete.Design] = complete;
            if (displays.TryAdd(complete.Composition, new(complete.Composition, complete.Design)))
                saved.TryAdd(complete.Design, new(complete.Design, complete.Definition, null));
        }
        return (saved.Values.OrderBy(d => d.Hash.Value, StringComparer.Ordinal).ToImmutableArray(),
            displays.Values.OrderBy(d => d.Composition.Value, StringComparer.Ordinal).ToImmutableArray());
    }

    private static ImmutableArray<C.BlueprintSummary> Summaries(IEnumerable<C.BlueprintRecord> records,
        ImmutableArray<C.BlueprintSummary> unreadable) => records.Select(PlayerBlueprint.Summary).Concat(unreadable)
        .OrderBy(b => b.Address.Library).ThenBy(b => b.Address.Id.Value, StringComparer.Ordinal).ToImmutableArray();

    internal ImmutableArray<C.PackageMetadata> UsedPackages(C.DesignDraft draft) => DesignTraversal.References(draft)
        .Select(r => DefinitionId(r.Reference).Split(':')[0]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
        .Select(id => Packages.Select(p => p.Metadata).OfType<C.PackageMetadata>().Concat(SavedPackages).FirstOrDefault(p => p.Id.Value == id))
        .OfType<C.PackageMetadata>().ToImmutableArray();

    internal ImmutableArray<C.MissingContentReference> AuthoringMissing(string stage = "restore") =>
        MissingFrom(_blueprints.Values.Where(b => b.Address.Library != C.BlueprintLibrary.Packages)
            .SelectMany(b => DesignTraversal.References(b.Definition, $"Blueprints[{b.Address.Library},{b.Address.Id.Value}]"))
            .Concat(LocalDrafts.SelectMany(d => DesignTraversal.References(((C.DraftPayload.Design)d.Payload).Value, $"Drafts[{d.Id.Value:D}]"))), stage);

    internal ImmutableArray<C.MissingContentReference> InstanceDraftMissing(string stage = "reload") =>
        MissingFrom(LocalDrafts.Where(d => d.Target is C.DraftTarget.Instance).SelectMany(d =>
            DesignTraversal.References(((C.DraftPayload.Design)d.Payload).Value, $"Drafts[{d.Id.Value:D}]")), stage);

    private ImmutableArray<C.MissingContentReference> MissingFrom(IEnumerable<(K.CoreDefinitionReference Reference, string Field)> references,
        string stage) => references.Where(r => !HasDefinition(r.Reference)).Distinct()
        .OrderBy(r => DefinitionId(r.Reference), StringComparer.Ordinal).ThenBy(r => r.Field, StringComparer.Ordinal)
        .Select(r =>
        {
            var package = new C.PackageId(DefinitionId(r.Reference).Split(':')[0]);
            var subject = new C.ContentReference.Core(r.Reference);
            return new C.MissingContentReference(subject, package, r.Field,
                [new("reference_missing", C.ContentDiagnosticSeverity.Error, stage, subject, new(package, ""), r.Field, null, "创作引用缺失。")]);
        }).ToImmutableArray();

    private HashSet<K.DesignHash> SeedDesigns() => _world.Templates.SelectMany(t => t.Core.Plots)
        .Select(p => p.PrebuiltDesign).OfType<K.DesignHash>()
        .Concat(_world.Templates.SelectMany(t => t.Core.FoundingBuildings).Select(b => b.Design))
        .Concat(_world.Profiles.SelectMany(p => p.Start.Trains).SelectMany(t => t.Vehicles))
        .Concat(_world.Profiles.SelectMany(p => p.Configuration.NpcInitialization.ForSaleVehicleCandidates))
        .Concat(_world.Profiles.Select(p => p.Configuration.StartConstraints.RequiredAffordableCommissionDesign))
        .Concat(_world.Templates.SelectMany(t => t.Core.SpecialInitialInventory).Select(q => q.Good).OfType<K.GoodId.Assembly>()
            .SelectMany(g => AssemblyDisplays.Where(d => d.Composition == g.Composition).Select(d => d.Design)))
        .Concat(_world.Profiles.SelectMany(p => p.Start.Trains).SelectMany(t => t.Inventory).Select(q => q.Good).OfType<K.GoodId.Assembly>()
            .SelectMany(g => AssemblyDisplays.Where(d => d.Composition == g.Composition).Select(d => d.Design))).ToHashSet();

    private static ImmutableArray<K.GoodDefinitionProjection> SeedAssemblyGoods(Dictionary<K.DesignHash, C.DesignEvaluation> designs,
        HashSet<K.DesignHash> required) => designs.Where(d => required.Contains(d.Key)).Select(d => d.Value)
        .OfType<C.DesignEvaluation.Complete>().Where(d => d.Core is not null).SelectMany(d => d.Core!.AssemblyGoods)
        .DistinctBy(g => g.Good).OrderBy(g => DesignEvaluator.GoodKey(g.Good), StringComparer.Ordinal).ToImmutableArray();
}
