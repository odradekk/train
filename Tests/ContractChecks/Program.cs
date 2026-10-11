using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Globalization;
using System.Text.RegularExpressions;

if (args.Length != 4 || args[0] is not ("snapshot" or "verify"))
{
    Console.Error.WriteLine("usage: Train.ContractChecks snapshot|verify <repository> <configuration> <baseline>");
    return 2;
}

var root = Path.GetFullPath(args[1]);
var configuration = args[2];
var baseline = Path.GetFullPath(args[3]);
var contractProjects = new[] { "Core", "Content", "Scenes" };
var paths = contractProjects.Select(module => Path.Combine(root, "Contracts", module, "bin",
    configuration, "net10.0", $"Train.{module}.Contracts.dll")).ToArray();
if (paths.Any(path => !File.Exists(path)))
{
    foreach (var path in paths.Where(path => !File.Exists(path))) Console.Error.WriteLine($"missing assembly: {path}");
    return 2;
}

var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
var implementations = contractProjects.Select(module => Path.Combine(root, module, "bin", configuration,
    "net10.0", $"Train.{module}.dll")).Where(File.Exists);
var references = platform.Concat(paths).Concat(implementations).Distinct()
    .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
var compilation = CSharpCompilation.Create("ContractSurface", references: references);
var format = SymbolDisplayFormat.FullyQualifiedFormat
    .WithMemberOptions(SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType |
        SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeModifiers |
        SymbolDisplayMemberOptions.IncludeRef | SymbolDisplayMemberOptions.IncludeConstantValue)
    .WithGenericsOptions(SymbolDisplayGenericsOptions.IncludeTypeParameters |
        SymbolDisplayGenericsOptions.IncludeTypeConstraints)
    .WithParameterOptions(SymbolDisplayParameterOptions.IncludeType |
        SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeParamsRefOut |
        SymbolDisplayParameterOptions.IncludeDefaultValue)
    .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier |
        SymbolDisplayMiscellaneousOptions.UseSpecialTypes);
var lines = new SortedSet<string>(StringComparer.Ordinal);
var failures = new List<string>();

foreach (var module in contractProjects)
{
    var owner = $"Train.{module}.Contracts";
    foreach (var path in Directory.GetFiles(Path.Combine(root, "Contracts", module), "*.cs", SearchOption.AllDirectories)
        .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                       !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
    {
        var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();
        foreach (var directive in syntax.DescendantNodes().OfType<UsingDirectiveSyntax>())
            lines.Add($"{owner}|using|{Normalize(directive.ToString())}");
        foreach (var type in syntax.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            if (!Visible(type.Modifiers)) continue;
            var scope = Scope(type);
            var declaration = type.OpenBraceToken.IsKind(SyntaxKind.OpenBraceToken)
                ? type.ToString()[..(type.OpenBraceToken.SpanStart - type.SpanStart)]
                : type.ToString();
            lines.Add($"{owner}|type|{scope}|{Normalize(declaration)}");
            if (type is not TypeDeclarationSyntax members) continue;
            foreach (var member in members.Members)
            {
                if (member is BaseTypeDeclarationSyntax) continue;
                if (member is not (MethodDeclarationSyntax or ConstructorDeclarationSyntax or PropertyDeclarationSyntax
                    or IndexerDeclarationSyntax or FieldDeclarationSyntax or EventDeclarationSyntax
                    or EventFieldDeclarationSyntax or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax
                    or DelegateDeclarationSyntax)) continue;
                var modifiers = member switch
                {
                    BaseMethodDeclarationSyntax method => method.Modifiers,
                    BasePropertyDeclarationSyntax property => property.Modifiers,
                    BaseFieldDeclarationSyntax field => field.Modifiers,
                    DelegateDeclarationSyntax declarationNode => declarationNode.Modifiers,
                    _ => default
                };
                if (type is not InterfaceDeclarationSyntax && !Visible(modifiers)) continue;
                var text = member is BaseMethodDeclarationSyntax methodNode && methodNode.Body is not null
                    ? member.ToString()[..(methodNode.Body.SpanStart - member.SpanStart)]
                    : member.ToString();
                lines.Add($"{owner}|member|{scope}|{Normalize(text)}");
            }
        }
    }
}

foreach (var path in paths)
{
    var reference = references.Single(item => item.FilePath == path);
    var assembly = (IAssemblySymbol)compilation.GetAssemblyOrModuleSymbol(reference)!;
    var owner = assembly.Name;
    var allowed = owner switch
    {
        "Train.Core.Contracts" => new[] { owner },
        "Train.Content.Contracts" => new[] { owner, "Train.Core.Contracts" },
        "Train.Scenes.Contracts" => new[] { owner, "Train.Core.Contracts", "Train.Content.Contracts" },
        _ => throw new InvalidOperationException($"Unexpected contract assembly: {owner}")
    };
    Visit(assembly.GlobalNamespace);

    void Visit(INamespaceOrTypeSymbol container)
    {
        foreach (var member in container.GetMembers())
        {
            if (member is INamespaceSymbol ns) { Visit(ns); continue; }
            if (member.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal)) continue;
            // Positional record properties represent authored constructor parameters. Other synthesized
            // record methods add thousands of lines without adding independent contract decisions.
            if (member.IsImplicitlyDeclared && member is not IPropertySymbol) continue;
            var signature = member.ToDisplayString(format);
            foreach (var type in SignatureTypes(member)) CheckType(type, signature);
            if (member is IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } field)
                lines.Add($"{owner}|enum|{field.ContainingType.ToDisplayString(format)}|{field.Name}|{Convert.ToString(field.ConstantValue, CultureInfo.InvariantCulture)}");
            if (member is INamedTypeSymbol named)
            {
                Visit(named);
            }
        }
    }

    void CheckType(ITypeSymbol type, string signature)
    {
        switch (type)
        {
            case IArrayTypeSymbol array: CheckType(array.ElementType, signature); break;
            case IPointerTypeSymbol pointer: CheckType(pointer.PointedAtType, signature); break;
            case INamedTypeSymbol named:
                var source = named.ContainingAssembly?.Name;
                if (source is not null && source.StartsWith("Train.", StringComparison.Ordinal) &&
                    !allowed.Contains(source, StringComparer.Ordinal))
                    failures.Add($"type leak: {owner}: {signature} uses {source}");
                foreach (var argument in named.TypeArguments) CheckType(argument, signature);
                break;
            case ITypeParameterSymbol parameter:
                foreach (var constraint in parameter.ConstraintTypes) CheckType(constraint, signature);
                break;
        }
    }
}

