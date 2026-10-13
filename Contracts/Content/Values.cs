using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Content.Contracts;

public readonly record struct PackageId(string Value);
public readonly record struct ContentGeneration(ulong Value);
public readonly record struct ContentRevision(ulong Value);
public readonly record struct AssetRevision(string Value);
public readonly record struct BlueprintId(string Value);
public readonly record struct DraftId(Guid Value);
public readonly record struct EditRevision(ulong Value);
public readonly record struct GenerationProfileId(string Value);
public readonly record struct PackageSourceVersion(string Value);
public enum BlueprintLibrary { Packages, User, Local }
public sealed record BlueprintAddress(BlueprintLibrary Library, BlueprintId Id);
public sealed record ContentSourceLocation(PackageId Package, string Path);
public enum DefinitionKind
{
    Voxel, Blueprint, Process, SettlementType, SettlementTemplate, PopulationRule,
    MarketRule, ConstructionRule, RailwayRule, CombatRule, LairType, GenerationProfile, TerrainType
}
public sealed record PackageDependency(PackageId Package, string MinimumVersion);
public sealed record PackageMetadata(PackageId Id, string Name, string Version,
    ImmutableArray<string> Authors, ImmutableArray<PackageDependency> Dependencies);
public enum PackageState { Available, Disabled, Invalid, DependencyUnavailable }
public sealed record PackageStatus(PackageMetadata? Metadata, ContentRoot Root,
    PackageState State, ImmutableArray<ContentDiagnostic> Diagnostics);

public abstract record ContentReference
{
    private ContentReference() { }
    public sealed record Core(CoreDefinitionReference Definition) : ContentReference;
    public sealed record Blueprint(BlueprintAddress Address) : ContentReference;
    public sealed record Design(DesignHash Hash) : ContentReference;
    public sealed record Draft(DraftId Id) : ContentReference;
    public sealed record Generation(GenerationProfileId Id) : ContentReference;
}
public enum ContentDiagnosticSeverity { Information, Warning, Error }
public sealed record ContentDiagnostic(string Code, ContentDiagnosticSeverity Severity,
    string Stage, ContentReference? Subject, ContentSourceLocation? Source,
    string Field, DesignElementPath? Element, string Message);
public abstract record ContentResult<T> where T : notnull
{
    private ContentResult() { }
    public sealed record Success(T Value) : ContentResult<T>;
    public sealed record Failure(ImmutableArray<ContentDiagnostic> Diagnostics) : ContentResult<T>;
    public sealed record Faulted(string Code, string Message, string? StackTrace) : ContentResult<T>;
}
public abstract record ContentLookup<T> where T : notnull
{
    private ContentLookup() { }
    public sealed record Found(T Value) : ContentLookup<T>;
    public sealed record Missing(ImmutableArray<ContentDiagnostic> Diagnostics) : ContentLookup<T>;
    public sealed record NotFound : ContentLookup<T>;
}
public sealed record ContentDisplay(DisplayText Text, AssetRevision? Icon, AssetRevision? MapSymbol, bool Major);
public sealed record BlueprintSummary(BlueprintAddress Address, DisplayText Display,
    DesignHash? Design, bool Usable, ImmutableArray<ContentDiagnostic> Diagnostics);
public sealed record BlueprintRecord(BlueprintAddress Address, DesignDraft Definition,
    ImmutableArray<PackageMetadata> UsedPackages, DesignEvaluation Evaluation);
public sealed record GenerationProfileSummary(GenerationProfileId Id, DisplayText Display);
