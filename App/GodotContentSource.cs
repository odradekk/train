// 属于 Godot 项目的内容适配模块。
// 将 Godot 虚拟文件访问、外部物理内容包和 PNG 解码接入内容读取边界。
using Train.Content;
using Train.Content.Voxels;
using Godot;
using Train.App.Logging;

namespace Train.App;

/// <summary>通过 Godot 和物理文件系统发现内容包、读取资源并解码 PNG。</summary>
/// <remarks>路径与日志依赖由宿主提供，构造时不访问目录；实例保留最近一次列包诊断，不提供并发同步。内容定义、引用及图片用途的校验由内容加载器负责。</remarks>
/// <example>真实 Godot 中读取内置 JSON 并发布 core:grain 的检查见 <c>scripts/checks/run-app-check.ps1</c> 的 content 检查。</example>
public sealed class GodotContentSource : IContentSource
{
    /// <summary>内置资源的默认 Godot 路径，供编辑器和导出运行读取同一组资源。</summary>
    /// <remarks>资源放在 builtin/ 下，与 Content/ 源码目录分开，避免 Windows 上目录名称的冲突。</remarks>
    public const string BuiltInResourcesRoot = "res://builtin/core/resources";
    /// <summary>Mod 根的 Godot 逻辑路径；实际扫描位置由 <see cref="UserDataDirectory"/> 确定。</summary>
    public const string ModsGodotPath = ExternalContent.ModsGodotPath;
    /// <summary>玩家蓝图根的 Godot 逻辑路径；实际扫描位置由 <see cref="UserDataDirectory"/> 确定。</summary>
    public const string BlueprintsGodotPath = ExternalContent.BlueprintsGodotPath;

    /// <summary>可选的扫描日志实例；null 表示不记录此适配器的日志。</summary>
    private readonly TrainLogger? _logger;
    /// <summary>宿主注入的物理用户数据根；null 时在访问时查询 Godot 用户目录。</summary>
    private readonly string? _userDataDirectory;
    /// <summary>内置资源根的原始路径，未注入有效文本时使用默认 Godot 路径。</summary>
    private readonly string _builtInResourcesRoot;
    /// <summary>内置根不以区分大小写的 res:// 前缀开头时为 true，决定内置包使用物理访问。</summary>
    private readonly bool _usesPhysicalBuiltIn;
    /// <summary>普通运行允许准备用户内容目录；只读检查设为 false，只扫描已有目录。</summary>
    private readonly bool _createUserDirectories;
    /// <summary>最近一次完成列包后保留的诊断列表；初始为空，在每次完成扫描时替换引用。</summary>
    private IReadOnlyList<ContentDiagnostic> _scanDiagnostics = Array.Empty<ContentDiagnostic>();

    /// <summary>保存宿主依赖，并根据内置根的路径前缀选择虚拟或物理访问方式。</summary>
    /// <param name="logger">扫描日志实例；null 表示关闭此适配器的日志输出。</param>
    /// <param name="userDataDirectory">可信的物理用户数据根；null 时使用 Godot 用户目录，其他文本原样保存。</param>
    /// <param name="builtInResourcesRoot">内置资源根；null、空串或全空白时使用 <see cref="BuiltInResourcesRoot"/>，其他文本原样保存。</param>
    /// <param name="createUserDirectories">是否在扫描时创建 mods 和 blueprints；false 用于不改写用户内容的启动检查。</param>
    /// <remarks>仅以 Ordinal 比较 res:// 前缀决定访问方式，不修剪路径、不检查存在性或链接，也不创建目录；物理用户数据根应由宿主选定。</remarks>
    public GodotContentSource(
        TrainLogger? logger = null,
        string? userDataDirectory = null,
        string? builtInResourcesRoot = null,
        bool createUserDirectories = true)
    {
        _logger = logger;
        _userDataDirectory = userDataDirectory;
        _builtInResourcesRoot = string.IsNullOrWhiteSpace(builtInResourcesRoot)
            ? BuiltInResourcesRoot
            : builtInResourcesRoot;
        _usesPhysicalBuiltIn = !_builtInResourcesRoot.StartsWith("res://", StringComparison.Ordinal);
        _createUserDirectories = createUserDirectories;
    }

