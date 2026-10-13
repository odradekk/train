using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Train.Content.Loading;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Designs;

internal static class DesignEvaluator
{
    internal static K.DesignHash Identity(C.DesignDraft draft) => new(Hash(
        draft with { Display = new(draft.Display.Name.Trim(), draft.Display.Description.Trim()) },
        draft.Layout.Cells.OrderBy(cell => cell.At.X).ThenBy(cell => cell.At.Y).ThenBy(cell => cell.At.Z), false));

    internal static C.DesignEvaluation Evaluate(ContentView content, C.DesignDraft draft, bool allowMissing = false)
    {
        if (!draft.Layout.NestedItems.IsEmpty || draft.Specification is
            C.DesignSpecification.Assembly or C.DesignSpecification.FunctionalComposite or C.DesignSpecification.Creature)
            throw new NotImplementedException("嵌套、大型设备和生物设计尚未实现。");
        if (draft.Specification is C.DesignSpecification.Vehicle { Size: null } or
            C.DesignSpecification.Building { PlotSize: null } or C.DesignSpecification.Building { Kind: null })
            throw new NotImplementedException("不完整草稿的部分计算尚未实现。");

        var display = new C.DisplayText(draft.Display.Name.Trim(), draft.Display.Description.Trim());
        if (display != draft.Display) draft = draft with { Display = display };

        var diagnostics = ImmutableArray.CreateBuilder<C.ContentDiagnostic>();
        void Invalid(string field, string message, int? cell = null) => diagnostics.Add(new(
            "design_invalid", C.ContentDiagnosticSeverity.Error, "evaluate", null, null, field,
            cell is null ? null : new C.DesignElementPath.Cell([], cell.Value), message));
        var cells = draft.Layout.Cells.Select((cell, index) => (Cell: cell, Index: index))
            .OrderBy(item => item.Cell.At.X).ThenBy(item => item.Cell.At.Y).ThenBy(item => item.Cell.At.Z).ToArray();
        foreach (var item in cells)
        {
            if (item.Cell.PaletteIndex < 0 || item.Cell.PaletteIndex >= draft.Layout.Palette.Length)
                Invalid($"/layout/cells/{item.Index}/3", "调色板序号超出范围。", item.Index);
        }
        if (diagnostics.Count > 0) return new C.DesignEvaluation.Failed(diagnostics.ToImmutable());
        var paletteCells = cells.ToLookup(item => item.Cell.PaletteIndex, item => item.Index);
        var missing = ImmutableArray.CreateBuilder<K.MissingCoreReference>();
        for (var index = 0; index < draft.Layout.Palette.Length; index++)
        {
            var entry = draft.Layout.Palette[index];
            var voxel = content.FindVoxel(entry.Voxel);
            if (voxel is null)
            {
                if (!allowMissing) throw new NotImplementedException("只有已保存设计支持缺失定义的部分计算。");
                var field = $"/layout/palette/{index}/voxel";
                missing.Add(new(new K.CoreDefinitionReference.Voxel(entry.Voxel), field));
                diagnostics.Add(new("reference_missing", C.ContentDiagnosticSeverity.Error, "evaluate", null,
                    new(new(entry.Voxel.Value.Split(':')[0]), ""), field, null, "体素定义缺失。"));
            }
            var cellIndex = paletteCells[index].Select(cell => (int?)cell).FirstOrDefault();
            if (entry.Paint is { } paint && !Regex.IsMatch(paint.Value, "\\A#[0-9a-f]{6}\\z", RegexOptions.CultureInvariant))
                Invalid($"/layout/palette/{index}/paint", "颜色必须为小写 #rrggbb。", cellIndex);
            if (!Enum.IsDefined(entry.Orientation) || voxel is not null && !Allowed(entry.Orientation, voxel.Definition.Orientations))
                Invalid($"/layout/palette/{index}/orientation", "体素不允许该朝向。", cellIndex);
            if (entry.Shape is { } shape)
            {
                if (voxel?.Appearance.Presentation is C.VoxelPresentation.Cube cube && !cube.Shapes.Contains(shape))
                    Invalid($"/layout/palette/{index}/shape", "体素不允许该形状。", cellIndex);
                else if (voxel?.Appearance.Presentation is C.VoxelPresentation.Mesh)
                    Invalid($"/layout/palette/{index}/shape", "模型体素不能指定形状。", cellIndex);
            }
        }

        var design = new K.DesignHash(Hash(draft, cells.Select(item => item.Cell), false));
        var composition = new K.CompositionHash(Hash(draft, cells.Select(item => item.Cell), true));
        var envelope = Envelope(draft.Specification);
        var sceneOnly = draft.Specification is C.DesignSpecification.SceneModel;
        if (cells.Length == 0) Invalid("/layout/cells", "布局必须非空。");
        if (draft.Display.Name.Length is < 1 or > 100) Invalid("/display/name", "名称长度必须为 1–100。");
        if (draft.Display.Description.Length is < 1 or > 2000) Invalid("/display/description", "说明长度必须为 1–2000。");
        if (draft.Specification is C.DesignSpecification.Building building)
        {
            if ((building.PlotSize == K.PlotSize.Station) != (building.Kind == K.BuildingKind.Station))
                Invalid("/simulation/building_type", "站点尺寸和站点建筑类型必须相互对应。");
            if (building.FacilityTags.Distinct().Count() != building.FacilityTags.Length)
                Invalid("/simulation/facility_tags", "设施标签不能重复。");
            if (!Enum.IsDefined(building.Kind!.Value)) Invalid("/simulation/building_type", "未知建筑类型。");
            if (building.FacilityTags.Any(tag => !Enum.IsDefined(tag))) Invalid("/simulation/facility_tags", "未知设施标签。");
        }
        if (draft.Specification is C.DesignSpecification.SceneModel { Flatten: { } flatten } &&
            (flatten.From.X > flatten.To.X || flatten.From.Z > flatten.To.Z))
            Invalid("/appearance/flatten", "平整区域起点不能大于终点。");

        var occupied = new HashSet<C.GridPosition>();
        var sceneVoxels = ImmutableArray.CreateBuilder<C.SceneVoxelProjection>();
        var elements = ImmutableArray.CreateBuilder<C.DesignElementProjection>();
        var parts = ImmutableArray.CreateBuilder<K.PartProjection>();
        var locks = ImmutableArray.CreateBuilder<C.DesignEditLock>();
        var bom = new Dictionary<K.VoxelDefinitionId, long>();
        var structuralGoods = new Dictionary<K.VoxelDefinitionId, long>();
        var mass = 0d;
        var structureDurability = 0L;
        var structuralValue = 0L;
        var structuralCount = 0L;
        foreach (var item in cells)
        {
            var cell = item.Cell;
            var palette = draft.Layout.Palette[cell.PaletteIndex];
            var voxel = content.FindVoxel(palette.Voxel);
            var definition = voxel?.Definition;
            var element = new C.DesignElementPath.Cell([], item.Index);
            var number = new K.PartId(elements.Count);
            var functional = definition is not null && !definition.Capabilities.IsEmpty;
            if (!occupied.Add(cell.At)) Invalid($"/layout/cells/{item.Index}", "坐标重复。", item.Index);
            if (definition is not null && sceneOnly != (definition.Good is null))
                Invalid($"/layout/cells/{item.Index}", sceneOnly ? "场景模型只能使用场景体素。" : "车辆和建筑只能使用货物体素。", item.Index);
            var shape = palette.Shape;
            if (voxel?.Appearance.Presentation is C.VoxelPresentation.Cube cube)
                shape ??= cube.Shapes[0];
            if (envelope is not null && !Contains(envelope, cell.At))
                Invalid($"/layout/cells/{item.Index}", "体素超出尺寸包络。", item.Index);
            sceneVoxels.Add(new(cell.At, palette.Voxel, palette.Orientation, shape, palette.Paint,
                number, element, [], cell.At, palette.Orientation, definition is null));
            elements.Add(new(number, element, new C.ComputedValue<ImmutableArray<C.GridPosition>>.Known([cell.At]), functional, definition is null));
            if (definition is null)
            {
                var reference = new K.CoreDefinitionReference.Voxel(palette.Voxel);
                if (!sceneOnly) parts.Add(new(number, reference, new K.GoodId.Voxel(palette.Voxel), 0, 0, new(0), [], true));
                locks.Add(new(element, new C.ContentReference.Core(reference), new(palette.Voxel.Value.Split(':')[0]),
                    Enum.GetValues<C.LockedDesignOperation>().ToImmutableArray(),
                    diagnostics.Where(d => d.Code == "reference_missing" && d.Field == $"/layout/palette/{cell.PaletteIndex}/voxel").ToImmutableArray()));
                continue;
            }
            if (definition.Good is not { } good) continue;
            mass += good.MassTons;
            bom[palette.Voxel] = checked(bom.GetValueOrDefault(palette.Voxel) + 1);
            if (functional)
            {
                parts.Add(new(number, new K.CoreDefinitionReference.Voxel(palette.Voxel), good.Good,
                    definition.MaxDurability, 1, good.BaseValue, definition.Capabilities, false));
                structureDurability = checked(structureDurability + Math.Max(1, (definition.MaxDurability + 1) / 2));
            }
            else
            {
                structuralCount++;
                structureDurability = checked(structureDurability + definition.MaxDurability);
                structuralValue = checked(structuralValue + good.BaseValue.Value);
                structuralGoods[palette.Voxel] = checked(structuralGoods.GetValueOrDefault(palette.Voxel) + 1);
            }
        }
        if (!double.IsFinite(mass)) throw new OverflowException("设计总质量必须为有限值。");
        if (!sceneOnly && occupied.Count > 0)
        {
            var connected = Connected(occupied, cells[0].Cell.At);
            foreach (var item in cells.Where(item => !connected.Contains(item.Cell.At)))
                Invalid($"/layout/cells/{item.Index}", "体素未与设计其余部分六面连通。", item.Index);
        }
        C.GridBounds? bounds = cells.Length == 0 ? null : new(
            new(cells.Min(item => item.Cell.At.X), cells.Min(item => item.Cell.At.Y), cells.Min(item => item.Cell.At.Z)),
            new(cells.Max(item => item.Cell.At.X), cells.Max(item => item.Cell.At.Y), cells.Max(item => item.Cell.At.Z)));
        var vehicleLength = draft.Specification is C.DesignSpecification.Vehicle && bounds is not null
            ? (long)bounds.Maximum.Z - bounds.Minimum.Z + 1 : 0;
        if (vehicleLength > int.MaxValue)
        {
            var last = cells.First(item => item.Cell.At.Z == bounds!.Maximum.Z);
            Invalid($"/layout/cells/{last.Index}", "车辆长度超出 Int32 表示范围。", last.Index);
            return new C.DesignEvaluation.Failed(diagnostics.ToImmutable());
        }
        K.DesignKind? kind = draft.Specification switch
        {
            C.DesignSpecification.Vehicle vehicle => new K.DesignKind.Vehicle(vehicle.Size!.Value, (int)vehicleLength),
            C.DesignSpecification.Building b => new K.DesignKind.Building(b.PlotSize!.Value, b.Kind!.Value,
                b.FacilityTags.OrderBy(tag => Wire(tag), StringComparer.Ordinal).ToImmutableArray()),
            _ => null
        };
        var valid = diagnostics.Count == 0;
        var core = kind is null ? null : new K.DesignInput(content.Core.Identity, new(
            design, composition, kind, parts.ToImmutable(), structureDurability, structuralCount,
            mass, new(structuralValue), Goods(structuralGoods), Goods(bom), bom.Values.Sum(), [], valid, missing.ToImmutable()), []);
        var scene = new C.DesignSceneProjection(design, bounds, envelope, true, null,
            (draft.Specification as C.DesignSpecification.SceneModel)?.Flatten,
            sceneVoxels.ToImmutable(), elements.ToImmutable(), [], [], locks.ToImmutable());
        if (missing.Count > 0)
        {
            var reasons = diagnostics.Where(d => d.Code == "reference_missing").ToImmutableArray();
            return new C.DesignEvaluation.Partial(draft,
                new(new C.ComputedValue<double>.Unknown(reasons), new C.ComputedValue<K.Money>.Unknown(reasons),
                    new C.ComputedValue<long>.Unknown(reasons), new C.ComputedValue<ImmutableArray<K.PartProjection>>.Unknown(reasons),
                    new C.ComputedValue<ImmutableArray<K.GoodsQuantity>>.Unknown(reasons), new C.ComputedValue<int>.Known((int)vehicleLength)),
                new(mass, new(structuralValue), structureDurability, parts.ToImmutable(), Goods(bom)), core?.Design, scene,
                diagnostics.Select(d => d with { Subject = new C.ContentReference.Design(design) }).ToImmutableArray());
        }
        return new C.DesignEvaluation.Complete(design, composition, draft, core, scene, valid,
            diagnostics.Select(diagnostic => diagnostic with { Subject = new C.ContentReference.Design(design) }).ToImmutableArray());
    }

