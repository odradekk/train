// 属于内容模块。
// 定义包、诊断、路径与资源数据在内容加载边界上的共享契约。
using System.Collections.ObjectModel;
using Train.Core.Content.Voxels;

namespace Train.Core.Content;

/// <summary>集中保存当前内容 schema 沿用的资源和诊断数量上限。</summary>
/// <remarks>字节上限针对原始文件，图片尺寸上限针对解码后的单边像素数；数量上限分别作用于候选目录、单个 Mod 和单个诊断容器。这些值是当前内容加载的固定约束，见 Core/Content/README.md。</remarks>
/// <example>超过 1 MiB 的 JSON 文件拒绝加载；见 <c>ContentReloaderTests.File_and_global_limits_reject_oversized_candidates</c>。</example>
public static class ContentLimits
{
    /// <summary>单个 JSON 文件允许的最大字节数；沿用当前内容 schema 的 1 MiB 限制。</summary>
    public const int JsonFileMaxBytes = 1 * 1024 * 1024;
    /// <summary>单个 PNG 文件允许的最大字节数；沿用当前内容 schema 的 4 MiB 限制。</summary>
    public const int PngMaxBytes = 4 * 1024 * 1024;
    /// <summary>PNG 单边允许的最大像素数；沿用当前内容 schema 的 4096 像素限制。</summary>
    public const int ImageMaxDimension = 4096;
    /// <summary>一次发布允许的最大体素定义数；沿用当前内容 schema 的 10000 项限制。</summary>
    public const int MaxVoxels = 10_000;
    /// <summary>单个 Mod 允许的最大体素定义数；沿用当前内容 schema 的 1000 项限制。</summary>
    public const int MaxVoxelsPerMod = 1000;
    /// <summary>单个包保留的最大诊断条数；沿用当前内容 schema 的 100 条限制。</summary>
    public const int MaxDiagnosticsPerPackage = 100;
}