    /// <summary>获取注入的物理用户数据根；未注入时，每次访问均查询 Godot 用户目录。</summary>
    public string UserDataDirectory => _userDataDirectory ?? OS.GetUserDataDir();
    /// <summary>获取构造时确定的内置资源根，可能为 Godot 逻辑路径或物理路径。</summary>
    public string BuiltInResourcesRootPath => _builtInResourcesRoot;

    /// <summary>探测内置内容，并扫描用户目录中的 Mod 与玩家蓝图包。</summary>
    /// <returns>新建的包列表，先包含内置包，再包含已接受的 Mod 和按文件名发现的玩家蓝图；内置根不可用时仍保留 Available 为 false 的内置包。</returns>
    /// <remarks>
    /// 普通运行需要时创建用户根中的 mods 和 blueprints；只读检查只扫描已有目录。
    /// 未注入用户根且允许创建目录时，还会通过 Godot 准备这两个目录。
    /// 物理目录准备或枚举失败记为对应根不可用诊断；Mod 扫描代码块的异常也转为 mods_root_unavailable。
    /// 正常完成时替换最近一次扫描诊断，不冻结返回列表；未被转换的 Godot、路径或日志异常向上传播。
    /// 本方法不读取体素或蓝图定义正文，也不解码资源；目录可用标记不代替后续的链接与文件校验。
    /// </remarks>
    /// <example>隔离用户目录中的 Mod 根为空或不可用时，内置 grain 仍可发布；见 <c>scripts/checks/run-app-check.ps1</c> 的 mods-export 检查。</example>
    public IReadOnlyList<ContentPackage> ListPackages()
    {
        bool available;
        if (_usesPhysicalBuiltIn)
        {
            available = Directory.Exists(_builtInResourcesRoot);
        }
        else
        {
            using var probe = DirAccess.Open(_builtInResourcesRoot);
            available = probe is not null;
        }
        var packages = new List<ContentPackage>
        {
            new(VoxelDefinitionId.BuiltInNamespace, _builtInResourcesRoot, available)
        };

        var userData = UserDataDirectory;
        var physical = Path.Combine(userData, "mods");
        _logger?.Info(
            ContentLog.Category,
            ContentLog.ModsRootEventId,
            "Mods directory resolved.",
            new Dictionary<string, object?>
            {
                ["godot_path"] = ModsGodotPath,
                ["physical_path"] = physical
            });

        if (_userDataDirectory is null && _createUserDirectories)
        {
            try
            {
                using var user = DirAccess.Open("user://");
                user?.MakeDir("mods");
                user?.MakeDir("blueprints");
            }
            catch
            {
                // 是否可用以后续物理目录创建和安全检查的结果为准。
            }
        }

        var diagnostics = new List<ContentDiagnostic>();
        string? error = null;
        if (_createUserDirectories && !ExternalContent.TryEnsureDirectory(userData, physical, out error))
        {
            diagnostics.Add(ExternalContent.ModsRootUnavailable(ModsGodotPath, error ?? "Mods directory is unavailable."));
        }
        else if (!ExternalContent.TryListDirectChildren(userData, physical, out var children, out var listError))
        {
            diagnostics.Add(ExternalContent.ModsRootUnavailable(ModsGodotPath, listError ?? "Mods directory could not be read."));
        }
        else
        {
            try
            {
                var scan = ModDiscovery.Scan(children);
                diagnostics.AddRange(scan.Diagnostics);
                packages.AddRange(scan.Packages);
                _logger?.Info(
                    ContentLog.Category,
                    ContentLog.ModsScanSummaryEventId,
                    "Mod scan finished.",
                    new Dictionary<string, object?>
                    {
                        ["godot_path"] = ModsGodotPath,
                        ["physical_path"] = physical,
                        ["candidate_count"] = scan.CandidateCount,
                        ["accepted_count"] = scan.AcceptedCount
                    });
            }
            catch (Exception exception)
            {
                diagnostics.Add(ExternalContent.ModsRootUnavailable(ModsGodotPath, exception.Message));
            }
        }

        packages.AddRange(ListBlueprintPackages(userData, diagnostics));
        _scanDiagnostics = diagnostics;
        return packages;
    }

