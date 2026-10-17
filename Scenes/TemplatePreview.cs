using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class TemplatePreview : ITemplatePreview
{
    public ContentGeneration Content { get; }
    public EditRevision Revision { get; }
    public ISceneFrame Frame { get; }
    public ImmutableArray<TemplatePlotPreview> Plots { get; }
    public ImmutableArray<SceneProblem> Diagnostics { get; }

    internal TemplatePreview(IContentView content, SettlementTemplateDraft draft, EditRevision revision)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Plots.IsDefault || draft.FoundingBuildings.IsDefault || draft.Inventory.IsDefault || draft.ProductionPlans.IsDefault)
            throw new ArgumentException("Template collections must be initialized.", nameof(draft));
        foreach (var plot in draft.Plots) ArgumentNullException.ThrowIfNull(plot);
        Content = content.Generation;
        Revision = revision;
        var objects = ImmutableArray.CreateBuilder<SceneObject>();
        var plots = ImmutableArray.CreateBuilder<TemplatePlotPreview>();
        var lines = ImmutableArray.CreateBuilder<SceneDebugLine>();
        var diagnostics = ImmutableArray.CreateBuilder<SceneProblem>();
        var numbers = draft.Plots.GroupBy(plot => plot.Number).ToDictionary(group => group.Key, group => group.Count());
        ulong nextId = 0;
        var identity = new ScenePose(Vector3.Zero, Quaternion.Identity);
        if (draft.SceneModel is { } model) AddDesign(model, identity);
        else diagnostics.Add(new("content_missing", SceneStage.Build, null, null, null, null, "The scene model is unspecified."));

        for (var i = 0; i < draft.Plots.Length; i++)
        {
            var source = draft.Plots[i];
            var (width, depth, height) = NodeSettlement.Size(source.Size);
            var plot = new TemplatePlotPreview(i, SceneGeometry.Placement(source.At, source.Orientation),
                new(new(-width / 2, 0, -depth / 2), new(width - width / 2 - 1, height - 1, depth - depth / 2 - 1)));
            plots.Add(plot);
            var minimum = plot.Bounds.Minimum;
            var maximum = plot.Bounds.Maximum;
            var corners = new[] { NodeSettlement.Point(plot.Pose, minimum.X, 0, minimum.Z),
                NodeSettlement.Point(plot.Pose, maximum.X + 1, 0, minimum.Z),
                NodeSettlement.Point(plot.Pose, maximum.X + 1, 0, maximum.Z + 1),
                NodeSettlement.Point(plot.Pose, minimum.X, 0, maximum.Z + 1) };
            for (var edge = 0; edge < corners.Length; edge++)
                lines.Add(new(corners[edge], corners[(edge + 1) % corners.Length], new(0.2f, 0.8f, 1, 1)));
            if (numbers[source.Number] > 1)
                diagnostics.Add(new("duplicate_plot_number", SceneStage.Input, null, null, null, null,
                    $"plots[{i}].number: Plot number {source.Number} is repeated."));
            if (source.PrebuiltBuilding is { } building) AddDesign(building, plot.Pose);
        }
        for (var i = 0; i < draft.FoundingBuildings.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(draft.FoundingBuildings[i]);
            if (!numbers.ContainsKey(draft.FoundingBuildings[i].PlotNumber))
                diagnostics.Add(new("target_missing", SceneStage.Input, null, null, null, null,
                    $"founding[{i}]: Plot number {draft.FoundingBuildings[i].PlotNumber} does not exist."));
        }
        for (var i = 0; i < draft.ProductionPlans.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(draft.ProductionPlans[i]);
            if (!numbers.ContainsKey(draft.ProductionPlans[i].PlotNumber))
                diagnostics.Add(new("target_missing", SceneStage.Input, null, null, null, null,
                    $"production_plans[{i}]: Plot number {draft.ProductionPlans[i].PlotNumber} does not exist."));
        }
        Plots = plots.ToImmutable();
        Diagnostics = diagnostics.ToImmutable();
        Frame = new SceneFrame(new(new(Guid.NewGuid()), new(1), new(1), 1), default, 0, SceneReadiness.Ready,
            objects.ToImmutable(), [], [], Diagnostics, editLines: lines.ToImmutable());

        void AddDesign(BlueprintId blueprint, ScenePose pose)
        {
            var address = new BlueprintAddress(BlueprintLibrary.Packages, blueprint);
            var lookup = content.FindBlueprint(address);
            if (lookup is ContentLookup<BlueprintRecord>.Found { Value.Evaluation: not DesignEvaluation.Failed } found)
            {
                var geometry = DesignGeometry.Build(content, found.Value.Evaluation, null, pose, _ => new(++nextId));
                objects.AddRange(geometry.Objects);
                diagnostics.AddRange(geometry.Diagnostics);
                return;
            }
            var reference = new ContentReference.Blueprint(address);
            objects.Add(new(new(++nextId), null, pose, null, [new SceneVisual.Missing(reference, false)], [], []));
            var problems = lookup switch
            {
                ContentLookup<BlueprintRecord>.Missing missing => missing.Diagnostics,
                ContentLookup<BlueprintRecord>.Found { Value.Evaluation: DesignEvaluation.Failed failed } => failed.Diagnostics,
                _ => ImmutableArray<ContentDiagnostic>.Empty
            };
            if (problems.IsDefault) throw new ArgumentException("Diagnostics must be initialized.", nameof(content));
            if (problems.IsEmpty)
                diagnostics.Add(new("content_missing", SceneStage.Build, null, reference, null, null, "The referenced blueprint is unavailable."));
            foreach (var problem in problems)
                diagnostics.Add(new("content_missing", SceneStage.Build, null, reference,
                    problem.Source?.Package, problem.Element, problem.Message));
        }
    }

    public Lookup<int> PickPlot(SceneRay ray)
    {
        SceneGeometry.Validate(ray);
        int? nearest = null;
        var distance = ray.MaximumDistanceCells;
        foreach (var plot in Plots)
        {
            // Voxel rotations have exact grid axes; quaternion roundoff must not move inclusive hit boundaries.
            var right = Vector3.Round(Vector3.Transform(Vector3.UnitX, plot.Pose.Rotation));
            var up = Vector3.Round(Vector3.Transform(Vector3.UnitY, plot.Pose.Rotation));
            var forward = Vector3.Round(Vector3.Transform(Vector3.UnitZ, plot.Pose.Rotation));
            var origin = ray.Origin - plot.Pose.Position;
            var directionY = Vector3.Dot(ray.Direction, up);
            if (directionY == 0) continue;
            var hit = -(double)Vector3.Dot(origin, up) / directionY;
            if (hit < 0 || hit > distance || nearest is not null && hit == distance) continue;
            var x = Vector3.Dot(origin, right) + Vector3.Dot(ray.Direction, right) * hit;
            var z = Vector3.Dot(origin, forward) + Vector3.Dot(ray.Direction, forward) * hit;
            if (x < plot.Bounds.Minimum.X || x > plot.Bounds.Maximum.X + 1 ||
                z < plot.Bounds.Minimum.Z || z > plot.Bounds.Maximum.Z + 1) continue;
            nearest = plot.DraftIndex;
            distance = hit;
        }
        return nearest is { } index ? new Lookup<int>.Found(index) : new Lookup<int>.Missing();
    }
}
