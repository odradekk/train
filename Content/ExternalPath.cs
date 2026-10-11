// 属于内容模块。
// 检查外部物理路径的根目录包含关系与现存路径段属性，供资源访问边界使用。
namespace Train.Content;

/// <summary>提供外部内容路径的规范化、包含检查和重解析点防护。</summary>
/// <remarks>包含关系以平台完整路径文本为准，链接判断依据文件属性。可信根由调用方选定，链检查不追溯根之上的祖先；检查结果不保证目标已存在，也不锁定后续访问。</remarks>
/// <example>路径仍在 resources 的文本范围内，但经过 escaped 链接目录时被拒绝；见 <c>ExternalPathTests.Linked_or_reparse_entries_fail_closed_instead_of_committing_a_partial_package</c>。</example>
public static class ExternalPath
{
    /// <summary>按平台完整路径规则将输入转换为绝对路径。</summary>
    /// <param name="path">非 null、非空的物理路径；相对路径按当前工作目录和卷信息解析。</param>
    /// <returns>Path.GetFullPath 返回的完整路径；不保证路径已存在、为目录或不经过链接。</returns>
    /// <remarks>不执行根目录包含检查；其他平台转换异常原样传播，规则见 <see href="https://learn.microsoft.com/en-us/dotnet/api/system.io.path.getfullpath?view=net-10.0">.NET 完整路径文档</see>。</remarks>
    /// <exception cref="ArgumentNullException">path 为 null。</exception>
    /// <exception cref="ArgumentException">path 为空、包含 NUL，或平台无法将输入解释为有效路径，包括 Windows 上的纯空白路径。</exception>
    /// <exception cref="PathTooLongException">平台报告路径长度超过系统限制。</exception>
    /// <exception cref="IOException">平台取得完整路径时发生其他 I/O 错误。</exception>
    public static string NormalizeRoot(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Path.GetFullPath(path);
    }

