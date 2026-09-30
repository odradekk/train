using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

public sealed class DiscoveredCase
{
    public string Id = "";
    public string Family = "";
    public List<string> Scopes = new List<string>();
    public List<string> Requirements = new List<string>();
}

public sealed class TestCatalog
{
    public List<DiscoveredCase> Cases = new List<DiscoveredCase>();
    public List<string> Errors = new List<string>();
}

public static class TestMetadataReader
{
    static readonly string[] Families = { "unit", "headless" };
    static readonly string[] Requirements = { "godot", "process", "export", "graphical" };

    public static TestCatalog Read(string assemblyPath)
    {
        var catalog = new TestCatalog();
        using var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            if ((type.Attributes & TypeAttributes.Interface) != 0)
                continue;

            var typeCategories = Categories(reader, type.GetCustomAttributes(), catalog);
            var isTestClass = HasAttribute(reader, type.GetCustomAttributes(), "TestClassAttribute");
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (!IsExecutableTest(reader, method.GetCustomAttributes()))
                    continue;

                var id = TypeId(reader, type) + "." + reader.GetString(method.Name);
                if (!isTestClass)
                    catalog.Errors.Add(id + " has a test method without TestClass");

                var categories = new List<string>(typeCategories);
                categories.AddRange(Categories(reader, method.GetCustomAttributes(), catalog));
                AddCase(catalog, id, categories);
            }
        }

        catalog.Cases.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.Ordinal));
        return catalog;
    }

    static void AddCase(TestCatalog catalog, string id, List<string> categories)
    {
        string family = "";
        var scopes = new List<string>();
        var requirements = new List<string>();
        foreach (var category in categories)
        {
            if (category.StartsWith("family:", StringComparison.Ordinal))
            {
                var value = category.Substring("family:".Length);
                if (Array.IndexOf(Families, value) < 0)
                {
                    catalog.Errors.Add(id + " has unknown family '" + value + "'");
                    continue;
                }

                if (family.Length > 0 && family != value)
                    catalog.Errors.Add(id + " has conflicting families '" + family + "' and '" + value + "'");
                family = value;
            }
            else if (category.StartsWith("scope:", StringComparison.Ordinal))
            {
                var value = category.Substring("scope:".Length);
                if (value.Length == 0 || value.IndexOfAny(new[] { ' ', '\t', ':', ',' }) >= 0)
                    catalog.Errors.Add(id + " has invalid scope '" + value + "'");
                else if (!scopes.Contains(value))
                    scopes.Add(value);
            }
            else if (category.StartsWith("requires:", StringComparison.Ordinal))
            {
                var value = category.Substring("requires:".Length);
                if (Array.IndexOf(Requirements, value) < 0)
                    catalog.Errors.Add(id + " has unknown requirement '" + value + "'");
                else if (!requirements.Contains(value))
                    requirements.Add(value);
            }
            else
            {
                catalog.Errors.Add(id + " has unrecognized category '" + category + "'");
            }
        }

        if (family.Length == 0)
            catalog.Errors.Add(id + " is missing family");
        if (scopes.Count == 0)
            catalog.Errors.Add(id + " is missing scope");

        var discovered = new DiscoveredCase();
        discovered.Id = id;
        discovered.Family = family;
        discovered.Scopes = scopes;
        discovered.Requirements = requirements;
        catalog.Cases.Add(discovered);
    }

    static string TypeId(MetadataReader reader, TypeDefinition type)
    {
        var name = reader.GetString(type.Name);
        if (type.IsNested)
            return TypeId(reader, reader.GetTypeDefinition(type.GetDeclaringType())) + "+" + name;

        var ns = reader.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    static bool IsExecutableTest(MetadataReader reader, CustomAttributeHandleCollection handles)
    {
        foreach (var handle in handles)
        {
            var name = AttributeTypeName(reader, handle);
            // DataTestMethodAttribute derives from TestMethodAttribute. The metadata
            // records the derived name, so an exact TestMethodAttribute match drops it.
            if (name == "TestMethodAttribute" || name == "DataTestMethodAttribute")
                return true;
        }

        return false;
    }

    static bool HasAttribute(MetadataReader reader, CustomAttributeHandleCollection handles, string typeName)
    {
        foreach (var handle in handles)
        {
            if (AttributeTypeName(reader, handle) == typeName)
                return true;
        }

        return false;
    }

    static List<string> Categories(MetadataReader reader, CustomAttributeHandleCollection handles, TestCatalog catalog)
    {
        var values = new List<string>();
        foreach (var handle in handles)
        {
            if (AttributeTypeName(reader, handle) != "TestCategoryAttribute")
                continue;

            try
            {
                var attr = reader.GetCustomAttribute(handle);
                var blob = reader.GetBlobReader(attr.Value);
                if (blob.ReadUInt16() != 1)
                {
                    catalog.Errors.Add("TestCategory attribute blob is not a constructor argument");
                    continue;
                }

                values.Add(blob.ReadSerializedString());
            }
            catch (Exception ex)
            {
                catalog.Errors.Add("could not read TestCategory: " + ex.Message);
            }
        }

        return values;
    }

    static string AttributeTypeName(MetadataReader reader, CustomAttributeHandle handle)
    {
        var attr = reader.GetCustomAttribute(handle);
        var ctor = attr.Constructor;
        if (ctor.Kind == HandleKind.MemberReference)
        {
            var member = reader.GetMemberReference((MemberReferenceHandle)ctor);
            return TypeName(reader, member.Parent);
        }

        if (ctor.Kind == HandleKind.MethodDefinition)
        {
            var method = reader.GetMethodDefinition((MethodDefinitionHandle)ctor);
            return reader.GetString(reader.GetTypeDefinition(method.GetDeclaringType()).Name);
        }

        return "";
    }

    static string TypeName(MetadataReader reader, EntityHandle handle)
    {
        if (handle.Kind == HandleKind.TypeReference)
            return reader.GetString(reader.GetTypeReference((TypeReferenceHandle)handle).Name);
        if (handle.Kind == HandleKind.TypeDefinition)
            return reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)handle).Name);
        return "";
    }
}
