using Train.Content.Loading;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Designs;

internal static partial class DesignEvaluator
{
    internal static K.DesignHash Identity(C.DesignDraft draft) => new(DesignTraversal.Hash(DesignTraversal.Normalize(draft), false));

    internal static C.DesignEvaluation Evaluate(ContentView content, ParsedBlueprint blueprint) =>
        Evaluate(content, blueprint.Definition, compositeIdentity: blueprint.Definition.Specification is C.DesignSpecification.FunctionalComposite
            ? new K.CompositeDefinitionId(blueprint.Address.Id.Value) : null);
}