    private static ImmutableArray<K.GoodsQuantity> Goods(Dictionary<K.VoxelDefinitionId, long> counts) =>
        counts.OrderBy(item => item.Key.Value, StringComparer.Ordinal)
            .Select(item => new K.GoodsQuantity(new K.GoodId.Voxel(item.Key), item.Value)).ToImmutableArray();

    private static bool Allowed(C.VoxelOrientation orientation, C.AllowedVoxelOrientations allowed) =>
        Enum.IsDefined(orientation) && (allowed == C.AllowedVoxelOrientations.All24 ||
        orientation == C.VoxelOrientation.PxPy || allowed == C.AllowedVoxelOrientations.FourWay &&
        orientation is C.VoxelOrientation.PzPy or C.VoxelOrientation.NxPy or C.VoxelOrientation.NzPy);

    private static C.GridBounds? Envelope(C.DesignSpecification specification)
    {
        var dimensions = specification switch
        {
            C.DesignSpecification.Vehicle vehicle => vehicle.Size switch
            {
                K.SizeClass.Small => (7, 52, 9), K.SizeClass.Medium => (10, 64, 13),
                K.SizeClass.Large => (14, 80, 18), K.SizeClass.ExtraLarge => (20, 104, 26),
                K.SizeClass.Titan => (30, 136, 38), _ => throw new ArgumentOutOfRangeException(nameof(specification))
            },
            C.DesignSpecification.Building building => building.PlotSize switch
            {
                K.PlotSize.Small => (16, 16, 16), K.PlotSize.Medium => (24, 24, 24),
                K.PlotSize.Large => (32, 32, 32), K.PlotSize.Station => (64, 24, 16),
                _ => throw new ArgumentOutOfRangeException(nameof(specification))
            },
            _ => (0, 0, 0)
        };
        var (width, depth, height) = dimensions;
        return width == 0 ? null : new(new(-width / 2, 0, -depth / 2),
            new(width - width / 2 - 1, height - 1, depth - depth / 2 - 1));
    }

