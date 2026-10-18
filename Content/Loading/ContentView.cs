using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using Train.Content.Designs;
using Train.Content.Persistence;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Loading;

internal sealed record LoadedVoxel(ParsedVoxel Definition, C.VoxelAppearanceProjection Appearance, C.VoxelDisplayProjection Display);

internal sealed partial class ContentView : C.IContentView
{
    private readonly FrozenDictionary<K.VoxelDefinitionId, LoadedVoxel> _voxels;
    private readonly FrozenDictionary<C.AssetRevision, C.ContentAsset> _assets;
    private readonly FrozenDictionary<C.BlueprintAddress, C.BlueprintRecord> _blueprints;
    private readonly FrozenDictionary<K.DesignHash, C.DesignEvaluation> _designs;
    private readonly FrozenDictionary<K.SettlementId, C.SettlementLayoutProjection> _layouts;
    private readonly WorldDefinitions _world;
    public C.ContentGeneration Generation { get; } = new(1);
    public C.ContentRevision PersistentRevision { get; }
    public K.CoreContentProjection Core { get; }
    public ImmutableArray<C.PackageStatus> Packages { get; }
    public ImmutableArray<C.VoxelCatalogEntry> Voxels { get; }
    public ImmutableArray<C.BlueprintSummary> Blueprints { get; }
    public ImmutableArray<C.SavedContentDraft> LocalDrafts { get; } = [];
    internal ImmutableArray<C.BlueprintSummary> UnreadableBlueprints { get; } = [];
    internal string UserLibraryFingerprint { get; } = "";
    internal ImmutableArray<C.BlueprintRecord> LocalBlueprints => _blueprints.Values.Where(b => b.Address.Library == C.BlueprintLibrary.Local)
        .OrderBy(b => b.Address.Id.Value, StringComparer.Ordinal).ToImmutableArray();
    public ImmutableArray<C.GenerationProfileSummary> GenerationProfiles { get; }
    internal ImmutableArray<C.PackageMetadata> SavedPackages { get; } = [];
    internal ImmutableArray<CapturedDesign> SavedDesigns { get; } = [];
    internal ImmutableArray<CapturedAssembly> AssemblyDisplays { get; } = [];
    internal ContentSourceSnapshot? Source { get; }

