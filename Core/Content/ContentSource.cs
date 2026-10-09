// 属于内容模块。
// 定义资源访问与图片解码边界，并提供可登记测试输入的内存实现。
namespace Train.Core.Content;

/// <summary>表示从内容来源读取原始字节的结果。</summary>
/// <remarks>读取失败通过标记和 null 字节表示，不在本类型中创建诊断。Ok 直接保留数组引用，不校验 null 或长度；调用方须保持成功结果的字节稳定。</remarks>
/// <example>第一次 PNG 读取返回可解码字节，后续读取返回无效字节，加载器使用第一次读取的数据计算资产修订；见 <c>ContentReloaderTests.Asset_hash_uses_the_same_bytes_that_passed_png_validation</c>。</example>
public sealed class ContentBytesResult
{
    /// <summary>取得文件是否超过调用方给出的字节上限。</summary>
    public bool TooLarge { get; }
    /// <summary>取得读取是否因链接或重解析点而被拒绝。</summary>
    public bool UnsafeLink { get; }
    /// <summary>取得读取的原始字节数组引用；Missing、LimitExceeded 和 Linked 结果均为 null，成功文件可以为空数组。</summary>
    public byte[]? Bytes { get; }

    /// <summary>原样保存读取标记和字节引用，不复制或执行文件检查。</summary>
    private ContentBytesResult(bool tooLarge, bool unsafeLink, byte[]? bytes)
    {
        TooLarge = tooLarge;
        UnsafeLink = unsafeLink;
        Bytes = bytes;
    }

    /// <summary>将读取的字节包装为无超限或链接标记的结果。</summary>
    /// <param name="bytes">应为非 null 的原始字节数组，允许为空；直接保留引用。</param>
    /// <returns>TooLarge 和 UnsafeLink 均为 false、Bytes 为输入数组的结果。</returns>
    /// <remarks>不校验 null、大小或文件内容；状态由读取方判断。</remarks>
    public static ContentBytesResult Ok(byte[] bytes) => new(false, false, bytes);
    /// <summary>创建未取得文件字节的结果。</summary>
    /// <returns>Bytes 为 null，超限和链接标记均为 false 的结果。</returns>
    /// <remarks>可用于文件缺失、无法读取或文件类型不受支持；这些原因不在结果中进一步区分。</remarks>
    public static ContentBytesResult Missing() => new(false, false, null);
    /// <summary>创建文件字节数超过读取上限的结果。</summary>
    /// <returns>TooLarge 为 true、UnsafeLink 为 false 且 Bytes 为 null 的结果。</returns>
    public static ContentBytesResult LimitExceeded() => new(true, false, null);
    /// <summary>创建因链接或重解析点而拒绝读取的结果。</summary>
    /// <returns>UnsafeLink 为 true、TooLarge 为 false 且 Bytes 为 null 的结果。</returns>
    public static ContentBytesResult Linked() => new(false, true, null);
}

/// <summary>表示 PNG 解码或验证的结果。</summary>
/// <remarks>Missing 和 Invalid 的属性值相同，均为普通失败；LimitExceeded 和 Linked 还标记各自原因。Ok 直接保留图像引用，不校验 null、尺寸、格式或像素内容。</remarks>
/// <example>登记缺失或 PNG 字节超限的图像会使内容加载失败；见 <c>ContentReloaderTests.Image_shape_and_size_rules_reject_incompatible_visuals</c>。</example>
public sealed class ImageDecodeResult
{
    /// <summary>取得成功结果保存的图像引用；失败工厂创建的结果均为 null。</summary>
    public DecodedImage? Image { get; }
    /// <summary>取得原始文件或图像尺寸是否超限。</summary>
    public bool TooLarge { get; }
    /// <summary>取得结果是否标记为失败；普通失败、超限和链接拒绝时均为 true。</summary>
    public bool Failed { get; }
    /// <summary>取得读取是否因链接或重解析点而被拒绝。</summary>
    public bool UnsafeLink { get; }

    /// <summary>原样保存图像引用和解码标记，不执行解码或图像校验。</summary>
    private ImageDecodeResult(DecodedImage? image, bool tooLarge, bool failed, bool unsafeLink)
    {
        Image = image;
        TooLarge = tooLarge;
        Failed = failed;
        UnsafeLink = unsafeLink;
    }