if (args[0] == "snapshot")
{
    Directory.CreateDirectory(Path.GetDirectoryName(baseline)!);
    File.WriteAllLines(baseline, lines);
}
else
{
    if (!File.Exists(baseline)) { Console.Error.WriteLine($"missing baseline: {baseline}"); return 2; }
    var expected = new HashSet<string>(File.ReadAllLines(baseline), StringComparer.Ordinal);
    foreach (var item in expected.Except(lines).Take(30)) failures.Add("removed public API: " + item);
    foreach (var item in lines.Except(expected).Take(30)) failures.Add("added public API: " + item);
    if (expected.Count != lines.Count) failures.Add($"public API counts: baseline={expected.Count}, actual={lines.Count}");
}
foreach (var failure in failures) Console.Error.WriteLine(failure);
Console.WriteLine($"contract surface: declarations={lines.Count}, errors={failures.Count}");
return failures.Count == 0 ? 0 : 1;

static bool Visible(SyntaxTokenList modifiers) => modifiers.Any(token =>
    token.IsKind(SyntaxKind.PublicKeyword) || token.IsKind(SyntaxKind.ProtectedKeyword));

static string Scope(BaseTypeDeclarationSyntax type)
{
    var namespaces = type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(item => item.Name.ToString());
    var types = type.Ancestors().OfType<BaseTypeDeclarationSyntax>().Reverse().Append(type).Select(item => item.Identifier.ValueText);
    return string.Join(".", namespaces.Concat(types));
}

static string Normalize(string text) => Regex.Replace(text.Trim(), @"\s+", " ");

static IEnumerable<ITypeSymbol> SignatureTypes(ISymbol symbol)
{
    switch (symbol)
    {
        case INamedTypeSymbol type:
            if (type.BaseType is not null) yield return type.BaseType;
            foreach (var item in type.Interfaces) yield return item;
            foreach (var parameter in type.TypeParameters)
                foreach (var constraint in parameter.ConstraintTypes) yield return constraint;
            break;
        case IMethodSymbol method:
            yield return method.ReturnType;
            foreach (var parameter in method.Parameters) yield return parameter.Type;
            foreach (var parameter in method.TypeParameters)
                foreach (var constraint in parameter.ConstraintTypes) yield return constraint;
            break;
        case IPropertySymbol property:
            yield return property.Type;
            foreach (var parameter in property.Parameters) yield return parameter.Type;
            break;
        case IFieldSymbol field: yield return field.Type; break;
        case IEventSymbol eventSymbol: yield return eventSymbol.Type; break;
    }
}
