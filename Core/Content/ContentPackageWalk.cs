// 属于内容模块。
// 在内容源提供的目录树上发现数据文件，报告需要按包拒绝的路径问题。
namespace Train.Core.Content;

/// <summary>表示内容目录遍历器看到的一个直接子项。</summary>
/// <param name="Name">不为 null、不含父目录或路径分隔符的子项名称；遍历器忽略 . 和 ..。</param>
/// <param name="IsDirectory">内容源判断该子项是否为目录；非链接目录会继续递归列举。</param>
/// <param name="IsLink">内容源判断该子项是否为符号链接或重解析点；为 true 时不进入目录或收集文件。</param>
/// <remarks>名称和标记原样保存，不在构造时校验或探测文件系统。链接标记优先于目录标记和文件后缀检查。</remarks>
/// <example>linked-dir 同时标记为目录和链接时，其 hidden.voxels.json 不会被发现；见 <c>ContentPackageWalkTests.Linked_directory_invalidates_the_package_and_does_not_contribute_files</c>。</example>
public readonly record struct ContentTreeEntry(string Name, bool IsDirectory, bool IsLink);

/// <summary>表示一次内容文件发现得到的路径、诊断和包有效性标记。</summary>
/// <remarks>输入集合直接通过只读接口暴露，不复制或冻结；调用方须保持集合内容稳定。无效结果仍可包含已发现文件，使用方须同时检查 Invalid。</remarks>
/// <example>保留 safe.voxels.json 的同时，因 linked-dir 将结果标记无效；见 <c>ContentPackageWalkTests.Linked_directory_invalidates_the_package_and_does_not_contribute_files</c>。</example>
public sealed class ContentFileDiscovery
{
    /// <summary>取得已发现的包内相对路径；Discover 返回按 Ordinal 排列的路径，构造函数本身不排序或校验。</summary>
    public IReadOnlyList<string> Files { get; }
    /// <summary>取得发现期间保留的诊断；无问题时可为空集合。</summary>
    public IReadOnlyList<ContentDiagnostic> Diagnostics { get; }
    /// <summary>取得本轮发现是否要求按包拒绝；为 true 时文件集合仍可能非空。</summary>
    public bool Invalid { get; }

    /// <summary>保存调用方提供的文件集合、诊断集合和有效性标记。</summary>
    /// <param name="files">调用方已完成路径检查的包内相对路径集合，允许为空集合。</param>
    /// <param name="diagnostics">发现诊断集合，允许为空集合。</param>
    /// <param name="invalid">是否要求按包拒绝；不根据文件或诊断自动推导。</param>
    /// <remarks>原样保留集合引用，不检查 null、路径格式、顺序或诊断与标记的一致性。</remarks>
    public ContentFileDiscovery(
        IReadOnlyList<string> files,
        IReadOnlyList<ContentDiagnostic> diagnostics,
        bool invalid)
    {
        Files = files;
        Diagnostics = diagnostics;
        Invalid = invalid;
    }

    /// <summary>将调用方提供的文件集合包装为有效且无诊断的发现结果。</summary>
    /// <param name="files">调用方已完成路径检查的包内相对路径集合，允许为空集合。</param>
    /// <returns>Files 沿用输入集合、Diagnostics 为空且 Invalid 为 false 的结果。</returns>
    /// <remarks>不校验、排序或复制文件集合；有效标记表示调用方的结论。</remarks>
    /// <example>内存内容源将 voxels/ores.voxels.json 包装为有效发现结果，再交由加载器解析；见 <c>ContentReloaderTests.Initial_valid_voxel_catalog_commits</c>。</example>
    public static ContentFileDiscovery Of(IReadOnlyList<string> files) =>
        new(files, Array.Empty<ContentDiagnostic>(), invalid: false);

    /// <summary>按内容包身份筛选玩家蓝图文件，其他包的发现结果原样返回。</summary>
    /// <param name="package">决定是否应用玩家蓝图身份规则的内容包。</param>
    /// <param name="discovery">已经完成目录扫描的蓝图文件发现结果。</param>
    /// <returns>玩家蓝图包仅保留与包命名空间加 .composites.json 完全相等的根级文件；其他包返回原结果。</returns>
    /// <remarks>文件名按 Ordinal 比较；筛选保持原顺序，并原样保留诊断和无效标记。</remarks>
    /// <exception cref="NullReferenceException"><paramref name="package"/> 为 null，或筛选玩家蓝图包时 <paramref name="discovery"/> 为 null。</exception>
    /// <example>命名空间为 cccccccc-dddd-4eee-8fff-000000000001 时，仅保留同名的 .composites.json 文件；见 <c>CompositeContentTests.Player_blueprint_uses_file_uuid_namespace_and_display_name</c>。</example>
    public static ContentFileDiscovery ForCompositePackage(
        ContentPackage package,
        ContentFileDiscovery discovery)
    {
        if (package.Kind != ContentPackageKind.PlayerBlueprint)
            return discovery;
        var expected = package.Namespace + ".composites.json";
        return new ContentFileDiscovery(
            discovery.Files.Where(file => string.Equals(file, expected, StringComparison.Ordinal)).ToArray(),
            discovery.Diagnostics,
            discovery.Invalid);
    }
}