/// <summary>集中保存内容加载诊断的稳定机器可读代码。</summary>
/// <remarks>代码标识问题类别，供日志和调用方分类处理；包是否无效和发布是否被拒绝由相应校验流程决定。</remarks>
/// <example>不安全的表现路径产生 path_invalid 诊断；见 <c>ContentReloaderTests.Unsafe_visual_paths_reject_the_package</c>。</example>
public static class ContentDiagnosticCodes
{
    /// <summary>JSON 语法或根结构无效。</summary>
    public const string InvalidJson = "invalid_json";
    /// <summary>同一 JSON 对象包含重复属性。</summary>
    public const string DuplicateProperty = "duplicate_property";
    /// <summary>JSON 对象包含当前 schema 未声明的字段。</summary>
    public const string UnknownField = "unknown_field";
    /// <summary>文件声明的 schema 版本不受支持。</summary>
    public const string SchemaVersionUnsupported = "schema_version_unsupported";
    /// <summary>定义的包内本地标识无效。</summary>
    public const string LocalIdInvalid = "local_id_invalid";
    /// <summary>名称缺失、类型错误或越过 schema 文本边界。</summary>
    public const string NameInvalid = "name_invalid";
    /// <summary>说明类型错误或越过 schema 文本边界。</summary>
    public const string DescriptionInvalid = "description_invalid";
    /// <summary>定义的价值字段无效。</summary>
    public const string ValueInvalid = "value_invalid";
    /// <summary>定义的质量字段无效。</summary>
    public const string MassInvalid = "mass_invalid";
    /// <summary>定义的最大耐久度字段无效。</summary>
    public const string MaxDurabilityInvalid = "max_durability_invalid";
    /// <summary>定义标签不符合 schema。</summary>
    public const string TagInvalid = "tag_invalid";
    /// <summary>定义引用了未注册的行为类型。</summary>
    public const string BehaviorUnregistered = "behavior_unregistered";
    /// <summary>体素允许方向声明无效。</summary>
    public const string OrientationInvalid = "orientation_invalid";
    /// <summary>体素碰撞声明无效。</summary>
    public const string CollisionInvalid = "collision_invalid";
    /// <summary>体素表现声明无效。</summary>
    public const string PresentationInvalid = "presentation_invalid";
    /// <summary>体素缺少其类别要求的表现声明。</summary>
    public const string PresentationRequired = "presentation_required";
    /// <summary>定义类别不受当前 schema 支持。</summary>
    public const string CategoryUnsupported = "category_unsupported";
    /// <summary>包内相对路径不安全或格式无效。</summary>
    public const string PathInvalid = "path_invalid";
    /// <summary>图像字节无法按声明格式解码。</summary>
    public const string ImageInvalid = "image_invalid";
    /// <summary>同一包声明了重复的本地标识。</summary>
    public const string DuplicateLocalId = "duplicate_local_id";
    /// <summary>数据文件超过对应字节上限。</summary>
    public const string FileTooLarge = "file_too_large";
    /// <summary>图像字节数或像素尺寸超过上限。</summary>
    public const string ImageTooLarge = "image_too_large";
    /// <summary>候选目录中的体素定义总数超过全局上限。</summary>
    public const string GlobalVoxelLimitExceeded = "global_voxel_limit_exceeded";
    /// <summary>包产生的诊断超过保留上限。</summary>
    public const string DiagnosticsTruncated = "diagnostics_truncated";
    /// <summary>包因其中一个文件或跨定义校验失败而整体无效。</summary>
    public const string PackageInvalid = "package_invalid";
    /// <summary>上一轮内容重载尚未结束。</summary>
    public const string ReloadAlreadyInProgress = "reload_already_in_progress";
    /// <summary>定义引用了候选目录中不存在的体素定义。</summary>
    public const string UnresolvedVoxelReference = "unresolved_voxel_reference";
    /// <summary>必需的内置内容包不可用。</summary>
    public const string BuiltInPackageUnavailable = "built_in_package_unavailable";
    /// <summary>首次加载没有产生任何体素定义。</summary>
    public const string EmptyInitialCatalog = "empty_initial_catalog";
    /// <summary>发现或读取路径经过链接、重解析点或不可验证路径段。</summary>
    public const string UnsafeLinkedPath = "unsafe_linked_path";
    /// <summary>UUID 缺失或不是规范小写非零 UUID。</summary>
    public const string UuidInvalid = "uuid_invalid";
    /// <summary>版本缺失或不是有效 SemVer 2.0。</summary>
    public const string VersionInvalid = "version_invalid";
    /// <summary>作者列表缺失、重复或越过 schema 边界。</summary>
    public const string AuthorsInvalid = "authors_invalid";
    /// <summary>多个 Mod 声明了同一个 UUID。</summary>
    public const string DuplicateModUuid = "duplicate_mod_uuid";
    /// <summary>Mod 根目录无法安全创建或列举。</summary>
    public const string ModsRootUnavailable = "mods_root_unavailable";
    /// <summary>单个 Mod 的体素定义数超过包上限。</summary>
    public const string ModVoxelLimitExceeded = "mod_voxel_limit_exceeded";
    /// <summary>Mod 根目录缺少 <c>mod.json</c>。</summary>
    public const string ModManifestMissing = "mod_manifest_missing";
    /// <summary>玩家蓝图根目录无法安全创建或列举。</summary>
    public const string BlueprintsRootUnavailable = "blueprints_root_unavailable";
    /// <summary>组合定义的尺寸等级无效。</summary>
    public const string SizeClassInvalid = "size_class_invalid";
    /// <summary>组合定义的体素布局违反当前 schema。</summary>
    public const string LayoutInvalid = "layout_invalid";
}

/// <summary>表示内容问题在诊断和日志中的严重程度。</summary>
/// <remarks>严重程度与诊断容器的无效状态分别记录；警告也可以标记当前包无效。</remarks>
/// <example>非法名称会产生警告级诊断并使内置内容被拒绝；见 <c>ContentReloaderTests.Diagnostics_are_capped_per_package_and_still_reject</c>。</example>
public enum ContentDiagnosticSeverity
{
    /// <summary>警告级问题；可用于报告被隔离的可选内容，也可伴随包无效状态。</summary>
    Warning = 0,
    /// <summary>错误级问题，例如必需包不可用或候选发布被拒绝。</summary>
    Error = 1
}

