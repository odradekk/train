using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Train.ContractChecks;

internal static class ApiSurface
{
    internal static int Run(string command, string root, string configuration, string baseline, IReadOnlyDictionary<string, ProjectRule> projects)
    {
        var contractProjects = projects.Keys.Where(name => name.EndsWith(".Contracts", StringComparison.Ordinal)).ToArray();
        var paths = contractProjects.Select(name => AssemblyPath(name)).ToArray();
        if (paths.Any(path => !File.Exists(path)))
        {
            foreach (var path in paths.Where(path => !File.Exists(path))) Console.Error.WriteLine($"missing assembly: {path}");
            return 1;
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

        foreach (var owner in contractProjects)
        {
            var trees = Directory.GetFiles(Path.GetDirectoryName(Path.Combine(root, projects[owner].Path))!, "*.cs", SearchOption.AllDirectories)
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
                foreach (var dependency in projects[name].References)
                    AddAllowed(dependency);
            }

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

        if (command == "snapshot")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(baseline)!);
            File.WriteAllLines(baseline, lines);
        }
        else
        {
            if (!File.Exists(baseline)) { Console.Error.WriteLine($"missing baseline: {baseline}"); return 1; }
            var expected = new HashSet<string>(File.ReadAllLines(baseline), StringComparer.Ordinal);
            foreach (var item in expected.Except(lines).Take(30)) failures.Add("removed public API: " + item);
            foreach (var item in lines.Except(expected).Take(30)) failures.Add("added public API: " + item);
            if (expected.Count != lines.Count) failures.Add($"public API counts: baseline={expected.Count}, actual={lines.Count}");
        }
        foreach (var failure in failures) Console.Error.WriteLine(failure);
        Console.WriteLine($"contract surface: declarations={lines.Count}, errors={failures.Count}");
        return failures.Count == 0 ? 0 : 1;

        string AssemblyPath(string name) => Path.Combine(root,
            Path.GetDirectoryName(projects[name].Path)!,
            "bin", configuration, "net10.0", $"{name}.dll");

        static bool VisibleSymbol(ISymbol? symbol)
        {
            for (var current = symbol; current is not null && current is not INamespaceSymbol; current = current.ContainingType)
                if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal))
                    return false;
            return symbol is not null;
        }

        static bool Visible(SyntaxTokenList modifiers) => modifiers.Any(token =>
            token.IsKind(SyntaxKind.PublicKeyword) || token.IsKind(SyntaxKind.ProtectedKeyword));

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
    }
}