    internal ContentView(ImmutableArray<LoadedVoxel> voxels, ImmutableArray<C.PackageStatus> packages,
        ImmutableArray<C.ContentAsset> assets, ImmutableArray<C.BlueprintRecord> blueprints, WorldDefinitions? world = null,
        ContentSourceSnapshot? source = null)
    {
        _world = world ?? WorldDefinitions.Empty;
        Source = source;
        _layouts = FrozenDictionary<K.SettlementId, C.SettlementLayoutProjection>.Empty;
        PersistentRevision = new(0);
        _voxels = voxels.ToFrozenDictionary(voxel => voxel.Definition.Definition);
        _assets = assets.ToFrozenDictionary(asset => asset.Revision);
        Packages = packages;
        Voxels = voxels.Select(voxel => new C.VoxelCatalogEntry(voxel.Definition.Definition, voxel.Definition.Good)).ToImmutableArray();
        var compositeGoods = blueprints.Where(b => b.Definition.Specification is C.DesignSpecification.FunctionalComposite)
            .Select(CompositeGood).OrderBy(g => DesignEvaluator.GoodKey(g.Good), StringComparer.Ordinal).ToImmutableArray();
        var creatures = blueprints.Where(blueprint => blueprint.Definition.Specification is C.DesignSpecification.Creature { Behavior: not null })
            .Select(CreatureProjection).OrderBy(creature => creature.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        var identity = WorldSemantics.Core(voxels, _world, blueprints, compositeGoods, creatures);
        var records = blueprints.OrderBy(blueprint => blueprint.Address.Id.Value, StringComparer.Ordinal)
            .Select(blueprint => blueprint.Evaluation is C.DesignEvaluation.Complete { Core: { } core } complete
                ? blueprint with { Evaluation = complete with { Core = core with { InputIdentity = new(identity) } } } : blueprint).ToImmutableArray();
        _blueprints = records.ToFrozenDictionary(blueprint => blueprint.Address);
        var designs = records.Select(blueprint => (C.DesignEvaluation.Complete)blueprint.Evaluation)
            .DistinctBy(evaluation => evaluation.Design).OrderBy(evaluation => evaluation.Design.Value, StringComparer.Ordinal).ToImmutableArray();
        _designs = designs.ToFrozenDictionary(evaluation => evaluation.Design, evaluation => (C.DesignEvaluation)evaluation);
        Blueprints = records.Select(blueprint => new C.BlueprintSummary(blueprint.Address, blueprint.Definition.Display,
            ((C.DesignEvaluation.Complete)blueprint.Evaluation).Design,
            ((C.DesignEvaluation.Complete)blueprint.Evaluation).IsValid,
            ((C.DesignEvaluation.Complete)blueprint.Evaluation).Diagnostics)).ToImmutableArray();
        Core = new(Identity: new(identity), Goods: voxels.Where(voxel => voxel.Definition.Good is not null)
            .Select(voxel => voxel.Definition.Good!).Concat(compositeGoods).ToImmutableArray(), Designs: designs.Where(design => design.Core is not null)
                .Select(design => design.Core!.Design).ToImmutableArray(), DesignGoods: [], Processes: _world.Processes.Select(p => p.Projection).ToImmutableArray(),
            PopulationRules: _world.Rules.OfType<ParsedRule.Population>().Select(r => r.Projection).ToImmutableArray(),
            MarketRules: _world.Rules.OfType<ParsedRule.Market>().Select(r => r.Projection).ToImmutableArray(),
            ConstructionRules: _world.Rules.OfType<ParsedRule.Construction>().Select(r => r.Projection).ToImmutableArray(),
            RailwayRules: _world.Rules.OfType<ParsedRule.Railway>().Select(r => r.Projection).ToImmutableArray(),
            CombatRules: _world.Rules.OfType<ParsedRule.Combat>().Select(r => r.Projection).ToImmutableArray(),
            SettlementTypes: _world.Types.Select(t => t.Source.Core).ToImmutableArray(),
            SettlementTemplates: _world.Templates.Select(t => t.Core).ToImmutableArray(), Creatures: creatures,
            LairTypes: _world.Lairs.Select(lair => lair.Core).ToImmutableArray(), MissingReferences: []);
        GenerationProfiles = _world.Profiles.Select(p => new C.GenerationProfileSummary(p.Id, p.Display)).ToImmutableArray();
        var allDesigns = _designs.ToDictionary();
        var displays = new Dictionary<K.CompositionHash, CapturedAssembly>();
        foreach (var record in records.OrderBy(r => r.Address.Library).ThenBy(r => r.Address.Id.Value, StringComparer.Ordinal))
            foreach (var assembly in DesignTraversal.Assemblies(record.Definition))
            {
                var evaluated = (C.DesignEvaluation.Complete)DesignEvaluator.Evaluate(this, assembly);
                allDesigns[evaluated.Design] = evaluated;
                displays.TryAdd(evaluated.Composition, new(evaluated.Composition, evaluated.Design));
            }
        _designs = allDesigns.ToFrozenDictionary();
        AssemblyDisplays = displays.Values.OrderBy(d => d.Composition.Value, StringComparer.Ordinal).ToImmutableArray();
        var seeds = SeedDesigns();
        Core = Core with { Designs = DesignProjections(allDesigns.Where(d => seeds.Contains(d.Key)).ToDictionary()),
            DesignGoods = SeedAssemblyGoods(allDesigns, seeds) };
    }

    private ContentView(ContentView basis, FrozenDictionary<K.SettlementId, C.SettlementLayoutProjection> layouts)
    {
        _voxels = basis._voxels; _assets = basis._assets; _blueprints = basis._blueprints; _designs = basis._designs;
        _world = basis._world; _layouts = layouts;
        AssemblyDisplays = basis.AssemblyDisplays;
        Generation = basis.Generation; SavedPackages = basis.SavedPackages; SavedDesigns = basis.SavedDesigns; Source = basis.Source;
        Core = basis.Core; Packages = basis.Packages; Voxels = basis.Voxels; Blueprints = basis.Blueprints; GenerationProfiles = basis.GenerationProfiles;
        PersistentRevision = new(checked(basis.PersistentRevision.Value + 1));
        LocalDrafts = basis.LocalDrafts; UnreadableBlueprints = basis.UnreadableBlueprints; UserLibraryFingerprint = basis.UserLibraryFingerprint;
    }

    private ContentView(ContentView basis, ContentCapture capture, K.CoreReferenceManifest references, C.ContentGeneration? generation, ContentView? reloadBasis)
    {
        capture.Validate(references);
        _voxels = basis._voxels; _assets = basis._assets; _world = basis._world;
        Generation = generation ?? basis.Generation; PersistentRevision = capture.Revision;
        SavedPackages = capture.ReferencedPackages; SavedDesigns = capture.Records; Source = basis.Source;
        Packages = basis.Packages; Voxels = basis.Voxels; GenerationProfiles = basis.GenerationProfiles;
        LocalDrafts = capture.LocalDrafts.OrderBy(d => d.Id.Value).ToImmutableArray();
        UnreadableBlueprints = basis.UnreadableBlueprints; UserLibraryFingerprint = basis.UserLibraryFingerprint;
        _blueprints = basis._blueprints.Values.Where(b => b.Address.Library != C.BlueprintLibrary.Local)
            .Concat(capture.LocalBlueprints.Select(b => new C.BlueprintRecord(b.Address, b.Definition, b.UsedPackages,
                PlayerBlueprint.Evaluate(basis, b.Definition))))
            .ToFrozenDictionary(b => b.Address);
        Blueprints = Summaries(_blueprints.Values, UnreadableBlueprints);
        var designs = basis._designs.ToDictionary();
        foreach (var blueprint in _blueprints.Values.Where(b => b.Address.Library == C.BlueprintLibrary.Local))
            if (blueprint.Evaluation is C.DesignEvaluation.Complete complete) designs[complete.Design] = complete;
        foreach (var record in capture.Records)
        {
            designs[record.Hash] = DesignEvaluator.Evaluate(basis, record.Definition, allowMissing: true, compositeIdentity: record.Composite);
            if (designs[record.Hash] is C.DesignEvaluation.Failed)
                ContentCapture.Inconsistent("/designs", "内嵌设计无法形成计算结果。");
        }
        _designs = designs.ToFrozenDictionary();
        var displays = capture.AssemblyDisplays.ToDictionary(d => d.Composition);
        foreach (var display in basis.AssemblyDisplays) displays.TryAdd(display.Composition, display);
        AssemblyDisplays = displays.Values.OrderBy(d => d.Composition.Value, StringComparer.Ordinal).ToImmutableArray();
        if (AssemblyDisplays.Length != capture.AssemblyDisplays.Length)
            PersistentRevision = new(checked(capture.Revision.Value + 1));
        _layouts = capture.Copies.ToFrozenDictionary(t => t.Owner, t =>
        {
            var scene = _designs[t.SceneModel] switch
            {
                C.DesignEvaluation.Complete complete => complete.Scene,
                C.DesignEvaluation.Partial partial => partial.Scene,
                _ => throw new InvalidOperationException("模板场景设计无法计算。")
            };
            var definition = capture.Records.Single(d => d.Hash == t.SceneModel).Definition;
            return new C.SettlementLayoutProjection(t.Owner, t.Source, new(t.SceneModel, scene, definition.Display), t.Plots, t.Display);
        });
        var requiredDesigns = references.Designs.Concat(SeedDesigns()).ToHashSet();
        Core = basis.Core with
        {
            Designs = DesignProjections(designs.Where(d => requiredDesigns.Contains(d.Key)).ToDictionary()),
            DesignGoods = SeedAssemblyGoods(designs, requiredDesigns),
            MissingReferences = Missing(references).Select(m => new K.MissingCoreReference(
                ((C.ContentReference.Core)m.Reference).Definition, m.Field)).ToImmutableArray()
        };
        if (generation is not null && (PersistentRevision != capture.Revision ||
            reloadBasis is not null && ChangesFrom(reloadBasis) != C.ContentChange.None))
            Generation = new(checked(generation.Value.Value + 1));
    }

    internal ContentView WithCapture(ContentCapture capture, K.CoreReferenceManifest references, C.ContentGeneration? generation = null, ContentView? reloadBasis = null) =>
        new(this, capture, references, generation, reloadBasis);

    private static ImmutableArray<K.DesignProjection> DesignProjections(Dictionary<K.DesignHash, C.DesignEvaluation> designs) =>
        designs.OrderBy(d => d.Key.Value, StringComparer.Ordinal).Select(d => d.Value switch
        { C.DesignEvaluation.Complete c => c.Core?.Design, C.DesignEvaluation.Partial p => p.CorePlaceholder, _ => null }).OfType<K.DesignProjection>().ToImmutableArray();

    private K.GoodDefinitionProjection CompositeGood(C.BlueprintRecord record)
    {
        var design = ((C.DesignEvaluation.Complete)record.Evaluation).Core!.Design;
        var capability = (C.DesignSpecification.FunctionalComposite)record.Definition.Specification;
        var value = DesignEvaluator.BaseValue(design);
        var revision = ContentHash.FromCanonicalJson(w => JsonSerializer.Serialize(w, new
        {
            id = record.Address.Id.Value, design.MassTons, design.MaxStructureDurability, value,
            occupancy = DesignTraversal.Cells(record.Definition).Select(c => new[] { c.Cell.At.X, c.Cell.At.Y, c.Cell.At.Z }),
            rotation = capability.Rotation is { } rotation ? new
            {
                rotation.Axis, rotation.Pivot, rotation.MinimumDegrees, rotation.MaximumDegrees
            } : null,
            capabilities = DesignTraversal.Behaviors(capability.Behaviors),
            dependencies = DesignTraversal.References(record.Definition).Select(r => ((K.CoreDefinitionReference.Voxel)r.Reference).Id)
                .Distinct().OrderBy(id => id.Value, StringComparer.Ordinal).Select(id => new { id = id.Value, revision = _voxels[id].Definition.Revision.Value }),
            returns = design.BillOfMaterials.Select(q => new { good = DesignEvaluator.GoodKey(q.Good), q.Quantity })
        }));
        return new(new K.GoodId.Composite(new(record.Address.Id.Value)), new(revision), new(value), design.MassTons, false, null);
    }

    internal K.CoreReferenceManifest PersistentReferences => new(
        SavedDesigns.Select(d => d.Hash).ToImmutableArray(),
        _layouts.Values.OrderBy(t => t.Owner.Value).Select(t => new K.OwnedSettlementTemplateReference(t.Owner, t.SourceTemplate,
            t.Plots.Select(p => new K.TemplatePlotReference(p.Number, p.Size)).ToImmutableArray())).ToImmutableArray(),
        Core.MissingReferences.Select(m => new K.RequiredDefinition(m.Reference, null, m.FieldPath, true)).ToImmutableArray(), [Core.Identity]);

    internal C.ContentChange ChangesFrom(ContentView basis)
    {
        var changes = Core.Identity == basis.Core.Identity ? C.ContentChange.None : C.ContentChange.CoreInput;
        if (Presentation() != basis.Presentation()) changes |= C.ContentChange.Presentation;
        if (Library() != basis.Library()) changes |= C.ContentChange.Library;
        return changes;
    }

    private string Library() => ContentCapture.Encode(writer =>
    {
        writer.WriteStartArray();
        writer.WriteStringValue(UserLibraryFingerprint);
        JsonSerializer.Serialize(writer, Blueprints);
        foreach (var record in _blueprints.Values.Where(b => b.Address.Library != C.BlueprintLibrary.Local)
            .OrderBy(b => b.Address.Library).ThenBy(b => b.Address.Id.Value, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("id", record.Address.Id.Value);
            writer.WriteString("library", record.Address.Library.ToString());
            writer.WriteStartObject("definition");
            BlueprintParser.WriteDesign(writer, record.Definition);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    });

    private string Presentation()
    {
        string Composition(K.DesignHash hash) => ((C.DesignEvaluation.Complete)_designs[hash]).Composition.Value;
        return JsonSerializer.Serialize(new
        {
            voxels = _voxels.Values.OrderBy(v => v.Definition.Definition.Value, StringComparer.Ordinal).Select(v => new
            {
                v.Display, v.Appearance.Definition, v.Appearance.AllowedOrientations, v.Appearance.Behaviors,
                collision = JsonSerializer.Serialize(v.Appearance.Collision, v.Appearance.Collision.GetType()),
                presentation = JsonSerializer.Serialize(v.Appearance.Presentation, v.Appearance.Presentation.GetType())
            }),
            terrains = _world.Terrains,
            composites = _blueprints.Values.Where(b => b.Address.Library == C.BlueprintLibrary.Packages &&
                b.Definition.Specification is C.DesignSpecification.FunctionalComposite)
                .OrderBy(b => b.Address.Id.Value, StringComparer.Ordinal).Select(b => new
                {
                    b.Address.Id, b.Definition.Display, b.Definition.Layout,
                    ((C.DesignSpecification.FunctionalComposite)b.Definition.Specification).Rotation
                }),
            types = _world.Types.Select(t => new { t.Source.Core.Id, t.Source.Display, t.Source.Major, t.Symbol }),
            templates = _world.Templates.Select(t => new { t.Core.Id, t.Source.Display, t.Source.Layout, t.SceneModel.Design }),
            railways = _world.Rules.OfType<ParsedRule.Railway>().Select(r => r.Appearance),
            processes = _world.Processes.Select(p => new { p.Id, p.Display }),
            profiles = _world.Profiles.Select(p => new
            {
                p.Id, p.Display, configuration = GenerationSemantics.Configuration(p.Configuration, Composition),
                start = GenerationSemantics.Start(p.Start, Composition)
            }),
            creatures = _blueprints.Values.Where(blueprint => blueprint.Address.Library == C.BlueprintLibrary.Packages &&
                blueprint.Definition.Specification is C.DesignSpecification.Creature)
                .OrderBy(blueprint => blueprint.Address.Id.Value, StringComparer.Ordinal).Select(blueprint => new
                { blueprint.Address.Id, blueprint.Definition.Display, blueprint.Definition.Layout }),
            lairs = _world.Lairs.Select(lair => new { lair.Core.Id, lair.Display, scene = lair.Scene.Design }),
            packages = Packages.Where(p => p.State == C.PackageState.Available).Select(p => p.Metadata).OfType<C.PackageMetadata>()
                .OrderBy(p => p.Id.Value, StringComparer.Ordinal)
        });
    }

    internal ImmutableArray<C.MissingContentReference> Missing(K.CoreReferenceManifest references, string stage = "restore")
    {
        var required = references.Definitions.Select(d => (d.Definition, d.Field))
            .Concat(SavedDesigns.Where(d => references.Designs.Contains(d.Hash) || references.Templates.Any(t =>
                _layouts.TryGetValue(t.Owner, out var layout) && layout.SceneModel.Design == d.Hash))
                .SelectMany(d => DesignTraversal.References(d.Definition, $"Designs[{d.Hash.Value}]")));
        return required.Where(r => !HasDefinition(r.Item1)).Distinct()
            .OrderBy(r => DefinitionId(r.Item1), StringComparer.Ordinal).ThenBy(r => r.Item2, StringComparer.Ordinal)
            .Select(r =>
            {
                var package = new C.PackageId(DefinitionId(r.Item1).Split(':')[0]);
                var reference = new C.ContentReference.Core(r.Item1);
                return new C.MissingContentReference(reference, package, r.Item2,
                    [new("reference_missing", C.ContentDiagnosticSeverity.Error, stage, reference, new(package, ""), r.Item2, null, "必需定义缺失。")]);
            }).ToImmutableArray();
    }

    private bool HasDefinition(K.CoreDefinitionReference definition) => definition switch
    {
        K.CoreDefinitionReference.Voxel voxel => _voxels.ContainsKey(voxel.Id),
        K.CoreDefinitionReference.Composite composite => FindComposite(composite.Id) is not null,
        K.CoreDefinitionReference.Rule rule => _world.Rules.Any(r => r.Id == rule.Id),
        K.CoreDefinitionReference.Process process => FindProcess(process.Id) is not null,
        K.CoreDefinitionReference.SettlementType type => _world.Types.Any(t => t.Source.Core.Id == type.Id),
        K.CoreDefinitionReference.Template template => _world.Templates.Any(t => t.Core.Id == template.Id),
        K.CoreDefinitionReference.Terrain terrain => _world.Terrains.Any(t => t.Id == terrain.Id),
        _ => false
    };

    internal ContentView WithSettlementCopies(ImmutableArray<K.OwnedSettlementTemplateReference> copies)
    {
        if (copies.IsDefault) throw new ArgumentException("副本清单必须初始化。", nameof(copies));
        if (copies.IsEmpty) return this;
        var layouts = _layouts.ToDictionary();
        foreach (var copy in copies)
        {
            if (copy is null || copy.Owner.Value == 0 || copy.Plots.IsDefault || copy.Plots.Any(p => p is null) || layouts.ContainsKey(copy.Owner))
                throw new ArgumentException("所有者必须唯一且地块清单必须初始化。", nameof(copies));
            var source = _world.Templates.FirstOrDefault(t => t.Core.Id == copy.SourceTemplate);
            if (source is null || !source.Core.Plots.Select(p => new K.TemplatePlotReference(p.Number, p.Size))
                .SequenceEqual(copy.Plots.OrderBy(p => p.Number)))
                throw new ArgumentException("来源或地块对应不属于该固定视图。", nameof(copies));
            layouts.Add(copy.Owner, new(copy.Owner, copy.SourceTemplate, source.SceneModel, source.Source.Layout, source.Source.Display));
        }
        return new(this, layouts.ToFrozenDictionary());
    }

    internal C.ContentResult<K.WorldGenerationInput> CreateWorldInput(C.GenerationProfileId id, ulong seed, string version)
    {
        var profile = _world.Profiles.FirstOrDefault(p => p.Id == id);
        if (profile is null) return new C.ContentResult<K.WorldGenerationInput>.Failure(
            [new("reference_missing", C.ContentDiagnosticSeverity.Error, "generation", new C.ContentReference.Generation(id),
                null, "", null, "生成配置不存在。")]);
        var revision = WorldSemantics.Generation(profile, _world,
            _voxels.Values.OrderBy(v => v.Definition.Definition.Value, StringComparer.Ordinal).ToImmutableArray(),
            _blueprints.Values.Where(b => b.Address.Library == C.BlueprintLibrary.Packages)
                .OrderBy(b => b.Address.Id.Value, StringComparer.Ordinal).ToImmutableArray(), Core.Goods);
        return new C.ContentResult<K.WorldGenerationInput>.Success(new(seed, version, revision, profile.Configuration, profile.Start));
    }

    internal LoadedVoxel? FindVoxel(K.VoxelDefinitionId definition) => _voxels.GetValueOrDefault(definition);
    internal C.BlueprintRecord? FindComposite(K.CompositeDefinitionId definition) =>
        _blueprints.GetValueOrDefault(new(C.BlueprintLibrary.Packages, new(definition.Value))) is { Definition.Specification: C.DesignSpecification.FunctionalComposite } record ? record : null;
    internal ParsedProcess? FindProcess(K.ProcessRef id) => _world.Processes.FirstOrDefault(p => p.Reference == id);
    internal ParsedProcess? FindProcess(string id) => _world.Processes.FirstOrDefault(p => p.Id == id);

    internal static string DefinitionId(K.CoreDefinitionReference definition) => definition switch
    {
        K.CoreDefinitionReference.Voxel voxel => voxel.Id.Value,
        K.CoreDefinitionReference.Composite composite => composite.Id.Value,
        K.CoreDefinitionReference.Rule rule => rule.Id.Value,
        K.CoreDefinitionReference.SettlementType type => type.Id.Value,
        K.CoreDefinitionReference.Template template => template.Id.Value,
        K.CoreDefinitionReference.Terrain terrain => terrain.Id.Value,
        K.CoreDefinitionReference.Creature creature => creature.Id.Value,
        K.CoreDefinitionReference.Lair lair => lair.Id.Value,
        K.CoreDefinitionReference.Process { Id: K.ProcessRef.Production process } => process.Definition.Value,
        K.CoreDefinitionReference.Process { Id: K.ProcessRef.Recovery process } => process.Definition.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(definition))
    };

    public C.ContentLookup<C.VoxelAppearanceProjection> FindVoxelAppearance(K.VoxelDefinitionId definition) =>
        _voxels.TryGetValue(definition, out var voxel) ? new C.ContentLookup<C.VoxelAppearanceProjection>.Found(voxel.Appearance) : Absent<C.VoxelAppearanceProjection>(new K.CoreDefinitionReference.Voxel(definition));
    public C.ContentLookup<C.VoxelDisplayProjection> FindVoxelDisplay(K.VoxelDefinitionId definition) =>
        _voxels.TryGetValue(definition, out var voxel) ? new C.ContentLookup<C.VoxelDisplayProjection>.Found(voxel.Display) : Absent<C.VoxelDisplayProjection>(new K.CoreDefinitionReference.Voxel(definition));
    public C.ContentLookup<C.ContentDisplay> FindDefinitionDisplay(K.CoreDefinitionReference definition)
    {
        if (definition is K.CoreDefinitionReference.Composite composite && FindComposite(composite.Id) is { } compositeRecord)
            return new C.ContentLookup<C.ContentDisplay>.Found(new(compositeRecord.Definition.Display, null, null, false));
        if (definition is K.CoreDefinitionReference.Process process && FindProcess(process.Id) is { } flow)
            return new C.ContentLookup<C.ContentDisplay>.Found(new(flow.Display, null, null, false));
        if (definition is K.CoreDefinitionReference.Voxel reference && _voxels.TryGetValue(reference.Id, out var voxel))
            return new C.ContentLookup<C.ContentDisplay>.Found(new(voxel.Display.Display, voxel.Display.Icon, null, false));
        if (definition is K.CoreDefinitionReference.SettlementType type && _world.Types.FirstOrDefault(t => t.Source.Core.Id == type.Id) is { } entry)
            return new C.ContentLookup<C.ContentDisplay>.Found(new(entry.Source.Display, null, entry.Symbol, entry.Source.Major));
        if (definition is K.CoreDefinitionReference.Terrain terrain && _world.Terrains.FirstOrDefault(t => t.Id == terrain.Id) is { } ground)
            return new C.ContentLookup<C.ContentDisplay>.Found(new(ground.Display, null, null, false));
        if (definition is K.CoreDefinitionReference.Template template && _world.Templates.FirstOrDefault(t => t.Core.Id == template.Id) is { } source)
            return new C.ContentLookup<C.ContentDisplay>.Found(new(source.Source.Display, null, null, false));
        if (definition is K.CoreDefinitionReference.Creature creature &&
            FindCreature(creature.Id) is C.ContentLookup<C.CreatureAppearanceProjection>.Found appearance)
            return new C.ContentLookup<C.ContentDisplay>.Found(new(appearance.Value.Display, null, null, false));
        if (definition is K.CoreDefinitionReference.Lair lair && _world.Lairs.FirstOrDefault(item => item.Core.Id == lair.Id) is { } loadedLair)
            return new C.ContentLookup<C.ContentDisplay>.Found(new(loadedLair.Display, null, null, false));
        return Absent<C.ContentDisplay>(definition);
    }
    public C.ContentLookup<C.GoodPresentation> FindGood(K.GoodId good)
    {
        if (good is K.GoodId.Voxel reference && _voxels.TryGetValue(reference.Definition, out var voxel) && voxel.Definition.Good is not null)
            return new C.ContentLookup<C.GoodPresentation>.Found(new(good, new(voxel.Display.Display, voxel.Display.Icon, null, false), null, false));
        if (good is K.GoodId.Composite composite && FindComposite(composite.Definition) is { } record)
            return new C.ContentLookup<C.GoodPresentation>.Found(new(good, new(record.Definition.Display, null, null, false), ((C.DesignEvaluation.Complete)record.Evaluation).Design, false));
        if (good is K.GoodId.Assembly assembly && AssemblyDisplays.FirstOrDefault(d => d.Composition == assembly.Composition) is { } display && _designs.TryGetValue(display.Design, out var design))
        {
            var draft = design is C.DesignEvaluation.Complete c ? c.Definition : ((C.DesignEvaluation.Partial)design).Definition;
            return new C.ContentLookup<C.GoodPresentation>.Found(new(good, new(draft.Display, null, null, false), display.Design, design is C.DesignEvaluation.Partial));
        }
        return good switch { K.GoodId.Voxel missing => Absent<C.GoodPresentation>(new K.CoreDefinitionReference.Voxel(missing.Definition)),
            K.GoodId.Composite missing => Absent<C.GoodPresentation>(new K.CoreDefinitionReference.Composite(missing.Definition)), _ => new C.ContentLookup<C.GoodPresentation>.NotFound() };
    }
    private C.ContentLookup<T> Absent<T>(K.CoreDefinitionReference definition) where T : notnull
    {
        var diagnostics = Core.MissingReferences.Where(m => m.Reference == definition).Select(m => new C.ContentDiagnostic(
            "reference_missing", C.ContentDiagnosticSeverity.Error, "lookup", new C.ContentReference.Core(definition),
            new(new(DefinitionId(definition).Split(':')[0]), ""), m.FieldPath, null, "定义缺失。")).ToImmutableArray();
        return diagnostics.IsEmpty ? new C.ContentLookup<T>.NotFound() : new C.ContentLookup<T>.Missing(diagnostics);
    }
    public C.ContentLookup<C.ContentAsset> FindAsset(C.AssetRevision revision) =>
        _assets.TryGetValue(revision, out var asset) ? new C.ContentLookup<C.ContentAsset>.Found(asset) : new C.ContentLookup<C.ContentAsset>.NotFound();
    public C.ContentLookup<C.BlueprintRecord> FindBlueprint(C.BlueprintAddress address) =>
        _blueprints.TryGetValue(address, out var blueprint) ? new C.ContentLookup<C.BlueprintRecord>.Found(blueprint) :
        UnreadableBlueprints.FirstOrDefault(b => b.Address == address) is { } unavailable
            ? new C.ContentLookup<C.BlueprintRecord>.Missing(unavailable.Diagnostics) : new C.ContentLookup<C.BlueprintRecord>.NotFound();
    public C.ContentLookup<C.DesignEvaluation> FindDesign(K.DesignHash hash) =>
        _designs.TryGetValue(hash, out var design) ? new C.ContentLookup<C.DesignEvaluation>.Found(design) : new C.ContentLookup<C.DesignEvaluation>.NotFound();
    public C.ContentLookup<C.SettlementLayoutProjection> FindSettlementLayout(K.SettlementId settlement) =>
        _layouts.TryGetValue(settlement, out var layout) ? new C.ContentLookup<C.SettlementLayoutProjection>.Found(layout) : new C.ContentLookup<C.SettlementLayoutProjection>.NotFound();
    public C.ContentLookup<C.CreatureAppearanceProjection> FindCreature(K.CreatureDefinitionId creature)
    {
        var record = _blueprints.Values.FirstOrDefault(blueprint => blueprint.Address.Id.Value == creature.Value
            && blueprint.Definition.Specification is C.DesignSpecification.Creature);
        return record?.Evaluation is C.DesignEvaluation.Complete complete
            ? new C.ContentLookup<C.CreatureAppearanceProjection>.Found(new(creature, complete.Scene, record.Definition.Display))
            : Absent<C.CreatureAppearanceProjection>(new K.CoreDefinitionReference.Creature(creature));
    }
    public C.ContentLookup<C.LairAppearanceProjection> FindLair(K.LairTypeId lair)
    {
        var loaded = _world.Lairs.FirstOrDefault(item => item.Core.Id == lair);
        return loaded is null
            ? Absent<C.LairAppearanceProjection>(new K.CoreDefinitionReference.Lair(lair))
            : new C.ContentLookup<C.LairAppearanceProjection>.Found(new(lair, loaded.Scene, new(loaded.Display, null, null, false)));
    }

    private static K.CreatureProjection CreatureProjection(C.BlueprintRecord record)
    {
        var creature = (C.DesignSpecification.Creature)record.Definition.Specification;
        var behavior = creature.Behavior!;
        var id = new K.CreatureDefinitionId(record.Address.Id.Value);
        return new(id, DefinitionJson.Semantic("creature", id.Value, new
        {
            hp = behavior.HitPoints, speed = behavior.SpeedMetersPerSecond, retreat_threshold = behavior.RetreatThreshold,
            raid_duration = behavior.RaidDurationSeconds,
            weapon = creature.Weapon is { } weapon ? new { damage = weapon.Damage, interval = weapon.IntervalSeconds } : null
        }), behavior.HitPoints, behavior.SpeedMetersPerSecond, behavior.RetreatThreshold, behavior.RaidDurationSeconds, creature.Weapon);
    }
    public C.ContentLookup<C.TerrainAppearanceProjection> FindTerrain(K.TerrainTypeId terrain) =>
        _world.Terrains.FirstOrDefault(t => t.Id == terrain) is { } ground
            ? new C.ContentLookup<C.TerrainAppearanceProjection>.Found(ground) : Absent<C.TerrainAppearanceProjection>(new K.CoreDefinitionReference.Terrain(terrain));
    public C.ContentLookup<C.RailwayAppearanceProjection> FindRailwayAppearance(K.RuleDefinitionId rule) =>
        _world.Rules.OfType<ParsedRule.Railway>().FirstOrDefault(r => r.Id == rule) is { } railway
            ? new C.ContentLookup<C.RailwayAppearanceProjection>.Found(railway.Appearance) : Absent<C.RailwayAppearanceProjection>(new K.CoreDefinitionReference.Rule(rule));
}
