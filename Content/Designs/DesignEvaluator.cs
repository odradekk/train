using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Train.Content.Loading;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Designs;

internal static class DesignEvaluator
{
    internal static K.DesignHash Identity(C.DesignDraft draft) => new(DesignTraversal.Hash(DesignTraversal.Normalize(draft), false));

    internal static C.DesignEvaluation Evaluate(ContentView content, C.DesignDraft draft, bool allowMissing = false,
        K.CompositeDefinitionId? compositeIdentity = null, int depth = 1)
    {
        if (draft.Specification is C.DesignSpecification.Creature)
            throw new NotImplementedException("生物设计尚未实现。");
        if (draft.Specification is C.DesignSpecification.FunctionalComposite && compositeIdentity is null)
            throw new NotImplementedException("根功能复合体草稿缺少定义身份，不能推断货物标识。");
        if (draft.Specification is C.DesignSpecification.Vehicle { Size: null } or
            C.DesignSpecification.Building { PlotSize: null } or C.DesignSpecification.Building { Kind: null })
            throw new NotImplementedException("不完整草稿的部分计算尚未实现。");

        draft = DesignTraversal.Normalize(draft);

        var diagnostics = ImmutableArray.CreateBuilder<C.ContentDiagnostic>();
        void Invalid(string field, string message, int? cell = null) => diagnostics.Add(new(
            "design_invalid", C.ContentDiagnosticSeverity.Error, "evaluate", null, null, field,
            cell is null ? null : new C.DesignElementPath.Cell([], cell.Value), message));
        var cells = DesignTraversal.Cells(draft).ToArray();
        foreach (var item in cells)
        {
            if (item.Cell.PaletteIndex < 0 || item.Cell.PaletteIndex >= draft.Layout.Palette.Length)
                Invalid($"/layout/cells/{item.Index}/3", "调色板序号超出范围。", item.Index);
        }
        if (diagnostics.Count > 0) return new C.DesignEvaluation.Failed(diagnostics.ToImmutable());
        var anchors = new HashSet<C.GridPosition>();
        foreach (var (item, index) in DesignTraversal.Items(draft))
        {
            var field = $"/layout/composites/{index}";
            if (depth >= 4) Invalid(field, "嵌套连同最外层最多 4 层。");
            if (draft.Specification is C.DesignSpecification.FunctionalComposite)
                Invalid(field, "功能复合体不能嵌套。");
            if (!anchors.Add(DesignTraversal.At(item))) Invalid(field + "/at", "同一个宿主里 at 不能重复。");
        }
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
            if (voxel is not null && !Allowed(entry.Orientation, voxel.Definition.Orientations))
                Invalid($"/layout/palette/{index}/orientation", "体素不允许该朝向。", cellIndex);
            if (entry.Shape is { } shape)
            {
                if (voxel?.Appearance.Presentation is C.VoxelPresentation.Cube cube && !cube.Shapes.Contains(shape))
                    Invalid($"/layout/palette/{index}/shape", "体素不允许该形状。", cellIndex);
                else if (voxel?.Appearance.Presentation is C.VoxelPresentation.Mesh)
                    Invalid($"/layout/palette/{index}/shape", "模型体素不能指定形状。", cellIndex);
            }
        }