/// <summary>表示一条带来源和字段位置的内容诊断。</summary>
/// <remarks>保存调用方提供的严重程度和位置；构造函数只校验代码非空、说明非 null，不规范化路径或检查 JSON 指针格式。</remarks>
/// <example>清单缺少 name 时，诊断带 name_invalid 代码和 /name 字段路径；见 <c>ModManifestTests.Missing_required_fields_each_produce_a_bounded_diagnostic</c>。</example>
public sealed class ContentDiagnostic
{
    /// <summary>取得诊断严重程度。</summary>
    public ContentDiagnosticSeverity Severity { get; }
    /// <summary>取得稳定的机器可读诊断代码。</summary>
    public string Code { get; }
    /// <summary>取得面向开发者的诊断说明。</summary>
    public string Message { get; }
    /// <summary>取得来源文件或逻辑路径；未知时为 <see langword="null"/>。</summary>
    public string? Source { get; }
    /// <summary>取得 JSON 指针或相关路径；不适用时为 <see langword="null"/>。</summary>
    public string? JsonPath { get; }

    /// <summary>创建内容诊断。</summary>
    /// <param name="severity">要记录的诊断严重程度。</param>
    /// <param name="code">非空的机器可读诊断代码，通常取自 <see cref="ContentDiagnosticCodes"/>。</param>
    /// <param name="message">面向开发者的说明；允许空字符串。</param>
    /// <param name="source">来源文件或逻辑路径；未知时为 null。</param>
    /// <param name="jsonPath">JSON 字段位置或相关路径；不适用时为 null。</param>
    /// <exception cref="ArgumentNullException">code 或 message 为 null。</exception>
    /// <exception cref="ArgumentException">code 为空字符串。</exception>
    public ContentDiagnostic(
        ContentDiagnosticSeverity severity,
        string code,
        string message,
        string? source = null,
        string? jsonPath = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        ArgumentNullException.ThrowIfNull(message);
        Severity = severity;
        Code = code;
        Message = message;
        Source = source;
        JsonPath = jsonPath;
    }
}

/// <summary>表示定义所属的包命名空间和源文件。</summary>
/// <remarks>命名空间按定义身份规则规范化；源文件路径只要求非空，路径安全性由发现和读取边界检查。</remarks>
/// <example>内置 ferrous-ore 定义的来源属于 core 包；见 <c>ContentReloaderTests.Initial_valid_voxel_catalog_commits</c>。</example>
public sealed class ContentOrigin
{
    /// <summary>取得定义所属的规范包命名空间。</summary>
    public string Namespace { get; }
    /// <summary>取得定义在包中的源文件路径。</summary>
    public string SourceFile { get; }

    /// <summary>规范化包命名空间，并保存定义的来源路径。</summary>
    /// <param name="namespace">去除首尾空白后符合小写 ASCII 身份规则、长度为 1 至 64 的包命名空间。</param>
    /// <param name="sourceFile">非空的来源文件路径；原样保存，不读取或规范化该路径。</param>
    /// <exception cref="ArgumentNullException">namespace 或 sourceFile 为 null。</exception>
    /// <exception cref="ArgumentException">命名空间格式无效或 sourceFile 为空字符串。</exception>
    public ContentOrigin(string @namespace, string sourceFile)
    {
        Namespace = VoxelDefinitionId.NormalizePart(@namespace, nameof(@namespace));
        ArgumentException.ThrowIfNullOrEmpty(sourceFile);
        SourceFile = sourceFile;
    }
}

/// <summary>表示解码后、与图像库无关的像素数据。</summary>
/// <remarks>宽高和格式由解码器提供，本类型不验证尺寸、格式支持性或像素字节数。像素数组通过只读接口包装，调用方须保持原数组内容稳定。</remarks>
/// <example>8×4 图像可以表示为解码结果，但内容加载器会根据表现规则拒绝不合要求的宽高；见 <c>ContentReloaderTests.Image_shape_and_size_rules_reject_incompatible_visuals</c>。</example>
public sealed class DecodedImage
{
    /// <summary>取得图像宽度，单位为像素。</summary>
    public int Width { get; }
    /// <summary>取得图像高度，单位为像素。</summary>
    public int Height { get; }
    /// <summary>取得解码器提供的像素格式名称，原文保留，具体排列由解码器和消费方约定。</summary>
    public string Format { get; }
    /// <summary>取得输入像素数组的只读包装；排列由 <see cref="Format"/> 定义，原数组的修改会反映到此视图。</summary>
    public IReadOnlyList<byte> Pixels { get; }