    /// <summary>将解码图像包装为没有失败标记的结果。</summary>
    /// <param name="image">应为非 null 的解码图像；直接保留引用。</param>
    /// <returns>Image 为输入图像，TooLarge、Failed 和 UnsafeLink 均为 false 的结果。</returns>
    /// <remarks>不在此处检查 null 或图像是否满足内容表现规则。</remarks>
    public static ImageDecodeResult Ok(DecodedImage image) => new(image, false, false, false);
    /// <summary>创建未取得图像的失败结果。</summary>
    /// <returns>Image 为 null、Failed 为 true，超限和链接标记均为 false 的结果。</returns>
    public static ImageDecodeResult Missing() => new(null, false, true, false);
    /// <summary>创建原始字节或解码尺寸超过上限的失败结果。</summary>
    /// <returns>Image 为 null、TooLarge 和 Failed 为 true、UnsafeLink 为 false 的结果。</returns>
    public static ImageDecodeResult LimitExceeded() => new(null, true, true, false);
    /// <summary>创建字节无法解码或验证不通过的普通失败结果。</summary>
    /// <returns>Image 为 null、Failed 为 true，超限和链接标记均为 false 的结果；与 Missing 的属性值相同。</returns>
    public static ImageDecodeResult Invalid() => new(null, false, true, false);
    /// <summary>创建因链接或重解析点而拒绝解码的失败结果。</summary>
    /// <returns>Image 为 null、Failed 和 UnsafeLink 为 true、TooLarge 为 false 的结果。</returns>
    public static ImageDecodeResult Linked() => new(null, false, true, true);
}

/// <summary>定义内容加载器所需的包发现、文件读取和 PNG 解码边界。</summary>
/// <remarks>调用方使用规范化的包内相对路径；实际文件系统实现负责资源根边界和链接检查。预期读取失败通过结果报告，实现异常由内容加载器向上传播。带原始字节的解码重载用于保证解码与资产修订使用同一份输入。</remarks>
/// <example>加载器读取 iron.png 后，将读取结果中的字节直接交给解码重载；见 <c>ContentReloaderTests.Asset_hash_uses_the_same_bytes_that_passed_png_validation</c>。</example>
public interface IContentSource
{
    /// <summary>列出当前可发现的内容包。</summary>
    /// <returns>非 null 的包集合，顺序供候选构建器使用；无包时为空集合，也可包含标记为不可用的包。</returns>
    /// <remarks>实现可以在调用时更新包发现状态和扫描诊断；返回集合不统一保证复制或冻结。</remarks>
    IReadOnlyList<ContentPackage> ListPackages();
    /// <summary>取得最近一次包发现阶段产生的诊断。</summary>
    /// <returns>非 null 的诊断集合；无问题时为空，诊断级别本身不直接决定整个候选是否允许发布。</returns>
    IReadOnlyList<ContentDiagnostic> ListScanDiagnostics();
    /// <summary>发现一个包内的全部 JSON 文件，不解释定义类型。</summary>
    /// <param name="package">待发现文件的内容包。</param>
    /// <returns>以小写 .json 结尾的规范包内相对路径、发现诊断和包无效标记；无文件时返回空列表。</returns>
    /// <remarks>
    /// 结果是一次发现的列表快照；无效发现仍可包含部分文件，加载器按包处理失败。
    /// 格式后缀、schema 和玩家蓝图的文件身份规则由加载器解释；内容源只负责资源根边界和安全访问。
    /// </remarks>
    ContentFileDiscovery ListDefinitionFiles(ContentPackage package);
    /// <summary>在调用方给出的字节上限内读取包内文件。</summary>
    /// <param name="package">文件所属内容包。</param>
    /// <param name="normalizedRelativePath">已通过 ContentPath.TryNormalize 的包内相对路径。</param>
    /// <param name="maxBytes">非负读取上限，单位为字节；等于上限的文件可以接受，零仅允许空文件。</param>
    /// <returns>非 null 的字节结果；成功时提供原始数组，缺失、超限或链接拒绝时不提供字节。</returns>
    /// <remarks>消费方须保持返回数组稳定，以便后续解码和修订计算使用一致输入。</remarks>
    /// <example>超过 1 MiB 的定义 JSON 会通过超限结果被拒绝；见 <c>ContentReloaderTests.File_and_global_limits_reject_oversized_candidates</c>。</example>
    ContentBytesResult ReadFile(ContentPackage package, string normalizedRelativePath, int maxBytes);
    /// <summary>读取并解码或验证一个 PNG 文件。</summary>
    /// <param name="package">图像所属内容包。</param>
    /// <param name="normalizedRelativePath">已规范化且以小写 .png 结尾的包内相对路径。</param>
    /// <returns>非 null 的图像结果，报告图像、普通失败、超限或链接拒绝。</returns>
    /// <remarks>已有读取字节时应使用带 rawBytes 的重载；最终图标及面贴图形状规则由内容加载器检查。</remarks>
    ImageDecodeResult DecodePng(ContentPackage package, string normalizedRelativePath);
    /// <summary>解码或验证此前从同一路径读取的 PNG 字节。</summary>
    /// <param name="package">图像所属内容包。</param>
    /// <param name="normalizedRelativePath">已规范化且以小写 .png 结尾的包内相对路径。</param>
    /// <param name="rawBytes">要参与解码的非 null 原始字节；消费方随后使用同一份输入计算资产修订。</param>
    /// <returns>非 null 的图像结果；实现使用提供的字节，不以重新读取的其他字节替代输入。</returns>
    /// <example>两份 PNG 原始字节可以产生相同像素，但保留不同资产修订；见 <c>ContentReloaderTests.Same_pixels_different_raw_png_bytes_change_only_the_asset_revision</c>。</example>
    ImageDecodeResult DecodePng(ContentPackage package, string normalizedRelativePath, byte[] rawBytes);
}

