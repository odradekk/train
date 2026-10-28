using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Train.ContractChecks;

internal static class FactoryReferences
{
    internal static List<string> Check(string root, string name, JsonElement items, string defineConstants)
    {
        if (IsExempt(name)) return [];
        var options = new CSharpParseOptions(LanguageVersion.CSharp12,
            preprocessorSymbols: defineConstants.Split(';', StringSplitOptions.RemoveEmptyEntries));
        var trees = items.GetProperty("Compile").EnumerateArray().Select(item =>
        {
            var path = item.GetProperty("FullPath").GetString()!;
            return CSharpSyntaxTree.ParseText(File.ReadAllText(path), options, path);
        });
        var references = items.GetProperty("ReferencePath").EnumerateArray().Select(item =>
            MetadataReference.CreateFromFile(item.GetProperty("FullPath").GetString()!));
        return CheckCompilation(root, name, CSharpCompilation.Create(name, trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));
    }

    internal static List<string> CheckCompilation(string root, string name, CSharpCompilation compilation)
    {
        var failures = new List<string>();
        if (IsExempt(name)) return failures;
        var factories = new[] { "Core", "Content", "Scenes" }.Select(module =>
            compilation.GetTypeByMetadataName($"Train.{module}.{module}Factory"))
            .Where(type => type is not null && type.ContainingAssembly.Name == "Train." + type.Name[..^"Factory".Length])
            .SelectMany(type => type!.GetMembers("Create").OfType<IMethodSymbol>())
            .ToHashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        if (factories.Count == 0) return failures;
        foreach (var tree in compilation.SyntaxTrees)
        {
            var path = Path.GetFullPath(tree.FilePath);
            if (name == "Train" && ProjectAnalysis.IsChildPath(path, Path.Combine(root, "App", "Composition")) ||
                name == "Train.Cli" && path.Equals(Path.Combine(root, "Tools", "Cli", "Composition.cs"),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                name == "Train.SceneScale" && path.Equals(Path.Combine(root, "Tools", "SceneScale", "Composition.cs"),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
            var model = compilation.GetSemanticModel(tree);
            foreach (var identifier in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
            {
                if (identifier.Identifier.ValueText != "Create") continue;
                var symbol = model.GetSymbolInfo(identifier).Symbol as IMethodSymbol;
                if (symbol is null || !factories.Contains(symbol.OriginalDefinition)) continue;
                var line = tree.GetLineSpan(identifier.Span).StartLinePosition.Line + 1;
                failures.Add($"factory reference outside composition: {name} ({path}:{line}) -> {symbol.ContainingType}.Create");
            }
        }
        return failures;
    }

    private static bool IsExempt(string name) => name.EndsWith(".Tests", StringComparison.Ordinal) || name == "Train.Benchmarks";
}
