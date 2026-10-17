using System.Collections.Immutable;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Editing;

internal sealed record CompositeCells(ImmutableArray<SceneVoxelProjection> Voxels, bool Complete,
    ImmutableArray<SceneProblem> Problems, bool Missing);

internal sealed class EditContent(IContentView content)
{
    private readonly Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>> appearances = [];
    private readonly Dictionary<CompositeDefinitionId, CompositeCells> composites = [];

    internal ContentLookup<VoxelAppearanceProjection> Appearance(VoxelDefinitionId id)
    {
        if (!appearances.TryGetValue(id, out var value)) appearances.Add(id, value = content.FindVoxelAppearance(id));
        return value;
    }

    internal CompositeCells Composite(CompositeDefinitionId id)
    {
        if (composites.TryGetValue(id, out var cached)) return cached;
        var reference = new ContentReference.Core(new CoreDefinitionReference.Composite(id));
        var good = content.FindGood(new GoodId.Composite(id));
        var missing = good is ContentLookup<GoodPresentation>.Missing or ContentLookup<GoodPresentation>.NotFound ||
            good is ContentLookup<GoodPresentation>.Found { Value.Missing: true };
        var problems = MissingProblems(good, reference);
        var cells = ImmutableArray<SceneVoxelProjection>.Empty;
        var complete = false;
        if (good is ContentLookup<GoodPresentation>.Found { Value.DisplayDesign: { } hash })
        {
            var lookup = content.FindDesign(hash);
            if (lookup is ContentLookup<DesignEvaluation>.Found { Value: not DesignEvaluation.Failed } found)
            {
                var projection = DesignGeometry.Projection(found.Value);
                ArgumentNullException.ThrowIfNull(projection);
                var draft = Definition(found.Value);
                if (projection.EditLocks.IsDefault || projection.Voxels.IsDefault ||
                    projection.EditLocks.Any(value => value is null) ||
                    projection.Voxels.Any(cell => cell is null || cell.ObjectPath.IsDefault || !Enum.IsDefined(cell.LocalOrientation)))
                    throw new ArgumentException("Composite occupancy collections and orientations must be initialized and valid.", nameof(id));
                if (!projection.EditLocks.IsEmpty)
                {
                    missing = true;
                    problems = projection.EditLocks.Select(value => new SceneProblem("content_missing", SceneStage.Edit,
                        null, value.Reference, value.Package, value.Element, "The composite contains a missing reference.")).ToImmutableArray();
                }
                complete = draft.Specification is DesignSpecification.FunctionalComposite && projection.OccupancyComplete &&
                    !projection.Voxels.Any(cell => cell.Missing || !cell.ObjectPath.IsEmpty) &&
                    projection.Voxels.Select(cell => cell.LocalAt).Distinct().Count() == projection.Voxels.Length;
                if (complete)
                {
                    var missingAppearances = projection.Voxels.SelectMany(cell => MissingProblems(Appearance(cell.Voxel),
                        new ContentReference.Core(new CoreDefinitionReference.Voxel(cell.Voxel)))).ToImmutableArray();
                    problems = problems.AddRange(missingAppearances);
                    if (missingAppearances.IsEmpty) cells = projection.Voxels;
                    else { complete = false; missing = true; }
                }
            }
            else
            {
                missing |= lookup is ContentLookup<DesignEvaluation>.Missing or ContentLookup<DesignEvaluation>.NotFound;
                problems = MissingProblems(lookup, reference);
            }
        }
        cached = new(cells, complete, problems, missing);
        composites.Add(id, cached);
        return cached;
    }

    internal ImmutableArray<SceneProblem> Missing(DesignDraft draft)
    {
        var result = ImmutableArray.CreateBuilder<SceneProblem>();
        foreach (var entry in draft.Layout.Palette)
            result.AddRange(MissingProblems(Appearance(entry.Voxel), new ContentReference.Core(new CoreDefinitionReference.Voxel(entry.Voxel))));
        foreach (var item in draft.Layout.NestedItems)
            if (EditLayout.Copy(item) is { } copy) result.AddRange(Missing(copy));
            else if (item is NestedDesignItem.FunctionalCompositeReference reference && Composite(reference.Definition) is { Missing: true } composite)
                result.AddRange(composite.Problems.IsEmpty ? [Problem(new ContentReference.Core(new CoreDefinitionReference.Composite(reference.Definition)))] : composite.Problems);
        return result.ToImmutable();
    }

    internal static DesignDraft Definition(DesignEvaluation evaluation) => evaluation switch
    {
        DesignEvaluation.Complete complete => complete.Definition,
        DesignEvaluation.Partial partial => partial.Definition,
        _ => throw new ArgumentException("The evaluation has no draft.", nameof(evaluation))
    };

    internal static DesignSceneProjection ValidateEvaluation(DesignEvaluation evaluation)
    {
        EditLayout.Validate(Definition(evaluation));
        var projection = DesignGeometry.Projection(evaluation);
        ArgumentNullException.ThrowIfNull(projection);
        if (projection.EditLocks.IsDefault || projection.RotationSweeps.IsDefault)
            throw new ArgumentException("Evaluation collections must be initialized.", nameof(evaluation));
        foreach (var value in projection.EditLocks)
        {
            var indices = value?.Element switch
            {
                DesignElementPath.Cell cell => cell.NestedItemIndices,
                DesignElementPath.FunctionalComposite composite => composite.NestedItemIndices,
                _ => default
            };
            if (indices.IsDefault || value!.BlockedOperations.IsDefault)
                throw new ArgumentException("Edit lock collections must be initialized.", nameof(evaluation));
        }
        foreach (var value in projection.RotationSweeps)
            if (value is null || value.NestedItemIndices.IsDefault ||
                value.SweptCells is ComputedValue<ImmutableArray<GridPosition>>.Known { Value.IsDefault: true } ||
                value.ConflictCells is ComputedValue<ImmutableArray<GridPosition>>.Known { Value.IsDefault: true })
                throw new ArgumentException("Rotation sweep collections must be initialized.", nameof(evaluation));
        return projection;
    }
    internal static ImmutableArray<ContentDiagnostic> Diagnostics(DesignEvaluation evaluation) => evaluation switch
    {
        DesignEvaluation.Complete complete => complete.Diagnostics,
        DesignEvaluation.Partial partial => partial.Diagnostics,
        DesignEvaluation.Failed failed => failed.Diagnostics,
        _ => throw new ArgumentException("Invalid evaluation.", nameof(evaluation))
    };

    internal static ImmutableArray<SceneProblem> MissingProblems<T>(ContentLookup<T> lookup, ContentReference reference) where T : notnull => lookup switch
    {
        ContentLookup<T>.Missing missing when !missing.Diagnostics.IsEmpty => missing.Diagnostics.Select(diagnostic =>
            new SceneProblem("content_missing", SceneStage.Edit, null, diagnostic.Subject ?? reference,
                diagnostic.Source?.Package, diagnostic.Element, diagnostic.Message)).ToImmutableArray(),
        ContentLookup<T>.Missing or ContentLookup<T>.NotFound => [Problem(reference)],
        _ => []
    };
    internal static SceneProblem Problem(ContentReference reference) => new("content_missing", SceneStage.Edit,
        null, reference, null, null, "The referenced content is unavailable.");
}