/// <summary>保存小型、确定性内容输入的内存内容来源。</summary>
/// <remarks>
/// 通过登记字节和解码结果模拟内容源，不访问文件系统或真正解析 PNG。显式提供的字节数组与图像对象保留引用，调用方须保持它们稳定；包和诊断列表查询返回数组快照，元素不复制。
/// 加入资源时按已规范化命名空间及可选资源根精确定位首个包；未找到时自动创建 BuiltIn 包。创建后仅按原输入命名空间取首项，不再按资源根筛选。各登记操作不保证失败时撤销已做修改，也不提供并发同步。
/// </remarks>
/// <example>登记 8×4 图标时不立即拒绝，内容加载器随后按正方形规则拒绝它；见 <c>ContentReloaderTests.Image_shape_and_size_rules_reject_incompatible_visuals</c>。</example>
public sealed class MemoryContentSource : IContentSource
{
    private readonly List<ContentPackage> _packages = new();
    private readonly List<ContentDiagnostic> _scanDiagnostics = new();
    private readonly Dictionary<string, List<string>> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageDecodeResult> _images = new(StringComparer.Ordinal);

    /// <summary>按包种类替换匹配的包，并将新包加入列表末尾。</summary>
    /// <param name="namespace">按 ContentPackage 规则规范化的包命名空间。</param>
    /// <param name="resourcesRoot">非空资源根键，原样保存，默认 memory://resources。</param>
    /// <param name="available">提供给加载器的包可用标记，不影响资源登记。</param>
    /// <param name="kind">包种类，默认 BuiltIn；决定包替换匹配规则。</param>
    /// <param name="source">声明来源；没有声明文件时可为 null。</param>
    /// <param name="mod">Mod 包必需且身份须与命名空间一致的元数据；其他种类必须为 null。</param>
    /// <remarks>BuiltIn 替换同命名空间的全部内置包；其他种类替换同资源根的全部包，不区分原种类或命名空间。既有文件、字节和图像登记不清除，同资源键的数据继续保留。</remarks>
    /// <exception cref="ArgumentNullException">namespace 或 resourcesRoot 为 null，或 Mod 包缺少 mod。</exception>
    /// <exception cref="ArgumentException">包身份、资源根或 Mod 元数据组合不符合 ContentPackage 规则。</exception>
    /// <example>同一 UUID、不同资源根的两个 Mod 可以同时登记，随后由加载器一起隔离；见 <c>ModLoadingTests.Duplicate_mod_uuids_are_all_disabled_without_choosing_a_winner</c>。</example>
    public void AddPackage(
        string @namespace,
        string resourcesRoot = "memory://resources",
        bool available = true,
        ContentPackageKind kind = ContentPackageKind.BuiltIn,
        string? source = null,
        ModPackageMetadata? mod = null)
    {
        var package = new ContentPackage(@namespace, resourcesRoot, available, kind, source, mod);
        if (kind == ContentPackageKind.BuiltIn)
            _packages.RemoveAll(item => item.Kind == ContentPackageKind.BuiltIn && item.Namespace == package.Namespace);
        else
            _packages.RemoveAll(item => item.ResourcesRoot == package.ResourcesRoot);
        _packages.Add(package);
        _files.TryAdd(PackageKey(package), new List<string>());
    }