/// <summary>在与存储实现无关的目录树上发现内容文件。</summary>
/// <remarks>目录访问和链接识别由内容源提供，本类型不直接探测文件系统；发现链接或无法列举的目录后，标记包无效并继续处理其他子项。</remarks>
/// <example>blocked 目录无法列举时，仍返回 visible.voxels.json，但结果无效；见 <c>ContentPackageWalkTests.Unlistable_directory_invalidates_the_package_instead_of_skipping_the_subtree</c>。</example>
public static class ContentPackageWalk
{
    /// <summary>递归收集指定后缀且路径格式有效的文件，遇到链接或不可列举目录时标记整个包无效。</summary>
    /// <param name="root">采用正斜杠、不带末尾斜杠的包资源根，例如 res://root；原样传给首次列举回调。</param>
    /// <param name="listChildren">同步列举指定目录直接子项的非 null 回调；空集合表示空目录，null 表示无法安全列举。须提供有限的目录树并正确标记链接。</param>
    /// <param name="fileSuffix">按 Ordinal 区分大小写匹配的非 null 文件后缀，默认 .voxels.json；空字符串匹配所有普通文件。</param>
    /// <returns>按 Ordinal 排序且不去重的正斜杠相对路径、按遍历顺序保留的诊断和无效标记；无效时仍保留其他子树已发现的文件。</returns>
    /// <remarks>
    /// 跳过 . 和 ..；其余子项只要标记为链接就使包无效，与目录标记和文件后缀无关。非链接目录递归列举，普通文件后缀不匹配或相对路径未通过 ContentPath.TryNormalize 时仅忽略该文件。
    /// 诊断最多保留 ContentLimits.MaxDiagnosticsPerPackage 条，达到上限后仍标记无效。根路径不在此处规范化；回调异常原样向上传播，不转换成诊断。
    /// </remarks>
    /// <exception cref="ArgumentNullException">遇到普通文件时 fileSuffix 为 null。</exception>
    /// <exception cref="NullReferenceException">listChildren 为 null，或实际拼接路径时 root 为 null，或检查普通文件后缀时子项名称为 null。</exception>
    /// <example>res://root 下的 ok.voxels.json 被保留，链接文件 alias.voxels.json 使结果无效且不进入文件列表；见 <c>ContentPackageWalkTests.Linked_goods_file_invalidates_the_package_instead_of_being_skipped</c>。</example>
    public static ContentFileDiscovery Discover(
        string root,
        Func<string, IReadOnlyList<ContentTreeEntry>?> listChildren,
        string fileSuffix = ".voxels.json")
    {
        var files = new List<string>();
        var bag = new DiagnosticBag();
        Collect(root, root, listChildren, files, bag, fileSuffix);
        files.Sort(StringComparer.Ordinal);
        return new ContentFileDiscovery(files, bag.Items.ToArray(), bag.Invalid);
    }

    /// <summary>创建一条拒绝链接或重解析点路径的诊断。</summary>
    /// <param name="path">已由调用方判定不可接受的路径；原样记录为 Source，不校验格式。</param>
    /// <returns>严重程度为 Error、代码为 unsafe_linked_path、Source 为输入路径且 JsonPath 为 null 的诊断。</returns>
    /// <remarks>仅创建诊断，不检测路径、遍历目录或设置发现结果的无效标记。</remarks>
    /// <example>将 res://linked 的路径诊断放入无效发现结果后，加载器拒绝发布；见 <c>ContentReloaderTests.Invalid_discovery_rejects_instead_of_committing_partial_files</c>。</example>
    public static ContentDiagnostic LinkedPathDiagnostic(string path) =>
        new(
            ContentDiagnosticSeverity.Error,
            ContentDiagnosticCodes.UnsafeLinkedPath,
            "Linked or reparse-point content is not allowed.",
            path);

    /// <summary>按回调提供的子项顺序递归收集文件，将链接和无法列举的目录记为包无效；其他文件仅在后缀及相对路径检查通过后追加。</summary>
    private static void Collect(
        string root,
        string current,
        Func<string, IReadOnlyList<ContentTreeEntry>?> listChildren,
        List<string> files,
        DiagnosticBag bag,
        string fileSuffix)
    {
        var entries = listChildren(current);
        if (entries is null)
        {
            AddLinked(bag, current);
            return;
        }

        foreach (var entry in entries)
        {
            if (entry.Name is "." or "..")
                continue;
            var path = current.TrimEnd('/') + "/" + entry.Name;
            if (entry.IsLink)
            {
                AddLinked(bag, path);
                continue;
            }

            if (entry.IsDirectory)
            {
                Collect(root, path, listChildren, files, bag, fileSuffix);
                continue;
            }

            if (!entry.Name.EndsWith(fileSuffix, StringComparison.Ordinal))
                continue;
            var relative = path.StartsWith(root + "/", StringComparison.Ordinal)
                ? path[(root.Length + 1)..]
                : entry.Name;
            if (ContentPath.TryNormalize(relative, out var normalized))
                files.Add(normalized);
        }
    }

    /// <summary>将路径问题记为错误并标记包无效；路径写入诊断的 JsonPath，本容器未设置 Source。</summary>
    private static void AddLinked(DiagnosticBag bag, string path)
    {
        var diagnostic = LinkedPathDiagnostic(path);
        bag.Error(diagnostic.Code, diagnostic.Message, path);
    }
}