    /// <summary>取得最近一次完成列包时保存的诊断。</summary>
    /// <returns>当前诊断列表的原引用；首次列包前为空，列表不保证被冻结。</returns>
    /// <remarks>下一次完成列包会替换引用；调用方应只读使用，不修改返回列表。</remarks>
    public IReadOnlyList<ContentDiagnostic> ListScanDiagnostics() => _scanDiagnostics;

    /// <summary>发现包内全部以小写 .json 结尾的定义文件。</summary>
    /// <param name="package">要扫描的内容包；内置包的访问方式由本实例构造参数确定。</param>
    /// <returns>规范化相对文件路径、发现诊断和包无效标记；无效结果仍可能保留部分文件。</returns>
    /// <remarks>物理包交给 ExternalContent，虚拟内置包交给 Godot 目录遍历；只发现文件，不解析格式或玩家蓝图身份。</remarks>
    /// <exception cref="NullReferenceException"><paramref name="package"/> 为 null。</exception>
    public ContentFileDiscovery ListDefinitionFiles(ContentPackage package)
    {
        if (package.Kind is ContentPackageKind.Mod or ContentPackageKind.PlayerBlueprint || IsPhysicalBuiltIn(package))
            return ExternalContent.ListDefinitionFiles(package.ResourcesRoot);

        if (IsLinkedOrUnresolved(package.ResourcesRoot))
            return new ContentFileDiscovery(
                Array.Empty<string>(),
                new[] { ContentPackageWalk.LinkedPathDiagnostic(package.ResourcesRoot) },
                invalid: true);

        return ContentPackageWalk.Discover(package.ResourcesRoot, ListEntries, ".json");
    }

    /// <summary>在给定字节上限内读取一个包内文件，并检查其资源根与路径链。</summary>
    /// <param name="package">文件所属的内容包。</param>
    /// <param name="normalizedRelativePath">包内规范相对路径；格式无效时返回普通缺失结果。</param>
    /// <param name="maxBytes">非负读取上限，单位为字节；等于上限的文件可以接受，零仅允许空文件。</param>
    /// <returns>成功时提供读取的原始字节；缺失、超限或链接拒绝时不提供字节，并设置相应标记。</returns>
    /// <remarks>
    /// 物理分支由 ExternalContent 读取，仅接受小写 .json 或 .png 后缀；虚拟分支检查相对路径和各段链接，不单独限制后缀。
    /// 虚拟路径不安全或无法解析时返回 Linked，文件不存在或无法打开时返回 Missing，长度超过上限时返回 LimitExceeded。
    /// 本实现不单独校验上限范围，调用方须满足非负前置条件；未转换为结果的异常原样传播。
    /// 返回数组不复制，后续解码和资产修订计算须使用稳定的同一份输入。
    /// </remarks>
    /// <exception cref="NullReferenceException"><paramref name="package"/> 为 null。</exception>
    public ContentBytesResult ReadFile(ContentPackage package, string normalizedRelativePath, int maxBytes)
    {
        if (package.Kind is ContentPackageKind.Mod or ContentPackageKind.PlayerBlueprint || IsPhysicalBuiltIn(package))
            return ExternalContent.ReadFile(package.ResourcesRoot, normalizedRelativePath, maxBytes);

        if (!ContentPath.TryNormalize(normalizedRelativePath, out var normalized))
            return ContentBytesResult.Missing();

        var virtualPath = package.ResourcesRoot.TrimEnd('/') + "/" + normalized;
        if (HasUnsafeLink(package.ResourcesRoot, virtualPath))
            return ContentBytesResult.Linked();
        if (!global::Godot.FileAccess.FileExists(virtualPath))
            return ContentBytesResult.Missing();

        using var file = global::Godot.FileAccess.Open(virtualPath, global::Godot.FileAccess.ModeFlags.Read);
        if (file is null)
            return ContentBytesResult.Missing();

        var length = file.GetLength();
        if (length > (ulong)maxBytes)
            return ContentBytesResult.LimitExceeded();
        return ContentBytesResult.Ok(file.GetBuffer((long)length));
    }