    /// <summary>以指定资源目录键和清单元数据登记内存 Mod 包。</summary>
    /// <param name="folderName">用于拼接 memory://mods 下资源根及清单来源的目录文本；本方法不验证物理目录。</param>
    /// <param name="uuid">Mod 包命名空间，应与清单 Id 精确一致。</param>
    /// <param name="available">提供给加载器的包可用标记。</param>
    /// <param name="manifest">已校验清单；为 null 时生成 Test Mod、版本 1.0.0、作者 Test 的默认清单并解析校验。</param>
    /// <remarks>传入清单不重新解析；包身份一致性由 AddPackage 检查。此方法仅登记包，定义和图片需另行加入。</remarks>
    /// <exception cref="ArgumentNullException">提供 manifest 时 uuid 为 null。</exception>
    /// <exception cref="ArgumentException">默认清单解析失败，命名空间无效或清单 Id 与包身份不一致。</exception>
    /// <example>Mod 本地标识会在加载后补全为其 UUID 命名空间；见 <c>ModLoadingTests.Local_ids_expand_to_the_mod_uuid_namespace</c>。</example>
    public void AddMod(string folderName, string uuid, bool available = true, ModManifest? manifest = null)
    {
        if (manifest is null)
        {
            if (!ModManifest.TryParse(
                    System.Text.Encoding.UTF8.GetBytes(
                        $"{{\"schema_version\":1,\"id\":\"{uuid}\",\"name\":\"Test Mod\",\"version\":\"1.0.0\",\"authors\":[\"Test\"]}}"),
                    folderName + "/" + ModDiscovery.ManifestFileName,
                    out var parsed,
                    out _) ||
                parsed is null)
            {
                throw new ArgumentException("A valid mod manifest is required.", nameof(uuid));
            }

            manifest = parsed;
        }

        AddPackage(
            uuid,
            "memory://mods/" + folderName + "/resources",
            available,
            ContentPackageKind.Mod,
            folderName + "/" + ModDiscovery.ManifestFileName,
            new ModPackageMetadata(manifest ?? throw new ArgumentException("A valid mod manifest is required.", nameof(manifest))));
    }

    /// <summary>将一条包扫描诊断追加到登记列表末尾。</summary>
    /// <param name="diagnostic">非 null 的诊断对象，保留引用，不在此处改变包可用性。</param>
    /// <exception cref="ArgumentNullException"><paramref name="diagnostic"/> 为 <see langword="null"/>。</exception>
    /// <example>登记 Mod 根目录失败诊断后，内置内容仍可成功发布；见 <c>ModLoadingTests.Mods_root_failure_does_not_fail_built_in_content</c>。</example>
    public void AddScanDiagnostic(ContentDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        _scanDiagnostics.Add(diagnostic);
    }

