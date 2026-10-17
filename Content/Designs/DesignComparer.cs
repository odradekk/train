using System.Collections.Immutable;
using Train.Content.Loading;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Designs;

internal static class DesignComparer
{
    internal static C.ContentResult<C.DesignChangeEvaluation> Compare(ContentView content, K.DesignHash current, C.DesignDraft target)
    {
        if (target.Specification is C.DesignSpecification.FunctionalComposite)
            return Failure("design_invalid", "玩家设计不能声明功能复合体行为。");
        if (content.FindDesign(current) is not C.ContentLookup<C.DesignEvaluation>.Found found)
            return new C.ContentResult<C.DesignChangeEvaluation>.Failure([new("reference_missing", C.ContentDiagnosticSeverity.Error,
                "compare", new C.ContentReference.Design(current), null, "/current", null, "当前设计不存在。")]);
        if (found.Value is not C.DesignEvaluation.Complete { Core: not null } before)
            return Incomplete(found.Value);
        var evaluated = DesignEvaluator.Evaluate(content, target);
        if (evaluated is not C.DesignEvaluation.Complete { Core: not null } after)
            return Incomplete(evaluated);
        if (!after.IsValid)
            return new C.ContentResult<C.DesignChangeEvaluation>.Failure(after.Diagnostics);
        if (before.Definition.Specification.GetType() != after.Definition.Specification.GetType())
            return Failure("design_invalid", "当前设计和目标设计的类别不兼容。");

        var oldAtoms = Atoms(before);
        var newAtoms = Atoms(after).ToDictionary(a => (a.At, a.Nested));
        var retained = ImmutableArray.CreateBuilder<C.DesignElementCorrespondence>();
        var removed = ImmutableArray.CreateBuilder<K.DesignReturnUnit>();
        var added = new Dictionary<K.GoodId, long>();
        long changed = 0;
        foreach (var old in oldAtoms)
        {
            if (!newAtoms.Remove((old.At, old.Nested), out var next))
            { removed.Add(old.Return); changed++; continue; }
            if (old.Return.Good != next.Return.Good)
            { removed.Add(old.Return); Add(next); changed++; continue; }
            for (var i = 0; i < old.Parts.Length; i++)
                retained.Add(new(new(old.Parts[i].Number, next.Parts[i].Number), old.Parts[i].Element, next.Parts[i].Element));
        }
        foreach (var atom in newAtoms.Values) { Add(atom); changed++; }
        var elements = retained.ToImmutable();
        var projection = new K.DesignChangeProjection(before.Design, before.Composition, after.Core,
            elements.Select(e => e.Parts).ToImmutableArray(),
            added.OrderBy(g => DesignEvaluator.GoodKey(g.Key), StringComparer.Ordinal).Select(g => new K.GoodsQuantity(g.Key, g.Value)).ToImmutableArray(),
            removed.ToImmutable(), changed);
        return new C.ContentResult<C.DesignChangeEvaluation>.Success(new(projection, elements, before.Composition == after.Composition, after.Diagnostics));

        void Add(Atom atom) => added[atom.Return.Good] = checked(added.GetValueOrDefault(atom.Return.Good) + atom.Return.Quantity);
    }

    private sealed record Atom(C.GridPosition At, bool Nested, K.DesignReturnUnit Return, ImmutableArray<C.DesignElementProjection> Parts);

    private static IEnumerable<Atom> Atoms(C.DesignEvaluation.Complete evaluation)
    {
        var core = evaluation.Core!.Design;
        var ids = core.Parts.Select(p => p.Id).ToHashSet();
        var goods = core.BillOfMaterials.Select(g => g.Good).ToHashSet();
        var rootCells = new Dictionary<int, C.DesignElementProjection>();
        var nested = new Dictionary<int, List<C.DesignElementProjection>>();
        foreach (var element in evaluation.Scene.Elements)
        {
            if (!ids.Contains(element.Number)) continue;
            var path = element.Element switch
            { C.DesignElementPath.Cell c => c.NestedItemIndices, C.DesignElementPath.FunctionalComposite c => c.NestedItemIndices, _ => [] };
            if (path.IsEmpty && element.Element is C.DesignElementPath.Cell cell) rootCells[cell.CellIndex] = element;
            else
            {
                var index = path.IsEmpty ? ((C.DesignElementPath.FunctionalComposite)element.Element).NestedItemIndex : path[0];
                if (!nested.TryGetValue(index, out var list)) nested[index] = list = [];
                list.Add(element);
            }
        }
        foreach (var (cell, index) in DesignTraversal.Cells(evaluation.Definition))
        {
            var good = new K.GoodId.Voxel(evaluation.Definition.Layout.Palette[cell.PaletteIndex].Voxel);
            if (!goods.Contains(good)) continue;
            var parts = rootCells.TryGetValue(index, out var element) ? ImmutableArray.Create(element) : [];
            yield return new(cell.At, false, new(good, 1, parts.Select(p => p.Number).ToImmutableArray(), parts.IsEmpty ? [new(good, 1)] : [], []), parts);
        }
        var returns = core.ReturnUnits.GroupBy(r => r.Good).ToDictionary(g => g.Key, g => new Queue<K.DesignReturnUnit>(g));
        foreach (var (item, index) in DesignTraversal.Items(evaluation.Definition))
        {
            K.GoodId good = item is C.NestedDesignItem.FunctionalCompositeReference reference
                ? new K.GoodId.Composite(reference.Definition)
                : new K.GoodId.Assembly(new(DesignTraversal.Hash(DesignTraversal.Copy(item)!, true)));
            if (!returns.TryGetValue(good, out var queue) || !queue.TryDequeue(out var unit)) continue;
            var parts = nested.TryGetValue(index, out var list) ? list.OrderBy(p => p.Number.Value).ToImmutableArray() : [];
            yield return new(DesignTraversal.At(item), true, unit, parts);
        }
    }

    private static C.ContentResult<C.DesignChangeEvaluation> Incomplete(C.DesignEvaluation evaluation)
    {
        var diagnostics = evaluation switch
        { C.DesignEvaluation.Partial p => p.Diagnostics, C.DesignEvaluation.Failed f => f.Diagnostics, C.DesignEvaluation.Complete c => c.Diagnostics, _ => [] };
        var missing = diagnostics.Where(d => d.Code == "reference_missing").ToImmutableArray();
        return !missing.IsEmpty ? new C.ContentResult<C.DesignChangeEvaluation>.Failure(missing)
            : Failure("design_invalid", "设计不完整或不能形成核心投影。");
    }

    private static C.ContentResult<C.DesignChangeEvaluation> Failure(string code, string message) =>
        new C.ContentResult<C.DesignChangeEvaluation>.Failure([new(code, C.ContentDiagnosticSeverity.Error, "compare", null, null, "/design", null, message)]);
}