    /// <summary>读取包内 PNG，再以读取到的同一份字节执行 Godot 解码。</summary>
    /// <param name="package">图片所属的内容包。</param>
    /// <param name="normalizedRelativePath">已规范化且以小写 .png 结尾的包内相对路径。</param>
    /// <returns>解码图像或失败结果；读取的链接和超限标记保留到结果中，未取得字节时为普通失败。</returns>
    /// <remarks>读取上限为 ContentLimits.PngMaxBytes；后续图片校验与带原始字节的重载一致，未转换的异常向上传播。</remarks>
    /// <exception cref="NullReferenceException"><paramref name="package"/> 为 null。</exception>
    public ImageDecodeResult DecodePng(ContentPackage package, string normalizedRelativePath)
    {
        var read = ReadFile(package, normalizedRelativePath, ContentLimits.PngMaxBytes);
        if (read.UnsafeLink)
            return ImageDecodeResult.Linked();
        if (read.TooLarge)
            return ImageDecodeResult.LimitExceeded();
        if (read.Bytes is null)
            return ImageDecodeResult.Missing();
        return DecodePng(package, normalizedRelativePath, read.Bytes);
    }

    /// <summary>直接解码调用方提供的 PNG 字节，并检查原始大小与图像尺寸上限。</summary>
    /// <param name="package">保留接口签名的包参数；本重载不访问该包。</param>
    /// <param name="normalizedRelativePath">应已规范化的包内路径；本重载仅按 Ordinal 检查小写 .png 后缀。</param>
    /// <param name="rawBytes">要解码的非 null 原始 PNG 字节，不重新读取文件，也不替换这份输入。</param>
    /// <returns>成功时保存宽高、格式和解码像素；后缀不符或无法解码时为普通失败，字节数或宽高超过上限时设置 TooLarge。</returns>
    /// <remarks>
    /// 原始字节上限取 ContentLimits.PngMaxBytes，宽高各自的像素上限取 ContentLimits.ImageMaxDimension，等于上限可以接受。
    /// 不检查路径归属、存在性或链接，不重新验证相对路径结构；读取安全应由调用方先完成。
    /// Missing 与 Invalid 工厂产生相同的普通失败标记。本方法不校验图标方形、面贴图形状或对应用途，最终表现规则由加载器检查。
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="rawBytes"/> 为 null。</exception>
    /// <exception cref="NullReferenceException"><paramref name="normalizedRelativePath"/> 为 null，且原始字节参数非 null。</exception>
    /// <example>真实 Godot 内容检查通过 PNG 读取与加载流程发布 grain；四个表现绑定共用同一解码图片；见 <c>scripts/checks/run-app-check.ps1</c> 的 content 检查。</example>
    public ImageDecodeResult DecodePng(ContentPackage package, string normalizedRelativePath, byte[] rawBytes)
    {
        ArgumentNullException.ThrowIfNull(rawBytes);
        if (!ContentPath.IsPngPath(normalizedRelativePath))
            return ImageDecodeResult.Missing();
        if (rawBytes.Length > ContentLimits.PngMaxBytes)
            return ImageDecodeResult.LimitExceeded();

        var image = new Image();
        if (image.LoadPngFromBuffer(rawBytes) != Error.Ok)
            return ImageDecodeResult.Invalid();
        if (image.GetWidth() > ContentLimits.ImageMaxDimension || image.GetHeight() > ContentLimits.ImageMaxDimension)
            return ImageDecodeResult.LimitExceeded();

        return ImageDecodeResult.Ok(new DecodedImage(
            image.GetWidth(),
            image.GetHeight(),
            NormalizeFormat(image.GetFormat()),
            image.GetData()));
    }