    /// <summary>将文本编码为 UTF-8 字节，并登记一个 JSON 文件。</summary>
    /// <param name="namespace">已规范化的包命名空间，用于精确定位包。</param>
    /// <param name="relativePath">须通过 ContentPath.TryNormalize 且以小写 .json 结尾的相对路径。</param>
    /// <param name="utf8Text">非 null 的文件文本，允许为空；登记时不解析 JSON。</param>
    /// <param name="resourcesRoot">用于区分同命名空间包的资源根；null 时取首个匹配包。</param>
    /// <remarks>效果：同一路径覆盖字节，首次登记时加入文件列表；内容格式由加载器识别。</remarks>
    /// <exception cref="ArgumentNullException">utf8Text 为 null，或自动建包时 namespace 为 null。</exception>
    /// <exception cref="ArgumentException">路径、自动建包的命名空间或资源根无效。</exception>
    /// <exception cref="InvalidOperationException">自动建包后无法按原输入命名空间精确定位。</exception>
    public void AddDefinitionFile(string @namespace, string relativePath, string utf8Text, string? resourcesRoot = null) =>
        AddDefinitionFile(@namespace, relativePath, System.Text.Encoding.UTF8.GetBytes(utf8Text), resourcesRoot);

    /// <summary>登记或覆盖一个安全路径下的 JSON 文件，保存其原始字节。</summary>
    /// <param name="namespace">已规范化的包命名空间，用于精确定位包。</param>
    /// <param name="relativePath">须通过相对路径检查且以小写 .json 结尾的路径。</param>
    /// <param name="bytes">非 null 的文件字节，允许为空数组；保留引用，调用方在一次加载中须保持其稳定。</param>
    /// <param name="resourcesRoot">精确匹配的资源根；null 时取首个同命名空间包。</param>
    /// <remarks>效果：同一路径只列出一次，再次登记覆盖字节。未找到包时沿用 AddPackage 的内置包默认值。</remarks>
    /// <exception cref="ArgumentNullException">bytes 为 null，或自动建包时 namespace 为 null。</exception>
    /// <exception cref="ArgumentException">路径、自动建包的命名空间或资源根无效。</exception>
    /// <exception cref="InvalidOperationException">自动建包后无法按原输入命名空间精确定位。</exception>
    public void AddDefinitionFile(string @namespace, string relativePath, byte[] bytes, string? resourcesRoot = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (!ContentPath.TryNormalize(relativePath, out var normalized) || !normalized.EndsWith(".json", StringComparison.Ordinal))
            throw new ArgumentException("Definition files must use a safe relative JSON path.", nameof(relativePath));
        var package = EnsurePackage(@namespace, resourcesRoot);
        var files = _files[PackageKey(package)];
        if (!files.Contains(normalized, StringComparer.Ordinal))
            files.Add(normalized);
        _bytes[Key(package, normalized)] = bytes;
    }
    /// <summary>登记图像对象和对应测试字节，供内存读取及解码结果查询使用。</summary>
    /// <param name="namespace">已规范化的包命名空间，用于精确定位包。</param>
    /// <param name="relativePath">须通过相对路径检查且以小写 .png 结尾的路径。</param>
    /// <param name="image">应为非 null 的解码图像，保留对象引用；不检查尺寸、格式或像素数量。</param>
    /// <param name="rawBytes">对应字节数组，直接保留引用；null 时复制当前像素作为测试字节，不要求这些字节是 PNG 编码。</param>
    /// <param name="resourcesRoot">精确匹配的资源根；null 时取首个同命名空间包。</param>
    /// <remarks>同路径覆盖解码结果和字节。省略 rawBytes 时复制图像像素作为测试字节；图像本身拥有不可修改的像素副本。</remarks>
    /// <exception cref="ArgumentNullException">自动建包时 namespace 为 null。</exception>
    /// <exception cref="ArgumentException">路径、自动建包的命名空间或资源根无效。</exception>
    /// <exception cref="InvalidOperationException">自动建包后无法按原输入命名空间精确定位。</exception>
    /// <exception cref="NullReferenceException">rawBytes 未提供且 image 为 null。</exception>
    /// <example>登记相同像素、不同原始字节的 iron.png，资产修订随原始字节变化；见 <c>ContentReloaderTests.Same_pixels_different_raw_png_bytes_change_only_the_asset_revision</c>。</example>
    public void AddImage(string @namespace, string relativePath, DecodedImage image, byte[]? rawBytes = null, string? resourcesRoot = null)
    {
        if (!ContentPath.TryNormalize(relativePath, out var normalized) || !ContentPath.IsPngPath(normalized))
            throw new ArgumentException("Images must use a safe relative PNG path.", nameof(relativePath));
        var package = EnsurePackage(@namespace, resourcesRoot);
        _images[Key(package, normalized)] = ImageDecodeResult.Ok(image);
        _bytes[Key(package, normalized)] = rawBytes ?? (image.Pixels as byte[] ?? image.Pixels.ToArray());
    }