        var design = Identity(draft);
        var composition = new K.CompositionHash(DesignTraversal.Hash(draft, true));
        var envelope = Envelope(draft.Specification);
        var sceneOnly = draft.Specification is C.DesignSpecification.SceneModel;
        if (cells.Length == 0 && draft.Layout.NestedItems.IsEmpty) Invalid("/layout/cells", "布局必须非空。");
        if (draft.Display.Name.Length is < 1 or > 100) Invalid("/display/name", "名称长度必须为 1–100。");
        if (draft.Display.Description.Length is < 1 or > 2000) Invalid("/display/description", "说明长度必须为 1–2000。");
        if (draft.Specification is C.DesignSpecification.Building building)
        {
            if (PlotRules.IsStation(building.PlotSize!.Value) != (building.Kind == K.BuildingKind.Station))
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
        var bom = new Dictionary<K.GoodId, long>();
        var nestedObjects = ImmutableArray.CreateBuilder<C.NestedObjectProjection>();
        var sweeps = ImmutableArray.CreateBuilder<C.RotationSweepProjection>();
        var returnUnits = ImmutableArray.CreateBuilder<K.DesignReturnUnit>();
        var assemblyGoods = new Dictionary<K.GoodId, K.GoodDefinitionProjection>();
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
            bom[good.Good] = checked(bom.GetValueOrDefault(good.Good) + 1);
            if (draft.Specification is C.DesignSpecification.FunctionalComposite && functional)
                Invalid($"/layout/cells/{item.Index}", "功能复合体只能包含无功能性行为的体素。", item.Index);
            if (functional)
            {
                parts.Add(new(number, new K.CoreDefinitionReference.Voxel(palette.Voxel), good.Good,
                    definition.MaxDurability, 1, good.BaseValue, definition.Capabilities, false));
                structureDurability = checked(structureDurability + Contribution(definition.MaxDurability));
            }
            else
            {
                structuralCount++;
                structureDurability = checked(structureDurability + definition.MaxDurability);
                structuralValue = checked(structuralValue + good.BaseValue.Value);
                structuralGoods[palette.Voxel] = checked(structuralGoods.GetValueOrDefault(palette.Voxel) + 1);
            }
        }
        foreach (var (item, index) in DesignTraversal.Items(draft))
        {
            var field = $"/layout/composites/{index}";
            var path = ImmutableArray.Create(index);
            var element = new C.DesignElementPath.FunctionalComposite([], index);
            var at = DesignTraversal.At(item);
            var orientation = DesignTraversal.Orientation(item);
            if (!Enum.IsDefined(orientation)) { Invalid(field + "/orientation", "未知朝向。"); continue; }
            var placement = new DesignPlacement(at, orientation);
            var reference = item as C.NestedDesignItem.FunctionalCompositeReference;
            var childDraft = DesignTraversal.Copy(item) ?? content.FindComposite(reference!.Definition)?.Definition;
            var number = new K.PartId(elements.Count);
            var missingStart = missing.Count;
            if (reference is not null && reference.Paint is { } paint && !Regex.IsMatch(paint.Value, "\\A#[0-9a-f]{6}\\z", RegexOptions.CultureInvariant))
                Invalid(field + "/paint", "颜色必须为小写 #rrggbb。");
            if (childDraft is null)
            {
                if (!allowMissing) throw new NotImplementedException("只有已保存设计支持缺失定义的部分计算。");
                var definition = new K.CoreDefinitionReference.Composite(reference!.Definition);
                var reason = new C.ContentDiagnostic("reference_missing", C.ContentDiagnosticSeverity.Error, "evaluate", null,
                    new(new(reference.Definition.Value.Split(':')[0]), ""), field + "/blueprint", element, "功能复合体定义缺失。");
                missing.Add(new(definition, reason.Field)); diagnostics.Add(reason);
                nestedObjects.Add(new(path, at, orientation, new C.NestedObjectDetails.MissingFunctionalComposite(reference.Definition, number)));
                elements.Add(new(number, element, new C.ComputedValue<ImmutableArray<C.GridPosition>>.Unknown([reason]), true, true));
                parts.Add(new(number, definition, new K.GoodId.Composite(reference.Definition), 0, 0, new(0), [], true));
                locks.Add(new(element, new C.ContentReference.Core(definition), new(reference.Definition.Value.Split(':')[0]),
                    Enum.GetValues<C.LockedDesignOperation>().ToImmutableArray(), [reason]));
                sweeps.Add(new(path, new C.ComputedValue<ImmutableArray<C.GridPosition>>.Unknown([reason]), new C.ComputedValue<ImmutableArray<C.GridPosition>>.Unknown([reason])));
                continue;
            }
            if (sceneOnly != (item is C.NestedDesignItem.SceneModelCopy) ||
                item is C.NestedDesignItem.AssemblyCopy && childDraft.Specification is not C.DesignSpecification.Assembly ||
                item is C.NestedDesignItem.SceneModelCopy && childDraft.Specification is not C.DesignSpecification.SceneModel)
            { Invalid(field + "/design/category", "宿主不允许此复制设计或其中的功能参数。"); continue; }
            var child = Evaluate(content, childDraft, allowMissing, reference?.Definition, depth + 1);
            ImmutableArray<C.ContentDiagnostic> childDiagnostics = child switch { C.DesignEvaluation.Complete c => c.Diagnostics,
                C.DesignEvaluation.Partial p => p.Diagnostics, C.DesignEvaluation.Failed f => f.Diagnostics, _ => throw new InvalidOperationException() };
            diagnostics.AddRange(childDiagnostics.Select(d => Prefix(d, index, reference is null ? "/design" : "/blueprint")));
            if (child is C.DesignEvaluation.Failed) return new C.DesignEvaluation.Failed(diagnostics.ToImmutable());
            var childScene = child is C.DesignEvaluation.Complete complete ? complete.Scene : ((C.DesignEvaluation.Partial)child).Scene;
            var childCore = child is C.DesignEvaluation.Complete full ? full.Core?.Design : ((C.DesignEvaluation.Partial)child).CorePlaceholder;
            var childComposition = child is C.DesignEvaluation.Complete computed ? computed.Composition
                : new K.CompositionHash(DesignTraversal.Hash(childDraft, true));
            var childGood = reference is not null ? (K.GoodId)new K.GoodId.Composite(reference.Definition) : new K.GoodId.Assembly(childComposition);
            var childParts = childCore?.Parts.Select(p => p with { Id = new(p.Id.Value + number.Value) }).ToImmutableArray() ?? [];
            if (childCore is not null)
            {
                mass += childCore.MassTons;
                if (child is C.DesignEvaluation.Complete) bom[childGood] = checked(bom.GetValueOrDefault(childGood) + 1);
                missing.AddRange(childCore.MissingReferences.Select(m => m with { FieldPath = field + (reference is null ? "/design" : "/blueprint") + m.FieldPath }));
                if (reference is null)
                {
                    parts.AddRange(childParts);
                    structureDurability = checked(structureDurability + childCore.MaxStructureDurability);
                    structuralValue = checked(structuralValue + childCore.StructuralBaseValue.Value);
                    structuralCount = checked(structuralCount + childCore.StructureHitWeight);
                    foreach (var good in childCore.StructuralGoods)
                    { var id = ((K.GoodId.Voxel)good.Good).Definition; structuralGoods[id] = checked(structuralGoods.GetValueOrDefault(id) + good.Quantity); }
                    foreach (var good in (child as C.DesignEvaluation.Complete)?.Core?.AssemblyGoods ?? []) assemblyGoods[good.Good] = good;
                    if (child is C.DesignEvaluation.Complete) assemblyGoods[childGood] = AssemblyGood(childComposition, childCore);
                }
                else
                {
                    var capability = (C.DesignSpecification.FunctionalComposite)childDraft.Specification;
                    var durability = childCore.MaxStructureDurability;
                    parts.Add(new(number, new K.CoreDefinitionReference.Composite(reference.Definition), childGood, durability,
                        childScene.Voxels.Count(v => !v.Missing), new(BaseValue(childCore)), capability.Behaviors, child is C.DesignEvaluation.Partial));
                    if (child is C.DesignEvaluation.Complete) structureDurability = checked(structureDurability + Contribution(durability));
                }
                returnUnits.Add(new(childGood, 1, reference is null ? childParts.Select(p => p.Id).ToImmutableArray() : [number],
                    reference is null ? childCore.StructuralGoods : [], missing.Skip(missingStart).ToImmutableArray()));
            }
            nestedObjects.Add(new(path, at, orientation, reference is not null
                ? new C.NestedObjectDetails.FunctionalComposite(reference.Definition, number, childScene.RootRotation)
                : childDraft.Specification is C.DesignSpecification.Assembly assembly ? new C.NestedObjectDetails.Assembly(assembly.Rotation)
                : new C.NestedObjectDetails.SceneModel(((C.DesignSpecification.SceneModel)childDraft.Specification).Flatten)));
            foreach (var voxel in childScene.Voxels)
            {
                var position = placement.Map(voxel.At);
                if (!occupied.Add(position)) Invalid(field, "嵌套对象与其他占格重叠。");
                sceneVoxels.Add(voxel with { At = position, Orientation = placement.Rotate(voxel.Orientation),
                    Number = reference is null ? new(voxel.Number.Value + number.Value) : number,
                    Element = reference is null ? Prefix(voxel.Element, index) : element,
                    ObjectPath = path.AddRange(voxel.ObjectPath), Paint = reference?.Paint ?? voxel.Paint });
            }
            if (reference is not null)
                elements.Add(new(number, element, childScene.OccupancyComplete
                    ? new C.ComputedValue<ImmutableArray<C.GridPosition>>.Known(Sorted(childScene.Voxels.Select(v => placement.Map(v.At))))
                    : new C.ComputedValue<ImmutableArray<C.GridPosition>>.Unknown(childDiagnostics), true, child is C.DesignEvaluation.Partial));
            else
            {
                elements.AddRange(childScene.Elements.Select(e => e with { Number = new(e.Number.Value + number.Value), Element = Prefix(e.Element, index),
                    OccupiedCells = e.OccupiedCells is C.ComputedValue<ImmutableArray<C.GridPosition>>.Known known
                        ? new C.ComputedValue<ImmutableArray<C.GridPosition>>.Known(Sorted(known.Value.Select(placement.Map))) : e.OccupiedCells }));
                nestedObjects.AddRange(childScene.NestedObjects.Select(o => o with { NestedItemIndices = path.AddRange(o.NestedItemIndices),
                    Details = o.Details switch { C.NestedObjectDetails.FunctionalComposite f => f with { Number = new(f.Number.Value + number.Value) },
                        C.NestedObjectDetails.MissingFunctionalComposite f => f with { Number = new(f.Number.Value + number.Value) }, _ => o.Details } }));
                locks.AddRange(childScene.EditLocks.Select(l => l with { Element = Prefix(l.Element, index), Diagnostics = l.Diagnostics.Select(d => Prefix(d, index, "/design")).ToImmutableArray() }));
            }
            sweeps.AddRange(childScene.RotationSweeps.Select(s => s with { NestedItemIndices = path.AddRange(s.NestedItemIndices),
                SweptCells = s.SweptCells is C.ComputedValue<ImmutableArray<C.GridPosition>>.Known known
                    ? new C.ComputedValue<ImmutableArray<C.GridPosition>>.Known(Sorted(known.Value.Select(placement.Map))) : s.SweptCells }));
        }
        if (!double.IsFinite(mass)) throw new OverflowException("设计总质量必须为有限值。");
        foreach (var voxel in sceneVoxels)
            if (envelope is not null && !Contains(envelope, voxel.At) && !voxel.ObjectPath.IsEmpty)
                diagnostics.Add(new("design_invalid", C.ContentDiagnosticSeverity.Error, "evaluate", null, null,
                    ElementField(voxel.Element), voxel.Element, "嵌套对象超出尺寸包络。"));
        var occupancyComplete = !elements.Any(e => e.OccupiedCells is C.ComputedValue<ImmutableArray<C.GridPosition>>.Unknown);
        if (!sceneOnly && occupancyComplete && occupied.Count > 0)
        {
            var connected = Connected(occupied, occupied.First());
            foreach (var voxel in sceneVoxels.Where(voxel => !connected.Contains(voxel.At)))
                diagnostics.Add(new("design_invalid", C.ContentDiagnosticSeverity.Error, "evaluate", null, null,
                    ElementField(voxel.Element), voxel.Element, "体素未与设计其余部分六面连通。"));
        }
        var rootRotation = draft.Specification switch { C.DesignSpecification.Assembly a => a.Rotation,
            C.DesignSpecification.FunctionalComposite f => f.Rotation, _ => null };
        if (rootRotation is not null)
        {
            if (!Enum.IsDefined(rootRotation.Axis) || !double.IsFinite(rootRotation.MinimumDegrees) || !double.IsFinite(rootRotation.MaximumDegrees) ||
                rootRotation.MinimumDegrees < -180 || rootRotation.MaximumDegrees > 180 || rootRotation.MinimumDegrees >= rootRotation.MaximumDegrees ||
                !double.IsFinite(rootRotation.SpeedDegreesPerSecond) || rootRotation.SpeedDegreesPerSecond <= 0)
                Invalid("/appearance/behaviors/rotation", "转动轴、角度范围或速度无效。");
            else
            {
                var reasons = diagnostics.Where(d => d.Code == "reference_missing").ToImmutableArray();
                var body = occupied.Concat(sweeps.SelectMany(s => s.SweptCells is C.ComputedValue<ImmutableArray<C.GridPosition>>.Known k ? k.Value : []));
                sweeps.Insert(0, new([], occupancyComplete
                    ? new C.ComputedValue<ImmutableArray<C.GridPosition>>.Known(RotationSweep.Calculate(body, rootRotation))
                    : new C.ComputedValue<ImmutableArray<C.GridPosition>>.Unknown(reasons),
                    new C.ComputedValue<ImmutableArray<C.GridPosition>>.Known([])));
            }
        }
        for (var index = 0; depth == 1 && index < sweeps.Count; index++)
        {
            var sweep = sweeps[index];
            var subtree = sceneVoxels.Where(v => StartsWith(v.ObjectPath, sweep.NestedItemIndices)).Select(v => v.At).ToHashSet();
            var unknownInside = elements.Any(e => e.OccupiedCells is C.ComputedValue<ImmutableArray<C.GridPosition>>.Unknown && StartsWith(ObjectPath(e.Element), sweep.NestedItemIndices));
            var reasons = diagnostics.Where(d => d.Code == "reference_missing").ToImmutableArray();
            C.ComputedValue<ImmutableArray<C.GridPosition>> conflicts;
            if (unknownInside || sweep.SweptCells is C.ComputedValue<ImmutableArray<C.GridPosition>>.Unknown)
                sweep = sweep with { SweptCells = new C.ComputedValue<ImmutableArray<C.GridPosition>>.Unknown(reasons), ConflictCells = new C.ComputedValue<ImmutableArray<C.GridPosition>>.Unknown(reasons) };
            else
            {
                var range = ((C.ComputedValue<ImmutableArray<C.GridPosition>>.Known)sweep.SweptCells).Value;
                var conflictCells = sweep.NestedItemIndices.IsEmpty ? [] : Sorted(range.Where(p => !subtree.Contains(p) &&
                    (occupied.Contains(p) || envelope is not null && !Contains(envelope, p))));
                conflicts = !occupancyComplete && !sweep.NestedItemIndices.IsEmpty
                    ? new C.ComputedValue<ImmutableArray<C.GridPosition>>.Unknown(reasons)
                    : new C.ComputedValue<ImmutableArray<C.GridPosition>>.Known(conflictCells);
                sweep = sweep with { ConflictCells = conflicts };
                if (conflicts is C.ComputedValue<ImmutableArray<C.GridPosition>>.Known { Value.IsEmpty: false })
                {
                    var path = sweep.NestedItemIndices;
                    var nested = nestedObjects.Single(o => o.NestedItemIndices.SequenceEqual(path));
                    C.DesignElementPath? element = nested.Details is C.NestedObjectDetails.FunctionalComposite
                        ? new C.DesignElementPath.FunctionalComposite(path.RemoveAt(path.Length - 1), path[^1]) : null;
                    var field = string.Concat(path.Select((item, level) => (level == 0 ? "" : "/design") + $"/layout/composites/{item}"));
                    diagnostics.Add(new("design_invalid", C.ContentDiagnosticSeverity.Error, "evaluate", null, null,
                        field, element, "转动扫过其他占格或超出尺寸包络。"));
                }
            }
            sweeps[index] = sweep;
        }
        C.GridBounds? bounds = occupied.Count == 0 ? null : new(
            new(occupied.Min(p => p.X), occupied.Min(p => p.Y), occupied.Min(p => p.Z)),
            new(occupied.Max(p => p.X), occupied.Max(p => p.Y), occupied.Max(p => p.Z)));
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
            C.DesignSpecification.Assembly => new K.DesignKind.LargeEquipment(new K.GoodId.Assembly(composition)),
            C.DesignSpecification.FunctionalComposite => new K.DesignKind.LargeEquipment(new K.GoodId.Composite(compositeIdentity!.Value)),
            _ => null
        };
        var valid = diagnostics.Count == 0;
        var projection = kind is null ? null : new K.DesignProjection(
            design, composition, kind, parts.ToImmutable(), structureDurability, structuralCount,
            mass, new(structuralValue), Goods(structuralGoods), Goods(bom), bom.Values.Sum(), returnUnits.ToImmutable(), valid, missing.ToImmutable());
        if (draft.Specification is C.DesignSpecification.Assembly && missing.Count == 0)
            assemblyGoods[new K.GoodId.Assembly(composition)] = AssemblyGood(composition, projection!);
        var core = projection is null ? null : new K.DesignInput(content.Core.Identity, projection,
            assemblyGoods.Values.OrderBy(g => GoodKey(g.Good), StringComparer.Ordinal).ToImmutableArray());
        var scene = new C.DesignSceneProjection(design, bounds, envelope, occupancyComplete, rootRotation,
            (draft.Specification as C.DesignSpecification.SceneModel)?.Flatten,
            sceneVoxels.ToImmutable(), elements.ToImmutable(), nestedObjects.ToImmutable(), sweeps.ToImmutable(), locks.ToImmutable());
        if (missing.Count > 0)
        {
            var reasons = diagnostics.Where(d => d.Code == "reference_missing").ToImmutableArray();
            return new C.DesignEvaluation.Partial(draft,
                new(new C.ComputedValue<double>.Unknown(reasons), new C.ComputedValue<K.Money>.Unknown(reasons),
                    new C.ComputedValue<long>.Unknown(reasons), new C.ComputedValue<ImmutableArray<K.PartProjection>>.Unknown(reasons),
                    new C.ComputedValue<ImmutableArray<K.GoodsQuantity>>.Unknown(reasons), occupancyComplete ? new C.ComputedValue<int>.Known((int)vehicleLength) : new C.ComputedValue<int>.Unknown(reasons)),
                new(mass, new(structuralValue), structureDurability, parts.ToImmutable(), Goods(bom)), core?.Design, scene,
                diagnostics.Select(d => d with { Subject = new C.ContentReference.Design(design) }).ToImmutableArray());
        }
        return new C.DesignEvaluation.Complete(design, composition, draft, core, scene, valid,
            diagnostics.Select(diagnostic => diagnostic with { Subject = new C.ContentReference.Design(design) }).ToImmutableArray());
    }

    private static ImmutableArray<K.GoodsQuantity> Goods(Dictionary<K.VoxelDefinitionId, long> counts) =>
        counts.OrderBy(item => item.Key.Value, StringComparer.Ordinal)
            .Select(item => new K.GoodsQuantity(new K.GoodId.Voxel(item.Key), item.Value)).ToImmutableArray();

    private static ImmutableArray<K.GoodsQuantity> Goods(Dictionary<K.GoodId, long> counts) =>
        counts.OrderBy(item => GoodKey(item.Key), StringComparer.Ordinal).Select(item => new K.GoodsQuantity(item.Key, item.Value)).ToImmutableArray();

    internal static string GoodKey(K.GoodId good) => good switch { K.GoodId.Voxel v => "0:" + v.Definition.Value,
        K.GoodId.Composite c => "1:" + c.Definition.Value, K.GoodId.Assembly a => "2:" + a.Composition.Value, _ => throw new ArgumentOutOfRangeException(nameof(good)) };
    private static long Contribution(long durability) => Math.Max(1, durability / 2 + durability % 2);
    internal static long BaseValue(K.DesignProjection design) => checked(design.StructuralBaseValue.Value + design.Parts.Sum(p => p.BaseValue.Value));
    internal static K.GoodDefinitionProjection AssemblyGood(K.CompositionHash composition, K.DesignProjection design)
    {
        var value = BaseValue(design);
        return new(new K.GoodId.Assembly(composition), new(ContentHash.FromCanonicalJson(w => JsonSerializer.Serialize(w, new
        { composition = composition.Value, design.MaxStructureDurability, design.StructureHitWeight, design.MassTons,
            value,
            parts = design.Parts.Select(p => new { p.Definition, p.MaxDurability, p.HitWeight, p.BaseValue, capabilities = DesignTraversal.Behaviors(p.Capabilities) }),
            bom = design.BillOfMaterials.Select(q => new { good = GoodKey(q.Good), q.Quantity }), design.ReturnUnits }))),
            new(value), design.MassTons, false, null);
    }

    private static bool StartsWith(ImmutableArray<int> path, ImmutableArray<int> prefix) => path.Length >= prefix.Length && path.AsSpan()[..prefix.Length].SequenceEqual(prefix.AsSpan());
    private static ImmutableArray<C.GridPosition> Sorted(IEnumerable<C.GridPosition> cells) => cells.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z).ToImmutableArray();
    private static ImmutableArray<int> ObjectPath(C.DesignElementPath element) => element switch
    { C.DesignElementPath.Cell c => c.NestedItemIndices, C.DesignElementPath.FunctionalComposite c => c.NestedItemIndices.Add(c.NestedItemIndex), _ => throw new ArgumentOutOfRangeException(nameof(element)) };
    private static C.DesignElementPath Prefix(C.DesignElementPath element, int index) => element switch
    { C.DesignElementPath.Cell c => c with { NestedItemIndices = ImmutableArray.Create(index).AddRange(c.NestedItemIndices) },
        C.DesignElementPath.FunctionalComposite c => c with { NestedItemIndices = ImmutableArray.Create(index).AddRange(c.NestedItemIndices) }, _ => throw new ArgumentOutOfRangeException(nameof(element)) };
    private static C.ContentDiagnostic Prefix(C.ContentDiagnostic diagnostic, int index, string kind) => diagnostic with
    { Field = $"/layout/composites/{index}" + kind + diagnostic.Field, Element = diagnostic.Element is { } e ? Prefix(e, index) : null };
    private static string ElementField(C.DesignElementPath element)
    {
        var path = element is C.DesignElementPath.Cell c ? c.NestedItemIndices : ((C.DesignElementPath.FunctionalComposite)element).NestedItemIndices;
        var field = string.Concat(path.Select(i => $"/layout/composites/{i}/design"));
        return field + (element is C.DesignElementPath.Cell cell ? $"/layout/cells/{cell.CellIndex}" : $"/layout/composites/{((C.DesignElementPath.FunctionalComposite)element).NestedItemIndex}");
    }

    private static bool Allowed(C.VoxelOrientation orientation, C.AllowedVoxelOrientations allowed) =>
        Enum.IsDefined(orientation) && (allowed == C.AllowedVoxelOrientations.All24 ||
        orientation == C.VoxelOrientation.PxPy || allowed == C.AllowedVoxelOrientations.FourWay &&
        orientation is C.VoxelOrientation.PzPy or C.VoxelOrientation.NxPy or C.VoxelOrientation.NzPy);

    private static C.GridBounds? Envelope(C.DesignSpecification specification)
    {
        if (specification is C.DesignSpecification.Building building) return PlotRules.Envelope(building.PlotSize!.Value);
        var dimensions = specification switch
        {
            C.DesignSpecification.Vehicle vehicle => vehicle.Size switch
            {
                K.SizeClass.Small => (7, 52, 9), K.SizeClass.Medium => (10, 64, 13),
                K.SizeClass.Large => (14, 80, 18), K.SizeClass.ExtraLarge => (20, 104, 26),
                K.SizeClass.Titan => (30, 136, 38), _ => throw new ArgumentOutOfRangeException(nameof(specification))
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

    internal static string Wire(Enum value) => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
