using System.Collections.Immutable;
using System.Text.Json;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Designs;

internal static class DesignTraversal
{
    internal static IEnumerable<(C.LayoutCell Cell, int Index)> Cells(C.DesignDraft draft) =>
        draft.Layout.Cells.Select((cell, index) => (Cell: cell, Index: index))
            .OrderBy(item => item.Cell.At.X).ThenBy(item => item.Cell.At.Y).ThenBy(item => item.Cell.At.Z);

    internal static C.GridPosition At(C.NestedDesignItem item) => item switch
    {
        C.NestedDesignItem.AssemblyCopy copy => copy.At,
        C.NestedDesignItem.FunctionalCompositeReference reference => reference.At,
        C.NestedDesignItem.SceneModelCopy copy => copy.At,
        _ => throw new ArgumentOutOfRangeException(nameof(item))
    };

    internal static IEnumerable<(C.NestedDesignItem Item, int Index)> Items(C.DesignDraft draft) =>
        draft.Layout.NestedItems.Select((item, index) => (Item: item, Index: index))
            .OrderBy(item => At(item.Item).X).ThenBy(item => At(item.Item).Y).ThenBy(item => At(item.Item).Z);

    internal static C.DesignDraft Normalize(C.DesignDraft draft) => draft with
    {
        Display = new(draft.Display.Name.Trim(), draft.Display.Description.Trim()),
        Layout = draft.Layout with { NestedItems = draft.Layout.NestedItems.Select(item => item switch
        {
            C.NestedDesignItem.AssemblyCopy copy => copy with { Design = Normalize(copy.Design) },
            C.NestedDesignItem.SceneModelCopy copy => copy with { Design = Normalize(copy.Design) },
            _ => item
        }).ToImmutableArray() }
    };

    internal static IEnumerable<(K.CoreDefinitionReference Reference, string Field)> References(C.DesignDraft draft, string field = "")
    {
        for (var index = 0; index < draft.Layout.Palette.Length; index++)
            yield return (new K.CoreDefinitionReference.Voxel(draft.Layout.Palette[index].Voxel), $"{field}/layout/palette/{index}/voxel");
        foreach (var (item, index) in Items(draft))
        {
            var nested = $"{field}/layout/composites/{index}";
            if (item is C.NestedDesignItem.FunctionalCompositeReference reference)
                yield return (new K.CoreDefinitionReference.Composite(reference.Definition), nested + "/blueprint");
            else
                foreach (var dependency in References(item is C.NestedDesignItem.AssemblyCopy copy ? copy.Design : ((C.NestedDesignItem.SceneModelCopy)item).Design, nested + "/design"))
                    yield return dependency;
        }
    }

    internal static IEnumerable<C.DesignDraft> Assemblies(C.DesignDraft draft)
    {
        if (draft.Specification is C.DesignSpecification.Assembly) yield return draft;
        foreach (var (item, _) in Items(draft))
        {
            var child = item switch { C.NestedDesignItem.AssemblyCopy copy => copy.Design,
                C.NestedDesignItem.SceneModelCopy copy => copy.Design, _ => null };
            if (child is not null)
                foreach (var assembly in Assemblies(child)) yield return assembly;
        }
    }

    internal static string Hash(C.DesignDraft draft, bool composition) =>
        ContentHash.FromCanonicalJson(writer => JsonSerializer.Serialize(writer, Canonical(draft, composition)));

    private static object Canonical(C.DesignDraft draft, bool composition)
    {
        var cells = Cells(draft).Select(item =>
        {
            var entry = draft.Layout.Palette[item.Cell.PaletteIndex];
            var cell = Map(("at", Position(item.Cell.At)), ("orientation", DesignEvaluator.Wire(entry.Orientation)), ("voxel", entry.Voxel.Value));
            if (!composition) { cell["shape"] = entry.Shape?.Name; cell["paint"] = entry.Paint?.Value; }
            return cell;
        }).ToArray();
        var nested = Items(draft).Select(item =>
        {
            var orientation = item.Item switch { C.NestedDesignItem.AssemblyCopy copy => copy.Orientation,
                C.NestedDesignItem.SceneModelCopy copy => copy.Orientation, C.NestedDesignItem.FunctionalCompositeReference functional => functional.Orientation,
                _ => throw new ArgumentOutOfRangeException() };
            var value = Map(("at", Position(At(item.Item))), ("orientation", DesignEvaluator.Wire(orientation)));
            if (item.Item is C.NestedDesignItem.FunctionalCompositeReference reference)
            { value["blueprint"] = reference.Definition.Value; if (!composition) value["paint"] = reference.Paint?.Value; }
            else value["design"] = Canonical(item.Item is C.NestedDesignItem.AssemblyCopy copy ? copy.Design : ((C.NestedDesignItem.SceneModelCopy)item.Item).Design, composition);
            return value;
        }).ToArray();
        var result = Map(("layout", Map(("cells", cells), ("composites", nested))),
            ("display", composition ? Map() : Map(("name", draft.Display.Name), ("description", draft.Display.Description))));
        C.RotationAppearance? rotation = null;
        switch (draft.Specification)
        {
            case C.DesignSpecification.Vehicle vehicle:
                result["category"] = "vehicle"; result["simulation"] = Map(("size_class", DesignEvaluator.Wire(vehicle.Size!.Value))); break;
            case C.DesignSpecification.Building building:
                result["category"] = "building"; result["simulation"] = Map(("plot_size", DesignEvaluator.Wire(building.PlotSize!.Value)),
                    ("building_type", DesignEvaluator.Wire(building.Kind!.Value)), ("facility_tags", building.FacilityTags.Select(tag => DesignEvaluator.Wire(tag)).Order(StringComparer.Ordinal).ToArray())); break;
            case C.DesignSpecification.SceneModel model:
                result["category"] = "scene_model";
                if (model.Flatten is { } flatten) result["appearance"] = Map(("flatten", Map(("from", new[] { flatten.From.X, flatten.From.Z }),
                    ("to", new[] { flatten.To.X, flatten.To.Z }), ("height", flatten.Height)))); break;
            case C.DesignSpecification.Assembly assembly:
                result["category"] = "large_equipment"; rotation = assembly.Rotation; break;
            case C.DesignSpecification.FunctionalComposite composite:
                result["category"] = "large_equipment"; rotation = composite.Rotation;
                result["simulation"] = Map(("behaviors", Behaviors(composite.Behaviors))); break;
        }
        if (rotation is not null) result["appearance"] = Map(("behaviors", Map(("rotation", Map(
            ("axis", DesignEvaluator.Wire(rotation.Axis)), ("pivot", Position(rotation.Pivot)),
            ("range", new[] { rotation.MinimumDegrees, rotation.MaximumDegrees }), ("speed", rotation.SpeedDegreesPerSecond))))));
        return result;
    }

    private static int[] Position(C.GridPosition at) => [at.X, at.Y, at.Z];
    private static JsonElement Behaviors(ImmutableArray<K.PartCapability> capabilities)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var capability in capabilities) Loading.BlueprintParser.WriteBehavior(writer, capability);
            writer.WriteEndObject();
        }
        using var json = JsonDocument.Parse(stream.ToArray());
        return json.RootElement.Clone();
    }
    private static SortedDictionary<string, object?> Map(params (string Key, object? Value)[] entries) =>
        new(entries.ToDictionary(entry => entry.Key, entry => entry.Value), StringComparer.Ordinal);
}