    /// <summary>将指定路径的解码结果登记为图像缺失。</summary>
    /// <param name="namespace">已规范化的包命名空间，用于精确定位包。</param>
    /// <param name="relativePath">须通过相对路径检查的路径；本方法不要求 .png 后缀。</param>
    /// <param name="resourcesRoot">精确匹配的资源根；null 时取首个同命名空间包。</param>
    /// <remarks>仅覆盖解码结果，不写入或删除既有原始字节；此前登记的文件字节仍可被 ReadFile 返回。</remarks>
    /// <exception cref="ArgumentNullException">自动建包时 namespace 为 null。</exception>
    /// <exception cref="ArgumentException">路径、自动建包的命名空间或资源根无效。</exception>
    /// <exception cref="InvalidOperationException">自动建包后无法按原输入命名空间精确定位。</exception>
    /// <example>新路径 iron.png 登记为缺失后，对应体素加载失败；见 <c>ContentReloaderTests.Image_shape_and_size_rules_reject_incompatible_visuals</c>。</example>
    public void AddMissingImage(string @namespace, string relativePath, string? resourcesRoot = null)
    {
        if (!ContentPath.TryNormalize(relativePath, out var normalized))
            throw new ArgumentException("Images must use a safe relative path.", nameof(relativePath));
        var package = EnsurePackage(@namespace, resourcesRoot);
        _images[Key(package, normalized)] = ImageDecodeResult.Missing();
    }

    /// <summary>登记 PNG 超限的解码结果和超过 PNG 字节上限的测试数组。</summary>
    /// <param name="namespace">已规范化的包命名空间，用于精确定位包。</param>
    /// <param name="relativePath">须通过相对路径检查的路径；本方法不要求 .png 后缀。</param>
    /// <param name="resourcesRoot">精确匹配的资源根；null 时取首个同命名空间包。</param>
    /// <remarks>字节数组长度固定为 PngMaxBytes 加一；实际读取是否超限仍取决于允许后缀和调用方提供的 maxBytes。</remarks>
    /// <exception cref="ArgumentNullException">自动建包时 namespace 为 null。</exception>
    /// <exception cref="ArgumentException">路径、自动建包的命名空间或资源根无效。</exception>
    /// <exception cref="InvalidOperationException">自动建包后无法按原输入命名空间精确定位。</exception>
    /// <example>iron.png 的测试字节超过 4 MiB，加载器拒绝对应内容；见 <c>ContentReloaderTests.Image_shape_and_size_rules_reject_incompatible_visuals</c>。</example>
    public void AddOversizedImageBytes(string @namespace, string relativePath, string? resourcesRoot = null)
    {
        if (!ContentPath.TryNormalize(relativePath, out var normalized))
            throw new ArgumentException("Images must use a safe relative path.", nameof(relativePath));
        var package = EnsurePackage(@namespace, resourcesRoot);
        _images[Key(package, normalized)] = ImageDecodeResult.LimitExceeded();
        _bytes[Key(package, normalized)] = new byte[ContentLimits.PngMaxBytes + 1];
    }

    /// <summary>按当前登记顺序返回内容包列表的数组快照。</summary>
    /// <returns>列表结构的副本，包对象保留引用；包括不可用包，无包时为空数组。</returns>
    public IReadOnlyList<ContentPackage> ListPackages() => _packages.ToArray();

    /// <summary>按追加顺序返回登记的包扫描诊断。</summary>
    /// <returns>列表结构的数组副本，诊断对象保留引用；不会清空登记列表。</returns>
    public IReadOnlyList<ContentDiagnostic> ListScanDiagnostics() => _scanDiagnostics.ToArray();

