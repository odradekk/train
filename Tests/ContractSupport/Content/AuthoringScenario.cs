using System.Collections.Immutable;
using Train.Content.Contracts;

namespace Train.Content.ContractSupport;

public sealed class FixedAuthoringPackage(PackageId package, PackageSourceVersion version,
    ImmutableArray<DefinitionDraft.Blueprint> definitions) : IAuthoringPackage
{
    public PackageId Package => package;
    public PackageSourceVersion SourceVersion => version;
    public PackageMetadata Metadata { get; } = new(package, "authoring fixture", "1.0.0", [], []);
    public ImmutableArray<AuthoringDefinitionAddress> EditableDefinitions =>
        definitions.Select(value => new AuthoringDefinitionAddress(DefinitionKind.Blueprint, value.LocalId)).ToImmutableArray();
    public ContentLookup<DefinitionDraft> Read(AuthoringDefinitionAddress address) =>
        address.Kind == DefinitionKind.Blueprint && definitions.FirstOrDefault(value => value.LocalId == address.LocalId) is { } found
            ? new ContentLookup<DefinitionDraft>.Found(found) : new ContentLookup<DefinitionDraft>.NotFound();
}
