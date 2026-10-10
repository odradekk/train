using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

// 公开面只作为本轮比较输入，不是用户批准的契约基线。
var directory = Path.GetFullPath(args[1]);
var paths = Directory.GetFiles(directory, "Train.*.Contracts.dll").Order().ToArray();
if (paths.Length != 3) throw new InvalidOperationException("Expected three rebuilt contract assemblies.");
var allowed = new Dictionary<string, string[]>
{
    ["Train.Core.Contracts"] = [],
    ["Train.Content.Contracts"] = ["Train.Core.Contracts"],
    ["Train.Scenes.Contracts"] = ["Train.Core.Contracts", "Train.Content.Contracts"]
};
var failures = new List<string>();
foreach (var path in paths)
{
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    var metadata = pe.GetMetadataReader();
    var name = metadata.GetString(metadata.GetAssemblyDefinition().Name);
    foreach (var handle in metadata.AssemblyReferences)
    {
        var reference = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
        Console.WriteLine($"assembly-reference {name} -> {reference}; source={path}");
        if (!(reference == "System" || reference.StartsWith("System.") || allowed[name].Contains(reference)))
            failures.Add($"Forbidden assembly dependency: {name} -> {reference}");
    }
}
var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
var refs = platform.Concat(paths).Distinct().Select(path => MetadataReference.CreateFromFile(path)).ToArray();
var compilation = CSharpCompilation.Create("SurfaceProbe", references: refs);
var format = SymbolDisplayFormat.FullyQualifiedFormat.WithMemberOptions(
    SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType |
    SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeModifiers |
    SymbolDisplayMemberOptions.IncludeRef | SymbolDisplayMemberOptions.IncludeConstantValue).WithGenericsOptions(
    SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints).WithParameterOptions(
    SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName |
    SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeDefaultValue).WithMiscellaneousOptions(
    SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier |
    SymbolDisplayMiscellaneousOptions.UseSpecialTypes);
var lines = new List<string>();
foreach (var path in paths)
{
    var reference = refs.Single(r => r.FilePath == path);
    var assembly = (IAssemblySymbol)compilation.GetAssemblyOrModuleSymbol(reference)!;
    Visit(assembly.GlobalNamespace);
}
void Visit(INamespaceOrTypeSymbol container)
{
    foreach (var member in container.GetMembers())
    {
        if (member is INamespaceSymbol ns) { Visit(ns); continue; }
        if (member.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal)) continue;
        lines.Add($"{member.Kind}|{member.DeclaredAccessibility}|{member.ToDisplayString(format)}|{string.Join(",", member.GetAttributes().Select(a => a.ToString()).Order())}");
        if (member is IPropertySymbol property)
            lines.Add($"accessors|{property.ToDisplayString(format)}|get={property.GetMethod?.DeclaredAccessibility}|set={property.SetMethod?.DeclaredAccessibility}|init={property.SetMethod?.IsInitOnly}");
        if (member is INamedTypeSymbol type)
        {
            lines.Add($"bases|{type.ToDisplayString(format)}|{type.BaseType?.ToDisplayString(format)}|{string.Join(",", type.Interfaces.Select(i => i.ToDisplayString(format)).Order())}");
            Visit(type);
        }
    }
}
lines.Sort(StringComparer.Ordinal);
if (args[0] == "observe") File.WriteAllLines(args[2], lines);
else
{
    var baseline = File.ReadAllLines(args[2]);
    foreach (var difference in baseline.Except(lines).Select(s => "removed " + s).Concat(lines.Except(baseline).Select(s => "added " + s)))
        failures.Add("Public surface changed: " + difference);
}
foreach (var failure in failures) Console.Error.WriteLine(failure);
Console.WriteLine($"public-symbols={lines.Count}; errors={failures.Count}");
return failures.Count == 0 ? 0 : 1;