    /// <summary>读取指定物理路径的属性，区分确认缺失与无法读取状态。</summary>
    /// <param name="path">要查询的文件或目录路径；相对路径遵循平台当前工作目录规则。</param>
    /// <param name="attributes">成功读取现存路径时的属性；缺失或查询失败时为默认值。</param>
    /// <param name="exists">属性读取成功时为 true；缺失或查询失败时为 false，须同时检查返回值。</param>
    /// <returns>读取属性成功，或收到文件、目录不存在异常时为 true；其他异常均转换为 false。</returns>
    /// <remarks>无效或 null 输入、权限和其他 I/O 错误不会向外抛出，不保留具体原因。此方法只提供属性，不判定目录性、链接安全或祖先链。</remarks>
    /// <example>查询 escaped 链接目录的属性后，IsReparsePoint 根据 ReparsePoint 标记返回 true；见 <c>ExternalPathTests.Linked_or_reparse_entries_fail_closed_instead_of_committing_a_partial_package</c>。</example>
    public static bool TryStat(string path, out FileAttributes attributes, out bool exists)
    {
        attributes = default;
        exists = false;
        try
        {
            attributes = File.GetAttributes(path);
            exists = true;
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>判断指定路径能否读取到重解析点属性。</summary>
    /// <param name="path">要查询的物理路径，只检查该节点，不检查祖先链。</param>
    /// <returns>属性读取成功、路径存在且包含 ReparsePoint 标记时为 true；普通、缺失或无法查询的路径均为 false。</returns>
    /// <remarks>false 不表示路径存在、可读或其祖先安全；需要拒绝无法验证的路径时使用链检查。</remarks>
    /// <example>linked-parent 是重解析点，其下 mods 本身不是，但可信链检查仍拒绝该目录；见 <c>ExternalPathTests.Trusted_chain_rejects_a_mods_path_reached_through_a_reparse_ancestor</c>。</example>
    public static bool IsReparsePoint(string path)
    {
        if (!TryStat(path, out var attributes, out var exists) || !exists)
            return false;
        return (attributes & FileAttributes.ReparsePoint) != 0;
    }

    /// <summary>按规范化后的路径文本判断候选是否等于根或处于根目录之下。</summary>
    /// <param name="root">调用方选定的物理根路径，可为绝对或相对路径。</param>
    /// <param name="candidate">待判断的物理路径，可与根相等；相对路径按当前工作目录解析。</param>
    /// <returns>完整路径去除末尾分隔符后相等，或候选具有根加目录分隔符的前缀时为 true；输入无效或越界时为 false。</returns>
    /// <remarks>Windows 使用 OrdinalIgnoreCase，其他平台使用 Ordinal；包含判断不验证存在、目录属性或链接目标。空、null、NUL 或完整路径转换失败的输入被拒绝。</remarks>
    /// <example>mods 下的 pretty/resources/copper.png 可接受，mods/../outside.json 被拒绝；见 <c>ExternalPathTests.Lexical_containment_rejects_parent_and_sibling_escapes</c>。</example>
    public static bool IsSafeUnderRoot(string root, string candidate)
    {
        if (!TryGetFullPath(root, out var rootFull) || !TryGetFullPath(candidate, out var candidateFull))
            return false;

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var rootTrimmed = TrimSeparators(rootFull);
        var candidateTrimmed = TrimSeparators(candidateFull);
        if (string.Equals(rootTrimmed, candidateTrimmed, comparison))
            return true;

        var prefix = rootTrimmed + Path.DirectorySeparatorChar;
        return candidateTrimmed.StartsWith(prefix, comparison);
    }

    /// <summary>检查根到候选的路径链是否无效、无法读取或包含重解析点。</summary>
    /// <param name="root">调用方选定的可信物理根；只检查此根及其下方枚举出的路径段。</param>
    /// <param name="candidate">待检查的文件或目录路径，必须在根的文本范围内。</param>
    /// <returns>路径无效、越界、任一路径段无法查询或现存段含重解析点时为 true；其余情况为 false。</returns>
    /// <remarks>缺失路径段跳过，现存普通文件也不会因缺少 Directory 属性而被拒绝。因此 false 不保证目标存在或整条链都是目录。链的生成与卷根表示限制见 <see cref="EnumerateChain"/>。</remarks>
    /// <example>resources/escaped/hidden.voxels.json 经过链接目录时，IsUnsafe 通过此链检查将其拒绝；见 <c>ExternalPathTests.Linked_or_reparse_entries_fail_closed_instead_of_committing_a_partial_package</c>。</example>
    public static bool AnyReparsePointOnChain(string root, string candidate)
    {
        if (!TryGetFullPath(root, out var rootFull) || !TryGetFullPath(candidate, out var candidateFull))
            return true;
        if (!IsSafeUnderRoot(rootFull, candidateFull))
            return true;

        foreach (var segment in EnumerateChain(rootFull, candidateFull))
        {
            if (!TryStat(segment, out var attributes, out var exists))
                return true;
            if (!exists)
                continue;
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                return true;
        }

        return false;
    }

    /// <summary>先检查根目录包含关系，再检查路径链的可查询性与重解析点。</summary>
    /// <param name="root">调用方选定的可信物理根。</param>
    /// <param name="candidate">待访问的文件或目录路径。</param>
    /// <returns>路径无效、越界或链检查不通过时为 true；通过这些检查时为 false。</returns>
    /// <remarks>缺失段和现存普通文件沿用 AnyReparsePointOnChain 的规则；此方法不证明目标存在、类型正确或后续访问一定成功。</remarks>
    /// <example>mods/../secret.png 越过根目录时被拒绝；见 <c>ExternalPathTests.Lexical_containment_rejects_parent_and_sibling_escapes</c>。</example>
    public static bool IsUnsafe(string root, string candidate) =>
        !IsSafeUnderRoot(root, candidate) || AnyReparsePointOnChain(root, candidate);

    /// <summary>验证候选未越过可信根，并检查链上现存节点都是可查询的非重解析点目录。</summary>
    /// <param name="trustedRoot">调用方选定的可信物理根；不检查此根之上的祖先。</param>
    /// <param name="candidate">待验证或稍后创建的目录路径，可以尚未存在。</param>
    /// <param name="error">成功时为 null；失败时说明路径无效、越界、属性无法读取、含链接或节点不是目录。</param>
    /// <returns>路径包含关系通过，且所有枚举出的现存段满足目录条件时为 true；其余情况为 false。</returns>
    /// <remarks>缺失路径段跳过，所以返回 true 不保证根或候选已经存在。与 AnyReparsePointOnChain 不同，本方法拒绝现存普通文件并提供失败说明；链的生成与卷根表示限制见 <see cref="EnumerateChain"/>。</remarks>
    /// <example>trusted 下的 linked-parent/mods 即使末端目录本身不是重解析点，也因链接祖先被拒绝；见 <c>ExternalPathTests.Trusted_chain_rejects_a_mods_path_reached_through_a_reparse_ancestor</c>。越界拒绝见 <c>ExternalPathTests.Trusted_chain_rejects_a_path_outside_the_trusted_root</c>。</example>
    public static bool TryValidateTrustedChain(string trustedRoot, string candidate, out string? error)
    {
        error = null;
        if (!TryGetFullPath(trustedRoot, out var rootFull) || !TryGetFullPath(candidate, out var candidateFull))
        {
            error = "Path is invalid.";
            return false;
        }

        if (!IsSafeUnderRoot(rootFull, candidateFull))
        {
            error = "Path escapes the trusted root.";
            return false;
        }

        foreach (var segment in EnumerateChain(rootFull, candidateFull))
        {
            if (!TryStat(segment, out var attributes, out var exists))
            {
                error = "Path segment could not be read.";
                return false;
            }

            if (!exists)
                continue;

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                error = "Path is a linked or reparse path.";
                return false;
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                error = "Path segment is not a directory.";
                return false;
            }
        }

        return true;
    }

    /// <summary>将路径中的正斜杠换成本机分隔符，与根连接后转换为完整路径。</summary>
    /// <param name="root">用于组合的物理根路径；相对根按当前工作目录解析。</param>
    /// <param name="relativeForwardSlash">要组合的非 null 路径文本，调用方应先按包内相对路径规则校验。</param>
    /// <returns>Path.Combine 后经 Path.GetFullPath 处理的路径；不保证结果仍在 root 内或已经存在。</returns>
    /// <remarks>不调用 ContentPath.TryNormalize 或包含、链接检查；点段可能越界，带根的第二参数会覆盖 root。调用方须另行检查结果，底层平台路径异常原样传播。</remarks>
    /// <exception cref="ArgumentNullException">root 为 null。</exception>
    /// <exception cref="NullReferenceException">relativeForwardSlash 为 null。</exception>
    /// <exception cref="ArgumentException">组合结果为空、包含 NUL 或不能按平台规则取得完整路径。</exception>
    /// <exception cref="PathTooLongException">平台报告路径长度超过系统限制。</exception>
    /// <exception cref="IOException">平台取得完整路径时发生其他 I/O 错误。</exception>
    /// <example>外部读取先组合 escaped/hidden.voxels.json，再由 IsUnsafe 拒绝链接；见 <c>ExternalPathTests.Linked_or_reparse_entries_fail_closed_instead_of_committing_a_partial_package</c>。</example>
    public static string CombineUnder(string root, string relativeForwardSlash)
    {
        var native = relativeForwardSlash.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(root, native));
    }

