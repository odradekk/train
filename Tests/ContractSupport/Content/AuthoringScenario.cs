using System.Collections.Immutable;
using Train.Content.Contracts;

namespace Train.Content.ContractSupport;

public sealed class FixedAuthoringPackage(PackageId package, PackageSourceVersion version,
    ImmutableArray<DefinitionDraft> definitions) : IAuthoringPackage
{
    public PackageId Package => package;
    public PackageSourceVersion SourceVersion => version;
    public PackageMetadata Metadata { get; } = new(package, "authoring fixture", "1.0.0", [], []);
    public ImmutableArray<AuthoringDefinitionAddress> EditableDefinitions =>
        definitions.Select(Address).ToImmutableArray();
    public ContentLookup<DefinitionDraft> Read(AuthoringDefinitionAddress address) =>
        definitions.FirstOrDefault(value => Address(value) == address) is { } found
            ? new ContentLookup<DefinitionDraft>.Found(found) : new ContentLookup<DefinitionDraft>.NotFound();
    private static AuthoringDefinitionAddress Address(DefinitionDraft draft) => draft switch
    {
        DefinitionDraft.Blueprint value => new(DefinitionKind.Blueprint, value.LocalId),
        DefinitionDraft.Voxel value => new(DefinitionKind.Voxel, value.Value.LocalId),
        DefinitionDraft.SettlementType value => new(DefinitionKind.SettlementType, value.Value.LocalId),
        DefinitionDraft.LairType value => new(DefinitionKind.LairType, value.Value.LocalId),
        DefinitionDraft.SettlementTemplate value => new(DefinitionKind.SettlementTemplate, value.Value.LocalId),
        _ => throw new ArgumentOutOfRangeException(nameof(draft))
    };
}