    /// <summary>按运行方式准备或只读扫描玩家蓝图根，把小写 D 格式 UUID 文件名登记为独立内容包。</summary>
    /// <remarks>目录失败向共享诊断列表追加根不可用诊断；非法 UUID 文件名追加警告后跳过，不读取文件正文，也不拒绝全零 UUID。</remarks>
    private IReadOnlyList<ContentPackage> ListBlueprintPackages(string userData, List<ContentDiagnostic> diagnostics)
    {
        var physical = Path.Combine(userData, "blueprints");
        string? error = null;
        if (_createUserDirectories && !ExternalContent.TryEnsureDirectory(userData, physical, out error))
        {
            diagnostics.Add(ExternalContent.BlueprintsRootUnavailable(
                BlueprintsGodotPath,
                error ?? "Blueprints directory is unavailable."));
            return Array.Empty<ContentPackage>();
        }

        if (!ExternalContent.TryListBlueprintFiles(userData, physical, out var files, out var listError))
        {
            diagnostics.Add(ExternalContent.BlueprintsRootUnavailable(
                BlueprintsGodotPath,
                listError ?? "Blueprints directory could not be read."));
            return Array.Empty<ContentPackage>();
        }

        var packages = new List<ContentPackage>();
        foreach (var (fileName, _) in files)
        {
            var idText = fileName[..^".composites.json".Length];
            if (!Guid.TryParseExact(idText, "D", out var guid) ||
                !string.Equals(idText, guid.ToString("D"), StringComparison.Ordinal))
            {
                diagnostics.Add(new ContentDiagnostic(
                    ContentDiagnosticSeverity.Warning,
                    ContentDiagnosticCodes.UuidInvalid,
                    "Player blueprint file name must be a lowercase UUID.",
                    fileName));
                continue;
            }

            packages.Add(new ContentPackage(
                idText,
                physical,
                available: true,
                ContentPackageKind.PlayerBlueprint,
                fileName));
        }

        return packages;
    }

    /// <summary>判断给定包是否为内置包，且本实例构造时选择了物理内置访问。</summary>
    private bool IsPhysicalBuiltIn(ContentPackage package) =>
        package.Kind == ContentPackageKind.BuiltIn && _usesPhysicalBuiltIn;

    /// <summary>枚举一个虚拟目录的直接子项及目录、链接标记，省略点目录。</summary>
    /// <returns>当前枚举得到的条目列表；目录无法解析、为链接或打不开时为 null。</returns>
    private static IReadOnlyList<ContentTreeEntry>? ListEntries(string current)
    {
        if (IsLinkedOrUnresolved(current))
            return null;

        using var dir = DirAccess.Open(current);
        if (dir is null)
            return null;

        var entries = new List<ContentTreeEntry>();
        dir.ListDirBegin();
        for (var name = dir.GetNext(); name.Length > 0; name = dir.GetNext())
        {
            if (name is "." or "..")
                continue;
            entries.Add(new ContentTreeEntry(name, dir.CurrentIsDir(), dir.IsLink(name)));
        }

        dir.ListDirEnd();
        return entries;
    }

    /// <summary>通过父目录检查虚拟路径是否为链接，父目录无法打开或末段为空时也视为不可解析。</summary>
    // Godot 4.7 的 C# 绑定将 is_link(path) 暴露为已打开父目录上的实例方法。
    private static bool IsLinkedOrUnresolved(string virtualPath)
    {
        var parent = virtualPath.GetBaseDir();
        var name = virtualPath.GetFile();
        if (string.IsNullOrEmpty(name))
            return true;
        using var dir = DirAccess.Open(parent);
        return dir is null || dir.IsLink(name);
    }

    /// <summary>检查虚拟路径位于根内，并逐段拒绝链接、空段和无法解析的父目录。</summary>
    /// <remarks>检查不锁定后续文件访问；返回 false 不保证目标文件存在。</remarks>
    private static bool HasUnsafeLink(string root, string virtualPath)
    {
        if (!ContentPath.IsUnderRoot(root, virtualPath) || IsLinkedOrUnresolved(root))
            return true;

        var prefix = root.TrimEnd('/') + "/";
        if (!virtualPath.StartsWith(prefix, StringComparison.Ordinal))
            return true;

        var current = root.TrimEnd('/');
        foreach (var segment in virtualPath[prefix.Length..].Split('/'))
        {
            current += "/" + segment;
            if (segment.Length == 0 || IsLinkedOrUnresolved(current))
                return true;
        }

        return false;
    }

    /// <summary>将 Godot 像素格式的枚举名称转为不受区域设置影响的小写标记。</summary>
    private static string NormalizeFormat(Image.Format format) => format.ToString().ToLowerInvariant();
}