    /// <summary>从候选向父级收集路径，再按父到子的顺序返回已收集的链。</summary>
    /// <param name="root">希望停止收集的根路径；调用方须先确认它是候选的祖先或与候选相等。</param>
    /// <param name="candidate">链末端的物理路径；相对路径先按当前工作目录转换。</param>
    /// <returns>调用时已构造的路径序列，元素去除末尾分隔符；普通包含路径返回根到候选，文件系统根的表示有下述限制。</returns>
    /// <remarks>
    /// 不自行验证包含关系、存在或链接；若根不在祖先链中，会走到无法继续取得父路径为止，结果可能不含指定根。比较在 Windows 忽略大小写，其他平台区分大小写；底层路径异常原样传播。
    /// 移除末尾分隔符也作用于文件系统根：Windows 的 C:\ 变为卷相对文本 C:，Unix 的 / 变为空串而不加入链；后续属性检查沿用这些文本，不恢复根分隔符。
    /// </remarks>
    /// <exception cref="ArgumentNullException">root 或 candidate 为 null。</exception>
    /// <exception cref="ArgumentException">输入为空、包含 NUL 或不能按平台规则取得完整路径。</exception>
    /// <exception cref="PathTooLongException">平台报告路径长度超过系统限制。</exception>
    /// <exception cref="IOException">平台取得完整路径时发生其他 I/O 错误。</exception>
    /// <example>可信链验证会枚举可信根、其下的 linked-parent 目录和 mods 目录，因中间链接拒绝；见 <c>ExternalPathTests.Trusted_chain_rejects_a_mods_path_reached_through_a_reparse_ancestor</c>。</example>
    public static IEnumerable<string> EnumerateChain(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var rootFull = TrimSeparators(Path.GetFullPath(root));
        var current = TrimSeparators(Path.GetFullPath(candidate));
        var stack = new Stack<string>();
        while (current.Length > 0)
        {
            stack.Push(current);
            if (string.Equals(current, rootFull, comparison))
                break;
            var parent = TrimSeparators(Path.GetDirectoryName(current) ?? "");
            if (parent.Length == 0 || string.Equals(parent, current, comparison))
                break;
            current = parent;
        }

        return stack;
    }

    /// <summary>将非空、无 NUL 的路径转换为平台完整路径，失败或异常时返回 false。</summary>
    /// <param name="path">待转换的物理路径，null 或空字符串也视为失败。</param>
    /// <param name="fullPath">成功时为完整路径，失败时为空字符串。</param>
    /// <returns>通过输入检查且平台转换成功时为 true。</returns>
    private static bool TryGetFullPath(string path, out string fullPath)
    {
        fullPath = "";
        try
        {
            if (string.IsNullOrEmpty(path) || path.Contains('\0', StringComparison.Ordinal))
                return false;
            fullPath = Path.GetFullPath(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>移除全部末尾主、备用目录分隔符；不保留卷根或 Unix 根的分隔符。</summary>
    private static string TrimSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