    /// <summary>返回指定资源键下登记的全部 JSON 文件。</summary>
    /// <param name="package">以资源根和命名空间定位文件列表的包。</param>
    /// <returns>小写 .json 后缀文件的数组快照，无诊断且 Invalid 为 false；没有登记列表时为空。</returns>
    /// <remarks>保持登记顺序；包种类、文件格式和玩家蓝图身份规则由加载器检查。</remarks>
    /// <exception cref="NullReferenceException">package 为 null。</exception>
    public ContentFileDiscovery ListDefinitionFiles(ContentPackage package)
    {
        var files = _files.TryGetValue(PackageKey(package), out var listed)
            ? listed.Where(file => file.EndsWith(".json", StringComparison.Ordinal)).ToArray()
            : Array.Empty<string>();
        return ContentFileDiscovery.Of(files);
    }

    /// <summary>以指定身份登记玩家蓝图包及同名蓝图文件。</summary>
    /// <param name="uuid">作为包命名空间和文件名的已规范化身份文本；调用方应按玩家蓝图约定提供 UUID。</param>
    /// <param name="utf8Text">非 null 的蓝图 JSON 文本；登记时不解析内容。</param>
    /// <remarks>仅按包身份和相对路径规则检查，不验证 UUID 格式。先登记 memory://blueprints 下的包，再编码并登记身份加 .composites.json 的文件。</remarks>
    /// <exception cref="ArgumentNullException">uuid 或 utf8Text 为 null。</exception>
    /// <exception cref="ArgumentException">包命名空间或生成的相对路径无效。</exception>
    /// <exception cref="InvalidOperationException">登记包后无法按原输入身份精确定位，例如身份包含首尾空白。</exception>
    /// <example>登记 cccccccc-dddd-4eee-8fff-000000000001 的玩家蓝图，并保留文件内显示名“我的手写蓝图”；见 <c>CompositeContentTests.Player_blueprint_uses_file_uuid_namespace_and_display_name</c>。</example>
    public void AddBlueprint(string uuid, string utf8Text)
    {
        var file = uuid + ".composites.json";
        AddPackage(uuid, "memory://blueprints/" + uuid, kind: ContentPackageKind.PlayerBlueprint, source: file);
        AddDefinitionFile(uuid, file, utf8Text, "memory://blueprints/" + uuid);
    }

    /// <summary>按资源键返回已登记的 JSON 或 PNG 字节，并比较调用方提供的大小上限。</summary>
    /// <param name="package">以资源根和命名空间定位字节的包；不检查当前登记状态或 Available。</param>
    /// <param name="normalizedRelativePath">调用方已规范化的相对路径；本方法仅检查小写 .json 或 .png 后缀。</param>
    /// <param name="maxBytes">读取上限，单位为字节；等于上限可接受，负值使任何已登记字节数组超限。</param>
    /// <returns>无允许后缀或没有登记字节时为 Missing，超过上限时为 LimitExceeded，否则返回原数组引用；不产生 Linked。</returns>
    /// <remarks>不再次规范化路径、不复制字节或验证文件内容。</remarks>
    /// <exception cref="NullReferenceException">路径为 null、定位资源时 package 为 null，或已登记的字节值为 null。</exception>
    /// <example>plugin.dll、script.cs、hook.gd 和 pack.pck 都不返回字节；见 <c>ModLoadingTests.Data_only_source_refuses_to_read_non_json_or_png_bytes</c>。</example>
    public ContentBytesResult ReadFile(ContentPackage package, string normalizedRelativePath, int maxBytes)
    {
        if (!ContentPath.IsAllowedDataFile(normalizedRelativePath))
            return ContentBytesResult.Missing();
        if (!_bytes.TryGetValue(Key(package, normalizedRelativePath), out var bytes))
            return ContentBytesResult.Missing();
        if (bytes.Length > maxBytes)
            return ContentBytesResult.LimitExceeded();
        return ContentBytesResult.Ok(bytes);
    }