    /// <summary>保存解码图像的尺寸和格式，并通过只读集合包装像素数组。</summary>
    /// <param name="width">调用方提供的图像宽度，单位为像素。</param>
    /// <param name="height">调用方提供的图像高度，单位为像素。</param>
    /// <param name="format">非空的像素格式名称。</param>
    /// <param name="pixels">像素字节数组；不复制，允许空数组。</param>
    /// <exception cref="ArgumentNullException">format 或 pixels 为 null。</exception>
    /// <exception cref="ArgumentException">format 为空字符串。</exception>
    public DecodedImage(int width, int height, string format, byte[] pixels)
    {
        ArgumentException.ThrowIfNullOrEmpty(format);
        ArgumentNullException.ThrowIfNull(pixels);
        Width = width;
        Height = height;
        Format = format;
        Pixels = new ReadOnlyCollection<byte>(pixels);
    }
}

/// <summary>区分内置内容、数据 Mod 与玩家蓝图。</summary>
/// <remarks>包种类由内容源指定，用于区分必需内容与可隔离内容，并决定是否需要 Mod 元数据。</remarks>
/// <example>有效 Mod 清单发现后生成 Mod 种类的包；见 <c>ModDiscoveryTests.Accepted_package_retains_normalized_manifest_metadata</c>。</example>
public enum ContentPackageKind
{
    /// <summary>随游戏分发且加载失败会阻止发布的内置包。</summary>
    BuiltIn = 0,
    /// <summary>从用户 Mod 根发现、可按包隔离的数据 Mod。</summary>
    Mod = 1,
    /// <summary>以文件 UUID 为命名空间的玩家蓝图包。</summary>
    PlayerBlueprint = 2
}

/// <summary>表示从 Mod 清单复制的包元数据和只读作者列表。</summary>
/// <remarks>调用方须提供已校验清单；本类型不重新校验字段或规范化文本。作者列表会复制，后续修改输入列表不会改变元数据。</remarks>
/// <example>显示名称“  铜矿扩展  ”经清单解析后，以“铜矿扩展”保留到包元数据；见 <c>ModDiscoveryTests.Accepted_package_retains_normalized_manifest_metadata</c>。</example>
public sealed class ModPackageMetadata
{
    /// <summary>取得清单原样声明的 Mod 身份文本，用于与包命名空间核对一致性。</summary>
    public string Id { get; }
    /// <summary>取得去除首尾空白后的显示名称。</summary>
    public string Name { get; }
    /// <summary>取得清单声明的 SemVer 2.0 版本文本，不再做版本转换。</summary>
    public string Version { get; }
    /// <summary>取得去除首尾空白且不重复的作者列表。</summary>
    public IReadOnlyList<string> Authors { get; }
    /// <summary>取得去除首尾空白后的说明；未声明时为空字符串。</summary>
    public string Description { get; }

    /// <summary>复制调用方提供的清单字段，并将作者列表复制为只读集合。</summary>
    /// <param name="manifest">非空、已校验且已完成文本规范化的 Mod 清单。</param>
    /// <exception cref="ArgumentNullException">manifest 为 null。</exception>
    public ModPackageMetadata(ModManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Id = manifest.Id;
        Name = manifest.Name;
        Version = manifest.Version;
        Authors = new ReadOnlyCollection<string>(manifest.Authors.ToArray());
        Description = manifest.Description;
    }
}

/// <summary>表示一次内容扫描中的一个可发现包。</summary>
/// <remarks>资源根和可用性由内容源提供，本对象不访问文件系统。Mod 包必须携带与命名空间一致的元数据，其他包不能携带 Mod 元数据。</remarks>
/// <example>已接受的 Mod 包同时保留声明 UUID 和规范化后的显示信息；见 <c>ModDiscoveryTests.Accepted_package_retains_normalized_manifest_metadata</c>。</example>
public sealed class ContentPackage
{
    /// <summary>取得定义身份使用的规范包命名空间。</summary>
    public string Namespace { get; }
    /// <summary>取得包资源根；可为逻辑路径或物理路径。</summary>
    public string ResourcesRoot { get; }
    /// <summary>取得内容源提供的资源根可用标记；构造函数不检查实际可访问性。</summary>
    public bool Available { get; }
    /// <summary>取得包种类。</summary>
    public ContentPackageKind Kind { get; }
    /// <summary>取得包声明来源；内置包没有声明文件时为 <see langword="null"/>。</summary>
    public string? Source { get; }
    /// <summary>取得 Mod 元数据；非 Mod 包为 <see langword="null"/>。</summary>
    public ModPackageMetadata? Mod { get; }

