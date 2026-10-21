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

    internal static C.VoxelOrientation Orientation(C.NestedDesignItem item) => item switch
    {
        C.NestedDesignItem.AssemblyCopy copy => copy.Orientation,
        C.NestedDesignItem.FunctionalCompositeReference reference => reference.Orientation,
        C.NestedDesignItem.SceneModelCopy copy => copy.Orientation,
        _ => throw new ArgumentOutOfRangeException(nameof(item))
    };

    internal static C.DesignDraft? Copy(C.NestedDesignItem item) => item switch
    {
        C.NestedDesignItem.AssemblyCopy copy => copy.Design,
        C.NestedDesignItem.SceneModelCopy copy => copy.Design,
        _ => null
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
                foreach (var dependency in References(Copy(item)!, nested + "/design"))
                    yield return dependency;
        }
    }

    internal static IEnumerable<C.DesignDraft> Assemblies(C.DesignDraft draft)
    {
        if (draft.Specification is C.DesignSpecification.Assembly) yield return draft;
        foreach (var (item, _) in Items(draft))
        {
            var child = Copy(item);
            if (child is not null)
                foreach (var assembly in Assemblies(child)) yield return assembly;
        }
    }

    internal static string Hash(C.DesignDraft draft, bool composition) =>
        ContentHash.FromCanonicalJson(writer => WriteCanonical(writer, draft, composition));

    // 设计哈希和构成哈希已写入存档，规范 JSON 的字节不能变：每层的键必须按 Ordinal 顺序写出。
    private static void WriteCanonical(Utf8JsonWriter writer, C.DesignDraft draft, bool composition)
    {
        var rotation = draft.Specification switch
        {
            C.DesignSpecification.Assembly assembly => assembly.Rotation,
            C.DesignSpecification.FunctionalComposite composite => composite.Rotation,
            _ => null
        };
        writer.WriteStartObject();
        if (rotation is not null)
        {
            writer.WriteStartObject("appearance");
            writer.WriteStartObject("behaviors");
            writer.WriteStartObject("rotation");
            writer.WriteString("axis", DesignEvaluator.Wire(rotation.Axis));
            WritePosition(writer, "pivot", rotation.Pivot);
            writer.WriteStartArray("range");
            writer.WriteNumberValue(rotation.MinimumDegrees);
            writer.WriteNumberValue(rotation.MaximumDegrees);
            writer.WriteEndArray();
            writer.WriteNumber("speed", rotation.SpeedDegreesPerSecond);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        else if (draft.Specification is C.DesignSpecification.SceneModel { Flatten: { } flatten })
        {
            writer.WriteStartObject("appearance");
            writer.WriteStartObject("flatten");
            WriteColumn(writer, "from", flatten.From);
            writer.WriteNumber("height", flatten.Height);
            WriteColumn(writer, "to", flatten.To);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        var category = draft.Specification switch
        {
            C.DesignSpecification.Vehicle => "vehicle",
            C.DesignSpecification.Building => "building",
            C.DesignSpecification.Creature => "creature",
            C.DesignSpecification.SceneModel => "scene_model",
            C.DesignSpecification.Assembly or C.DesignSpecification.FunctionalComposite => "large_equipment",
            _ => null
        };
        if (category is not null) writer.WriteString("category", category);
        writer.WriteStartObject("display");
        if (!composition)
        {
            writer.WriteString("description", draft.Display.Description);
            writer.WriteString("name", draft.Display.Name);
        }
        writer.WriteEndObject();
        writer.WriteStartObject("layout");
        writer.WriteStartArray("cells");
        var orientations = draft.Layout.Palette.Select(entry => DesignEvaluator.Wire(entry.Orientation)).ToArray();
        foreach (var (cell, _) in Cells(draft))
        {
            var entry = draft.Layout.Palette[cell.PaletteIndex];
            writer.WriteStartObject();
            WritePosition(writer, "at", cell.At);
            writer.WriteString("orientation", orientations[cell.PaletteIndex]);
            if (!composition)
            {
                writer.WriteString("paint", entry.Paint?.Value);
                writer.WriteString("shape", entry.Shape?.Name);
            }
            writer.WriteString("voxel", entry.Voxel.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("composites");
        foreach (var (item, _) in Items(draft))
        {
            writer.WriteStartObject();
            WritePosition(writer, "at", At(item));
            var reference = item as C.NestedDesignItem.FunctionalCompositeReference;
            if (reference is not null) writer.WriteString("blueprint", reference.Definition.Value);
            else
            {
                writer.WritePropertyName("design");
                WriteCanonical(writer, Copy(item)!, composition);
            }
            writer.WriteString("orientation", DesignEvaluator.Wire(Orientation(item)));
            if (reference is not null && !composition) writer.WriteString("paint", reference.Paint?.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        switch (draft.Specification)
        {
            case C.DesignSpecification.Vehicle vehicle:
                writer.WriteStartObject("simulation");
                writer.WriteString("size_class", DesignEvaluator.Wire(vehicle.Size!.Value));
                writer.WriteEndObject();
                break;
            case C.DesignSpecification.Building building:
                var plot = DesignEvaluator.Wire(building.PlotSize!.Value);
                writer.WriteStartObject("simulation");
                writer.WriteString("building_type", DesignEvaluator.Wire(building.Kind!.Value));
                writer.WriteStartArray("facility_tags");
                foreach (var tag in building.FacilityTags.Select(tag => DesignEvaluator.Wire(tag)).Order(StringComparer.Ordinal))
                    writer.WriteStringValue(tag);
                writer.WriteEndArray();
                writer.WriteString("plot_size", plot);
                writer.WriteEndObject();
                break;
            case C.DesignSpecification.Creature creature:
                writer.WriteStartObject("simulation");
                writer.WriteStartObject("behaviors");
                if (creature.Behavior is { } stats)
                {
                    writer.WriteStartObject("creature");
                    writer.WriteNumber("hp", stats.HitPoints);
                    writer.WriteNumber("raid_duration", stats.RaidDurationSeconds);
                    writer.WriteNumber("retreat_threshold", stats.RetreatThreshold);
                    writer.WriteNumber("speed", stats.SpeedMetersPerSecond);
                    writer.WriteEndObject();
                }
                if (creature.Weapon is { } weapon)
                {
                    writer.WriteStartObject("weapon");
                    writer.WriteNumber("damage", weapon.Damage);
                    writer.WriteNumber("interval", weapon.IntervalSeconds);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
                break;
            case C.DesignSpecification.FunctionalComposite composite:
                writer.WriteStartObject("simulation");
                writer.WritePropertyName("behaviors");
                Behaviors(composite.Behaviors).WriteTo(writer);
                writer.WriteEndObject();
                break;
        }
        writer.WriteEndObject();
    }

    private static void WritePosition(Utf8JsonWriter writer, string name, C.GridPosition at)
    {
        writer.WriteStartArray(name);
        writer.WriteNumberValue(at.X);
        writer.WriteNumberValue(at.Y);
        writer.WriteNumberValue(at.Z);
        writer.WriteEndArray();
    }

    private static void WriteColumn(Utf8JsonWriter writer, string name, C.GridColumn column)
    {
        writer.WriteStartArray(name);
        writer.WriteNumberValue(column.X);
        writer.WriteNumberValue(column.Z);
        writer.WriteEndArray();
    }
    internal static JsonElement Behaviors(ImmutableArray<K.PartCapability> capabilities)
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
}
