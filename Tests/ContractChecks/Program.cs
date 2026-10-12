// 契约公开面检查工具：从源码声明与编译产物提取调用方可见的形状。
// 与已确认基线比较，并检查公开签名中的 Train 类型是否属于允许的契约依赖闭包。
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length != 4 || args[0] is not ("snapshot" or "verify"))
{
    Console.Error.WriteLine("usage: Train.ContractChecks snapshot|verify <repository> <configuration> <baseline>");
    return 2;
}

var root = Path.GetFullPath(args[1]);
var configuration = args[2];
var baseline = Path.GetFullPath(args[3]);
using var rules = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "scripts", "checks", "project-boundaries.json")));
var projects = rules.RootElement.GetProperty("projects").EnumerateObject()
    .ToDictionary(project => project.Name, project => project.Value, StringComparer.Ordinal);
var contractProjects = projects.Keys.Where(name => name.EndsWith(".Contracts", StringComparison.Ordinal)).ToArray();
var paths = contractProjects.Select(name => AssemblyPath(name)).ToArray();
if (paths.Any(path => !File.Exists(path)))
{
    foreach (var path in paths.Where(path => !File.Exists(path))) Console.Error.WriteLine($"missing assembly: {path}");
    return 2;
}

var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
var implementations = contractProjects.Select(name => name[..^".Contracts".Length])
    .Where(projects.ContainsKey).Select(name => AssemblyPath(name)).Where(File.Exists);
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

// 源码声明保留参数名、默认值和 using；元数据补充实际类型所有权与枚举数值。
foreach (var owner in contractProjects)
{
    var trees = Directory.GetFiles(Path.GetDirectoryName(Path.Combine(root, projects[owner].GetProperty("path").GetString()!))!, "*.cs", SearchOption.AllDirectories)
        .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                       !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
        .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path)).ToArray();
    // 符号合并 partial 的可见性；单个声明可以省略 public，仍然属于公开类型。
    var sourceCompilation = CSharpCompilation.Create(owner, trees,
        references.Where(reference => Path.GetFileNameWithoutExtension(reference.FilePath) != owner));
    foreach (var tree in trees)
    {
        var syntax = tree.GetRoot();
        var model = sourceCompilation.GetSemanticModel(tree);
        foreach (var directive in syntax.DescendantNodes().OfType<UsingDirectiveSyntax>())
            lines.Add($"{owner}|using|{Normalize(directive.ToString())}");
        foreach (var declaration in syntax.DescendantNodes().OfType<DelegateDeclarationSyntax>())
        {
            if (!VisibleSymbol(model.GetDeclaredSymbol(declaration))) continue;
            lines.Add($"{owner}|delegate|{Scope(declaration)}|{Normalize(declaration.ToString())}");
        }
        foreach (var type in syntax.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            if (!VisibleSymbol(model.GetDeclaredSymbol(type))) continue;
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
    var allowed = new HashSet<string>(StringComparer.Ordinal);
    AddAllowed(owner);
    Visit(assembly.GlobalNamespace);

    void AddAllowed(string name)
    {
        if (!allowed.Add(name)) return;
        foreach (var dependency in projects[name].GetProperty("references").EnumerateArray())
            AddAllowed(dependency.GetString()!);
    }

    // 只走调用方可见成员，保留显式签名及位置 record 属性中的类型引用。
    void Visit(INamespaceOrTypeSymbol container)
    {
        foreach (var member in container.GetMembers())
        {
            if (member is INamespaceSymbol ns) { Visit(ns); continue; }
            if (member.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal)) continue;
            // 位置 record 的合成属性承载显式参数类型；其他合成成员不重复检查。
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

    // 递归检查组合类型，使数组、泛型和约束不能藏入允许依赖闭包之外的 Train.* 程序集类型，包括契约程序集。
    void CheckType(ITypeSymbol type, string signature)
    {
        switch (type)
        {
            case IArrayTypeSymbol array: CheckType(array.ElementType, signature); break;
            case IPointerTypeSymbol pointer: CheckType(pointer.PointedAtType, signature); break;
            case INamedTypeSymbol named:
                var source = named.ContainingAssembly?.Name;
                if (source is not null && source.StartsWith("Train.", StringComparison.Ordinal) &&
                    !allowed.Contains(source))
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

string AssemblyPath(string name) => Path.Combine(root,
    Path.GetDirectoryName(projects[name].GetProperty("path").GetString()!)!,
    "bin", configuration, "net10.0", $"{name}.dll");

// 外层 internal 类型中的 public 成员不属于调用方可见的公开表面。
static bool VisibleSymbol(ISymbol? symbol)
{
    for (var current = symbol; current is not null && current is not INamespaceSymbol; current = current.ContainingType)
        if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal))
            return false;
    return symbol is not null;
}

static bool Visible(SyntaxTokenList modifiers) => modifiers.Any(token =>
    token.IsKind(SyntaxKind.PublicKeyword) || token.IsKind(SyntaxKind.ProtectedKeyword));

// 声明的范围包含命名空间和所有外层类型，避免不同嵌套位置的声明相互抵消。
static string Scope(SyntaxNode declaration)
{
    var namespaces = declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(item => item.Name.ToString());
    var types = declaration.Ancestors().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(item => item.Identifier.ValueText);
    var name = declaration switch
    {
        BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
        DelegateDeclarationSyntax @delegate => @delegate.Identifier.ValueText,
        _ => throw new ArgumentException("Expected a type or delegate declaration", nameof(declaration))
    };
    return string.Join(".", namespaces.Concat(types).Append(name));
}

static string Normalize(string text) => Regex.Replace(text.Trim(), @"\s+", " ");

// 枚举调用方签名中可见的类型位置，包括泛型约束和继承关系。
static IEnumerable<ITypeSymbol> SignatureTypes(ISymbol symbol)
{
    switch (symbol)
    {
        case INamedTypeSymbol type:
            // 委托 Invoke 是编译器生成的，但其参数和返回类型就是委托的公开契约。
            if (type.DelegateInvokeMethod is { } invoke)
                foreach (var item in SignatureTypes(invoke)) yield return item;
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