    /// <summary>保存内容包的发现信息，并校验命名空间和 Mod 元数据的一致性。</summary>
    /// <param name="namespace">定义身份使用的包命名空间，去除首尾空白后须符合身份规则。</param>
    /// <param name="resourcesRoot">非空资源根路径，可为逻辑路径或物理路径，原样保存。</param>
    /// <param name="available">由内容源确定的可用标记，默认 true。</param>
    /// <param name="kind">内容源指定的包种类，默认 BuiltIn。</param>
    /// <param name="source">包声明来源；未知或没有声明文件时为 null。</param>
    /// <param name="mod">Mod 包必需的清单元数据，其他种类必须为 null。</param>
    /// <exception cref="ArgumentNullException">namespace 或 resourcesRoot 为 null，或 Mod 包未提供 mod。</exception>
    /// <exception cref="ArgumentException">命名空间格式无效、资源根为空、Mod 身份与命名空间不一致，或其他包种类提供了 Mod 元数据。</exception>
    public ContentPackage(
        string @namespace,
        string resourcesRoot,
        bool available = true,
        ContentPackageKind kind = ContentPackageKind.BuiltIn,
        string? source = null,
        ModPackageMetadata? mod = null)
    {
        Namespace = VoxelDefinitionId.NormalizePart(@namespace, nameof(@namespace));
        ArgumentException.ThrowIfNullOrEmpty(resourcesRoot);
        ResourcesRoot = resourcesRoot;
        Available = available;
        Kind = kind;
        Source = source;
        if (kind == ContentPackageKind.Mod)
        {
            ArgumentNullException.ThrowIfNull(mod);
            if (!string.Equals(mod.Id, Namespace, StringComparison.Ordinal))
                throw new ArgumentException("Mod metadata UUID must match the package namespace.", nameof(mod));
            Mod = mod;
        }
        else if (mod is not null)
        {
            throw new ArgumentException("Built-in packages must not carry mod metadata.", nameof(mod));
        }
    }

    /// <summary>取得包是否属于必需内容；仅 BuiltIn 种类为 true，加载器据此处理不可用或无效包。</summary>
    public bool IsRequired => Kind == ContentPackageKind.BuiltIn;
}

/// <summary>定义内容模块使用的结构化日志类别和事件 ID。</summary>
/// <remarks>这些固定字符串用于识别日志来源和事件种类，具体说明及结构化字段由调用方提供。</remarks>
/// <example>订阅者异常以 content 类别和 content_notify_failed 事件记录；见 <c>ContentReloaderTests.Notify_exception_keeps_the_commit_and_allows_a_later_reload</c>。</example>
public static class ContentLog
{
    /// <summary>内容模块的结构化日志类别。</summary>
    public const string Category = "content";
    /// <summary>内容扫描开始事件 ID。</summary>
    public const string ScanStartedEventId = "content_scan_started";
    /// <summary>单个定义被接受事件 ID。</summary>
    public const string DefinitionAcceptedEventId = "content_definition_accepted";
    /// <summary>单个数据文件扫描完成事件 ID。</summary>
    public const string FileScannedEventId = "content_file_scanned";
    /// <summary>包没有贡献定义事件 ID。</summary>
    public const string PackageEmptyEventId = "content_package_empty";
    /// <summary>内容输入无效事件 ID。</summary>
    public const string InvalidEventId = "content_invalid";
    /// <summary>内容重载完成摘要事件 ID。</summary>
    public const string ReloadSummaryEventId = "content_reload_summary";
    /// <summary>内容重载未能发布事件 ID。</summary>
    public const string ReloadFailedEventId = "content_reload_failed";
    /// <summary>发布后观察者通知失败事件 ID。</summary>
    public const string NotifyFailedEventId = "content_notify_failed";
    /// <summary>Mod 根目录解析事件 ID。</summary>
    public const string ModsRootEventId = "content_mods_root";
    /// <summary>Mod 扫描摘要事件 ID。</summary>
    public const string ModsScanSummaryEventId = "content_mods_scan_summary";
}