    /// <summary>以 PNG 字节上限读取登记数据，再查询同一字节对应的预设解码结果。</summary>
    /// <param name="package">图像所属包，用资源根和命名空间定位。</param>
    /// <param name="normalizedRelativePath">已规范化且以小写 .png 结尾的相对路径。</param>
    /// <returns>读取失败时返回相应失败标记，否则将取得的字节交给带 rawBytes 的重载。</returns>
    /// <exception cref="NullReferenceException">路径为 null、定位资源时 package 为 null，或已登记的字节值为 null。</exception>
    /// <example>非图像路径 plugin.dll 返回失败结果；见 <c>ModLoadingTests.Data_only_source_refuses_to_read_non_json_or_png_bytes</c>。</example>
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

    /// <summary>检查 PNG 后缀、字节大小及与登记字节的一致性，然后返回预设解码结果。</summary>
    /// <param name="package">以资源根和命名空间定位字节与图像结果的包。</param>
    /// <param name="normalizedRelativePath">已规范化的相对路径；本方法仅比较小写 .png 后缀。</param>
    /// <param name="rawBytes">非 null 的待验证字节数组；本方法只读取，不登记或复制。</param>
    /// <returns>后缀不匹配或无图像结果时为 Missing，超限时为 LimitExceeded，与已登记字节不同时为 Invalid，否则原样返回登记的图像结果。</returns>
    /// <remarks>逐字节比较内容而非数组身份；没有登记字节时跳过一致性检查。不解析 PNG，也不复核图像宽高、格式或像素字节数，内容表现规则由加载器检查。</remarks>
    /// <exception cref="ArgumentNullException">rawBytes 为 null。</exception>
    /// <exception cref="NullReferenceException">路径为 null，或定位资源时 package 为 null。</exception>
    /// <example>8×4 图像仍可作为预设解码结果返回，但加载器按图标规则拒绝；见 <c>ContentReloaderTests.Image_shape_and_size_rules_reject_incompatible_visuals</c>。</example>
    public ImageDecodeResult DecodePng(ContentPackage package, string normalizedRelativePath, byte[] rawBytes)
    {
        ArgumentNullException.ThrowIfNull(rawBytes);
        if (!ContentPath.IsPngPath(normalizedRelativePath))
            return ImageDecodeResult.Missing();
        if (rawBytes.Length > ContentLimits.PngMaxBytes)
            return ImageDecodeResult.LimitExceeded();
        if (_bytes.TryGetValue(Key(package, normalizedRelativePath), out var stored) &&
            !stored.AsSpan().SequenceEqual(rawBytes))
            return ImageDecodeResult.Invalid();
        return _images.TryGetValue(Key(package, normalizedRelativePath), out var image)
            ? image
            : ImageDecodeResult.Missing();
    }

    /// <summary>取首个精确匹配的包；未找到时创建内置包，再仅按原输入命名空间取首项。</summary>
    /// <param name="namespace">已规范化的命名空间，匹配前不去除空白或改变大小写。</param>
    /// <param name="resourcesRoot">首次查找需要匹配的资源根；null 时不限根，创建时使用 memory://resources。</param>
    /// <returns>首次匹配的包或创建后首个命名空间匹配的包；创建后的查找不再次约束资源根。</returns>
    /// <exception cref="InvalidOperationException">创建时规范化了命名空间，但原输入未规范化，导致后续精确查找无结果。</exception>
    private ContentPackage EnsurePackage(string @namespace, string? resourcesRoot)
    {
        var matches = _packages.Where(item => item.Namespace == @namespace);
        if (resourcesRoot is not null)
            matches = matches.Where(item => item.ResourcesRoot == resourcesRoot);
        var package = matches.FirstOrDefault();
        if (package is not null)
            return package;
        AddPackage(@namespace, resourcesRoot ?? "memory://resources");
        return _packages.First(item => item.Namespace == @namespace);
    }

    /// <summary>用资源根和命名空间组成登记键，使同一命名空间下不同资源根的数据分开存储。</summary>
    private static string PackageKey(ContentPackage package) => package.ResourcesRoot + "\n" + package.Namespace;

    /// <summary>将包登记键和相对路径组成文件或图像的登记键。</summary>
    private static string Key(ContentPackage package, string relativePath) =>
        PackageKey(package) + "\n" + relativePath;
}
