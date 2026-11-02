using System.Collections.Immutable;
using Train.Content.Loading;
using Train.Content.Authoring;
using C = Train.Content.Contracts;

namespace Train.Content;

// Decision note: 2026-10-31-full-screen-authoring-mode
internal sealed partial class InitialContentFactory
{
    public C.ContentResult<ImmutableArray<C.AuthoringReferenceChoice>> QueryAuthoringReferences(
        C.IAuthoringPackage source, C.IContentView content, ImmutableArray<C.PackageEdit> edits)
    {
        var (package, view) = AuthoringArguments(source, content, edits);
        return EditResult(() =>
        {
            package = AuthoringSource(package, view, edits);
            var allowed = package.Metadata.Dependencies.Select(dependency => dependency.Package).ToHashSet();
            return new C.ContentResult<ImmutableArray<C.AuthoringReferenceChoice>>.Success(package.References(view)
                .Concat(view.AuthoringReferences().Where(choice => ReferenceId(choice.Reference).Split(':')[0] != package.Package.Value
                    && allowed.Contains(new(ReferenceId(choice.Reference).Split(':')[0]))))
                .OrderBy(choice => choice.Kind).ThenBy(choice => ReferenceId(choice.Reference), StringComparer.Ordinal).ToImmutableArray());
        });
    }

    public C.ContentResult<ImmutableArray<C.ContentDiagnostic>> ValidateAuthoringDefinition(
        C.IAuthoringPackage source, C.IContentView content, ImmutableArray<C.PackageEdit> edits,
        C.AuthoringDefinitionAddress definition)
    {
        var (package, view) = AuthoringArguments(source, content, edits);
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Kind is not (C.DefinitionKind.Voxel or C.DefinitionKind.Blueprint or C.DefinitionKind.SettlementType or C.DefinitionKind.SettlementTemplate or C.DefinitionKind.LairType))
            throw new ArgumentException("此定义种类没有创作表单。", nameof(definition));
        return EditResult<ImmutableArray<C.ContentDiagnostic>>(() =>
        {
            package = AuthoringSource(package, view, edits);
            var diagnostics = AuthoringValidation.Validate(package, view, definition);
            return diagnostics.Any(diagnostic => diagnostic.Severity == C.ContentDiagnosticSeverity.Error)
                ? new C.ContentResult<ImmutableArray<C.ContentDiagnostic>>.Failure(diagnostics)
                : new C.ContentResult<ImmutableArray<C.ContentDiagnostic>>.Success(diagnostics);
        });
    }

    private static AuthoringPackage AuthoringSource(AuthoringPackage package, ContentView view, ImmutableArray<C.PackageEdit> edits)
    {
        package = package.Overlay(edits);
        var parser = package.ParsingPackage();
        if (PackageLoader.FirstDependencyProblem(parser, view.Packages) is { } problem)
            throw new DefinitionFormatException(problem.Field, problem.Message, problem.Code);
        return package;
    }

    private static (AuthoringPackage, ContentView) AuthoringArguments(C.IAuthoringPackage source, C.IContentView content, ImmutableArray<C.PackageEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(content);
        if (source is not AuthoringPackage package) throw new ArgumentException("需要本内容实现产生的固定源包。", nameof(source));
        if (content is not ContentView view) throw new ArgumentException("需要本内容实现产生的固定内容视图。", nameof(content));
        if (edits.IsDefault || edits.Any(edit => edit is null)) throw new ArgumentException("编辑数组必须初始化且不能含空项。", nameof(edits));
        return (package, view);
    }

    internal static string ReferenceId(C.ContentReference reference) => reference switch
    {
        C.ContentReference.Core core => ContentView.DefinitionId(core.Definition),
        C.ContentReference.Blueprint blueprint => blueprint.Address.Id.Value,
        _ => throw new ArgumentException("创作候选必须是内容定义引用。", nameof(reference))
    };
}