/// <summary>提供包内相对路径的规范化和类型检查。</summary>
/// <remarks>处理正斜杠路径的字符串结构和后缀；物理文件访问、链接验证和操作系统路径检查由读取边界负责。</remarks>
/// <example>voxels/iron.png 可作为包内相对路径，file:secret.png 会被拒绝；见 <c>ContentReloaderTests.Scheme_prefixed_visual_paths_reject_while_colon_free_relative_pngs_commit</c>。</example>
public static class ContentPath
{
    /// <summary>校验包内相对路径采用正斜杠分隔，且不包含被禁止的前缀或路径段。</summary>
    /// <param name="path">候选路径；null、空字符串、NUL 字符、反斜杠、绝对或 URI 前缀、盘符前缀、空段及点段均无效。</param>
    /// <param name="normalized">成功时为以正斜杠连接的有效路径；失败时为空字符串。</param>
    /// <returns>路径通过上述字符串结构检查时返回 true，否则返回 false。</returns>
    /// <remarks>不去除首尾空白或改变大小写，不进行 URL 解码，也不访问文件系统；返回 true 不表示文件存在或物理路径安全。</remarks>
    /// <example>a/../b.png 和 a\\b.png 被拒绝；见 <c>ContentReloaderTests.Unsafe_visual_paths_reject_the_package</c>。</example>
    public static bool TryNormalize(string? path, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrEmpty(path))
            return false;
        if (path.Contains('\0', StringComparison.Ordinal))
            return false;
        if (path.Contains('\\', StringComparison.Ordinal))
            return false;
        if (path.StartsWith('/') || HasUriSchemePrefix(path))
            return false;
        if (path.Length >= 2 && path[1] == ':')
            return false;

        var segments = path.Split('/');
        if (segments.Length == 0)
            return false;
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
                return false;
        }

        normalized = string.Join('/', segments);
        return true;
    }

    /// <summary>判断路径是否以区分大小写的 .png 后缀结束。</summary>
    /// <param name="normalizedPath">非空引用、已经通过相对路径检查的字符串。</param>
    /// <returns>以小写 .png 结尾时返回 true，否则返回 false。</returns>
    /// <remarks>只比较后缀，不再次检查路径、读取字节或验证图像格式。</remarks>
    /// <exception cref="NullReferenceException">normalizedPath 为 null。</exception>
    public static bool IsPngPath(string normalizedPath) =>
        normalizedPath.EndsWith(".png", StringComparison.Ordinal);

    /// <summary>判断路径后缀是否属于当前读取边界允许的 JSON 或 PNG 数据文件。</summary>
    /// <param name="normalizedPath">非空引用、已经通过相对路径检查的字符串。</param>
    /// <returns>以小写 .json 或 .png 结尾时返回 true，否则返回 false。</returns>
    /// <remarks>后缀比较区分大小写；不读取文件或检查内容是否符合声明的格式。</remarks>
    /// <exception cref="NullReferenceException">normalizedPath 为 null。</exception>
    /// <example>外部读取接受 copper.voxels.json，排除 .dll、.cs、.gd 和 .pck；见 <c>ExternalPathTests.Physical_mod_root_discovers_only_direct_children_and_ignores_code_files</c>。</example>
    public static bool IsAllowedDataFile(string normalizedPath) =>
        normalizedPath.EndsWith(".json", StringComparison.Ordinal) ||
        IsPngPath(normalizedPath);

    /// <summary>检查冒号前的标识是否符合 URI 前缀格式：以 ASCII 字母开头，后续只含字母、数字、加号、连字符或点号；不要求冒号后有双斜杠。</summary>
    private static bool HasUriSchemePrefix(string path)
    {
        if (path.Length == 0 || !IsAsciiLetter(path[0]))
            return false;
        var colon = path.IndexOf(':');
        if (colon <= 0)
            return false;
        for (var i = 1; i < colon; i++)
        {
            var c = path[i];
            if (IsAsciiLetter(c) || char.IsAsciiDigit(c) || c is '+' or '-' or '.')
                continue;
            return false;
        }

        return true;
    }

    private static bool IsAsciiLetter(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');

    /// <summary>按区分大小写的正斜杠路径文本判断候选是否等于根或带有根目录前缀。</summary>
    /// <param name="root">非空引用的根路径，采用正斜杠且点段已由调用方处理。</param>
    /// <param name="candidate">非空引用的候选路径，采用与根相同的路径表示和大小写规则。</param>
    /// <returns>忽略末尾斜杠后与根相等，或以根加一个分隔斜杠为前缀时返回 true。</returns>
    /// <remarks>仅执行字符串比较，不解析点段、链接或物理路径，输入路径的规范化由调用方负责。</remarks>
    /// <exception cref="NullReferenceException">root 或 candidate 为 null。</exception>
    public static bool IsUnderRoot(string root, string candidate)
    {
        var prefix = root.TrimEnd('/') + "/";
        return candidate.StartsWith(prefix, StringComparison.Ordinal) ||
               string.Equals(candidate.TrimEnd('/'), root.TrimEnd('/'), StringComparison.Ordinal);
    }
}

