// 内容的身份、来源和诊断；不以文件路径替代定义身份。
// 普通数据深度不可变，局部有效范围与失败语义见 docs/contracts/content.md。
using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Content.Contracts;

/// <summary>core 或规范小写 Mod UUID；与磁盘目录名无关。</summary>
public readonly record struct PackageId(string Value);
public readonly record struct ContentGeneration(ulong Value);
/// <summary>本局可保存内容的修订；保存草稿也可改变它而不发布新世代。</summary>
public readonly record struct ContentRevision(ulong Value);
public readonly record struct AssetRevision(string Value);
/// <summary>包蓝图为 namespaced identity，玩家蓝图为 UUID；查询同时指定库。</summary>
public readonly record struct BlueprintId(string Value);
public readonly record struct DraftId(Guid Value);
public readonly record struct EditRevision(ulong Value);
public readonly record struct GenerationProfileId(string Value);
/// <summary>宿主提供的完整源包基准身份，内容仅保存和传回，不把时间戳当防覆盖证明。</summary>
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
/// <summary>Code 稳定；Message 仅供诊断。字段和嵌套路径用于 UI 定位及控制台输出。</summary>
public sealed record ContentDiagnostic(string Code, ContentDiagnosticSeverity Severity,
    string Stage, ContentReference? Subject, ContentSourceLocation? Source,
    string Field, DesignElementPath? Element, string Message);
/// <summary>预期失败与内部故障分开；失败不修改输入、活动内容或核心会话。</summary>
public abstract record ContentResult<T> where T : notnull
{
    private ContentResult() { }
    public sealed record Success(T Value) : ContentResult<T>;
    public sealed record Failure(ImmutableArray<ContentDiagnostic> Diagnostics) : ContentResult<T>;
    public sealed record Faulted(string Code, string Message, string? StackTrace) : ContentResult<T>;
}
/// <summary>不存在与已知缺失分开；Missing 保留身份及诊断，不伪造完整默认值。</summary>
public abstract record ContentLookup<T> where T : notnull
{
    private ContentLookup() { }
    public sealed record Found(T Value) : ContentLookup<T>;
    public sealed record Missing(ImmutableArray<ContentDiagnostic> Diagnostics) : ContentLookup<T>;
    public sealed record NotFound : ContentLookup<T>;
}
public sealed record ContentDisplay(DisplayText Text, AssetRevision? Icon, AssetRevision? MapSymbol);
public sealed record BlueprintSummary(BlueprintAddress Address, DisplayText Display,
    DesignHash? Design, bool Usable, ImmutableArray<ContentDiagnostic> Diagnostics);
public sealed record BlueprintRecord(BlueprintAddress Address, DesignDraft Definition,
    ImmutableArray<PackageMetadata> UsedPackages, DesignEvaluation Evaluation);
public sealed record GenerationProfileSummary(GenerationProfileId Id, DisplayText Display);