    private static bool Contains(C.GridBounds bounds, C.GridPosition at) =>
        at.X >= bounds.Minimum.X && at.X <= bounds.Maximum.X &&
        at.Y >= bounds.Minimum.Y && at.Y <= bounds.Maximum.Y &&
        at.Z >= bounds.Minimum.Z && at.Z <= bounds.Maximum.Z;

    private static HashSet<C.GridPosition> Connected(HashSet<C.GridPosition> occupied, C.GridPosition first)
    {
        var connected = new HashSet<C.GridPosition> { first };
        var pending = new Queue<C.GridPosition>();
        pending.Enqueue(first);
        (int X, int Y, int Z)[] offsets = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];
        while (pending.TryDequeue(out var at))
            foreach (var offset in offsets)
            {
                var x = (long)at.X + offset.X;
                var y = (long)at.Y + offset.Y;
                var z = (long)at.Z + offset.Z;
                if (x is < int.MinValue or > int.MaxValue || y is < int.MinValue or > int.MaxValue || z is < int.MinValue or > int.MaxValue) continue;
                var neighbour = new C.GridPosition((int)x, (int)y, (int)z);
                if (occupied.Contains(neighbour) && connected.Add(neighbour)) pending.Enqueue(neighbour);
            }
        return connected;
    }

    private static string Hash(C.DesignDraft draft, IEnumerable<C.LayoutCell> cells, bool composition)
    {
        var layout = cells.Select(cell =>
        {
            var palette = draft.Layout.Palette[cell.PaletteIndex];
            var value = Map(("at", new[] { cell.At.X, cell.At.Y, cell.At.Z }),
                ("orientation", Wire(palette.Orientation)), ("voxel", palette.Voxel.Value));
            if (!composition)
            {
                value["shape"] = palette.Shape?.Name;
                value["paint"] = palette.Paint?.Value;
            }
            return value;
        }).ToArray();
        var value = Map(("layout", Map(("cells", layout), ("composites", Array.Empty<object>()))));
        value["display"] = composition ? Map() : Map(("name", draft.Display.Name), ("description", draft.Display.Description));
        switch (draft.Specification)
        {
            case C.DesignSpecification.Vehicle vehicle:
                value["category"] = "vehicle";
                value["simulation"] = Map(("size_class", Wire(vehicle.Size!.Value)));
                break;
            case C.DesignSpecification.Building building:
                value["category"] = "building";
                value["simulation"] = Map(("plot_size", Wire(building.PlotSize!.Value)), ("building_type", Wire(building.Kind!.Value)),
                    ("facility_tags", building.FacilityTags.Select(tag => Wire(tag)).Order(StringComparer.Ordinal).ToArray()));
                break;
            case C.DesignSpecification.SceneModel model:
                value["category"] = "scene_model";
                if (model.Flatten is { } flatten)
                    value["appearance"] = Map(("flatten", Map(("from", new[] { flatten.From.X, flatten.From.Z }),
                        ("to", new[] { flatten.To.X, flatten.To.Z }), ("height", flatten.Height))));
                break;
        }
        return ContentHash.FromCanonicalJson(writer => JsonSerializer.Serialize(writer, value));
    }

    internal static string Wire(Enum value) => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
    private static SortedDictionary<string, object?> Map(params (string Key, object? Value)[] entries) =>
        new(entries.ToDictionary(entry => entry.Key, entry => entry.Value), StringComparer.Ordinal);
}