/// <summary>收集有数量上限的内容诊断，并单独记录当前校验范围是否已无效。</summary>
/// <remarks>诊断列表通过只读接口暴露，但随追加而变化。达到保留上限后继续更新无效状态，丢弃新的诊断条目。</remarks>
/// <example>150 个非法名称只保留 100 条诊断，仍拒绝内置包；见 <c>ContentReloaderTests.Diagnostics_are_capped_per_package_and_still_reject</c>。</example>
internal sealed class DiagnosticBag
{
    private readonly List<ContentDiagnostic> _items = new();

    /// <summary>获取或设置后续追加诊断使用的来源路径；可以为 null，更改不会改写已记录条目的来源。</summary>
    public string? Source { get; set; }
    /// <summary>获取当前校验范围是否已被标记无效；一旦为 true，本容器不会将其恢复为 false。</summary>
    public bool Invalid { get; private set; }
    /// <summary>获取当前已保留诊断的只读接口；它直接指向内部列表，后续追加会反映到此视图。</summary>
    public IReadOnlyList<ContentDiagnostic> Items => _items;

    /// <summary>按请求更新无效状态，并在尚未达到上限时追加一条带当前来源的诊断。</summary>
    /// <param name="severity">要记录的诊断级别；不独立决定是否标记无效。</param>
    /// <param name="code">非空的机器可读诊断代码。</param>
    /// <param name="message">非空引用的诊断说明，允许空字符串。</param>
    /// <param name="jsonPath">字段或相关路径；不适用时为 null。</param>
    /// <param name="invalidate">为 true 时先将当前范围标记无效；为 false 时保留已有无效状态。</param>
    /// <remarks>达到 <see cref="ContentLimits.MaxDiagnosticsPerPackage"/> 后不创建条目或截断提示，仍执行无效状态更新。条目构造失败时，已经设置的无效状态也不会撤回。</remarks>
    /// <exception cref="ArgumentNullException">未达到保留上限且 code 或 message 为 null。</exception>
    /// <exception cref="ArgumentException">未达到保留上限且 code 为空字符串。</exception>
    public void Add(
        ContentDiagnosticSeverity severity,
        string code,
        string message,
        string? jsonPath = null,
        bool invalidate = true)
    {
        if (invalidate)
            Invalid = true;
        if (_items.Count >= ContentLimits.MaxDiagnosticsPerPackage)
            return;
        _items.Add(new ContentDiagnostic(severity, code, message, Source, jsonPath));
    }

    /// <summary>将当前校验范围标记无效，不追加诊断条目。</summary>
    public void MarkInvalid() => Invalid = true;
}

/// <summary>提供常用诊断级别及是否标记当前范围无效的追加方式。</summary>
/// <remarks>Warn 和 Error 都标记无效；两者共用诊断容器的条目上限。</remarks>
internal static class DiagnosticBagExtensions
{
    /// <summary>追加警告级诊断，并将当前校验范围标记无效。</summary>
    /// <param name="bag">非空的诊断容器。</param>
    /// <param name="code">非空诊断代码。</param>
    /// <param name="message">非空引用的诊断说明。</param>
    /// <param name="jsonPath">相关字段或路径；未提供时为 null。</param>
    public static void Warn(this DiagnosticBag bag, string code, string message, string? jsonPath = null) =>
        bag.Add(ContentDiagnosticSeverity.Warning, code, message, jsonPath);

    /// <summary>追加错误级诊断，并将当前校验范围标记无效。</summary>
    /// <param name="bag">非空的诊断容器。</param>
    /// <param name="code">非空诊断代码。</param>
    /// <param name="message">非空引用的诊断说明。</param>
    /// <param name="jsonPath">相关字段或路径；未提供时为 null。</param>
    public static void Error(this DiagnosticBag bag, string code, string message, string? jsonPath = null) =>
        bag.Add(ContentDiagnosticSeverity.Error, code, message, jsonPath);
}
