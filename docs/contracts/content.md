# 内容层公开契约

本页落实[内容层公开契约：加载、投影、设计计算与发布](https://github.com/odradekk/train/issues/288)，与[公共约定](conventions.md)、[跨层事务](transactions.md)及[编辑协议](editing.md)一起使用。内容负责解析、依赖、合法性、派生计算与编码；玩法规则仍以设计文档为准。

## 状态与入口

[Train.Content.Contracts](../../Contracts/Content/Train.Content.Contracts.csproj) 只引用核心契约，并提供[可编译消费样例](../../prototypes/content-contract/ConsumerExamples.cs)。`Content/Train.Content.csproj` 已实现真实工厂的 `Discover` 与 `PrepareLoad(Initial/Restore/Reload)`：manifest v2、依赖、voxel v3、固定资产字节、资产规则、只读投影及候选生命周期；包内 blueprint v3 支持车辆、建筑、组装体、功能复合体与场景模型，包含最多四层的设计复制和功能复合体引用。Initial 还支持地貌 v2、聚居地类型 v3、聚居地模板 v2、五类规则与生成配置 v2，包含 NPC 初始化参数；铁路表现参数通过 `FindRailwayAppearance` 提供；`RailwayAppearanceProjection` 的必填 `BallastVoxel` 对应 `appearance.ballast_voxel`，必须引用不含 `simulation` 的场景体素，供线路和节点道床使用，没有地貌岩层回退。该投影不含模拟字段，表现字段不进入核心铁路规则或业务语义修订。`CreateWorldInput` 交出已解析的新局输入，`WithSettlementCopies` 按最终所有者固定模板副本，`FindSettlementLayout` 读取所有者的布局。模板的 `founding`、`inventory`、`production_plans` 支持非空数组，内容层解析、校验引用及地块和能力约束并交出核心投影；特殊库存支持 voxel、功能复合体标识及组装体构成哈希货物。NPC 补货、产出预存和现车生成由核心执行，不属于本次内容交付。初始化后的经营状态由核心持有，不写入内容布局副本。Restore 从内嵌设计与实例模板副本重算当前投影，Reload 保留清单仍引用的原设计与副本；源蓝图、源模板的删改不改写已有实例布局。

`EvaluateDesign` 在真实工厂产生的固定视图上计算车辆、建筑、组装体、场景模型及生物的身份、合法性和嵌套投影；包加载还为功能复合体传入定义身份。车辆、建筑及两类大型设备提供 `DesignInput`。流程 v1 已支持生产、开采和回收的解析、引用校验、核心投影与显示查询；租赁参数沿用市场规则。流程业务参数参与语义修订和核心输入身份，显示变化只标记 `Presentation`；货物和地貌数组按引用身份的 Ordinal 顺序规范化。`Capture/DecodeCapture` 与 `CaptureReplay/DecodeReplay` 已实现当前范围的普通内容往返和仅凭载荷重建历史视图。恢复报告被引用流程的缺失；重载拒绝原有效引用新增缺失及流程种类变化，允许原缺失保留或恢复。恢复／重载对已保存设计和模板副本中的缺失定义提供部分计算、占位和锁资料；损伤、进度和批次冻结参数仍由核心保存。生物蓝图沿场景模型求值，完整结果的核心设计为空；生物聚落类型参与核心投影和语义身份。新设计草稿支持缺定义和缺必填规格的部分计算；设计草稿编解码、本局草稿保存和删除、本局库及用户库编辑、用户库加载、设计比较、建造目标和实例目标准备已有实现。定义草稿与包定义目标编解码、固定创作包读取及逐文件补丁编码已有实现；生物聚落类型支持读取、写入、删除和草稿编解码，未完成字段在具体路径返回 `invalid_field`。生物类别蓝图已支持普通加载、求值与创作往返。本局草稿保存遇到包定义目标或定义载荷时继续返回 `invalid_field`。上述模块实现不表示完整存档容器、核心经济运行或真实四模块联调已经通过；阶段一真实交接归 #298，经济运输交接归 #299；生产宿主已通过 `App/Composition/` 接入真实工厂。共享用例与替身的行为验证由[契约原型](https://github.com/odradekk/train/issues/292)完成。

| 声明 | 作用 |
| --- | --- |
| [Values.cs](../../Contracts/Content/Values.cs) | 内容身份、包、来源、诊断及查询结果 |
| [Sources.cs](../../Contracts/Content/Sources.cs) | 文件读取和资产检查宿主接口、原始资产字节 |
| [Designs.cs](../../Contracts/Content/Designs.cs) | 不可变设计草稿、完整与部分计算、编号和位置对应 |
| [Appearance.cs](../../Contracts/Content/Appearance.cs) | 体素、设计、场景模型、生物和聚居地布局 |
| [WorldContent.cs](../../Contracts/Content/WorldContent.cs) | 地貌、生物聚落及铁路表现资料 |
| [Authoring.cs](../../Contracts/Content/Authoring.cs) | 已保存草稿、开发表单输入及包文件变更 |
| [Lifecycle.cs](../../Contracts/Content/Lifecycle.cs) | 工厂、准备、固定读取、保存和复现捕获 |

实现程序集唯一公开类型为 `Train.Content.ContentFactory`，其 `Create()` 返回 `IContentFactory`；候选、视图、解析器和辅助类型均为 `internal`。宿主已在 `App/Composition/` 接入，工厂调用位置由结构检查验证。旧公开加载路径已在 [#452](https://github.com/odradekk/train/issues/452) 退役；[ContentFactoryTests.cs](../../Tests/Content/Loading/ContentFactoryTests.cs) 的 `Only_the_factory_is_exported_from_the_content_implementation` 用例通过反射断言检查实现程序集的公开面。文件端口和资产检查由宿主注入，内容不访问静态全局目录、不启动线程、不依赖 Godot，也不允许调用方替换解析器、哈希器或业务计算阶段。

## 身份、版本与数据所有权

- 核心拥有的定义身份、设计哈希、构成哈希、部件编号、设计属性与规则投影直接引用核心声明。内容计算这些值，但不重新声明同形类型；核心不因此读取布局、显示文字或资产。
- 包身份为 `core` 或规范 Mod UUID。包内蓝图使用 namespaced identity，玩家蓝图使用系统分配的 UUID；`BlueprintAddress` 同时指定包库、用户库或本局库。跨库复制用 `BlueprintSaveTarget.New`，取得新 UUID；覆盖只允许用户库或本局库已有条目，包蓝图只读。
- `ContentGeneration` 从 1 开始，是当前有效内容的发布序号；无变化和失败不递增。`PersistentRevision` 跟踪本局可保存内容，保存本局草稿也会改变它；用户库文件变化不凭空制造本局修改。
- 语义修订、资产修订、设计哈希和构成哈希使用规范大写 SHA-256 文本，分别遵守[版本与规范化规则](../design/02-definitions-and-content.md#三种内容版本)。有效价格参数直接解析为整数万分比；规范编码写十进制整数，不经 `double`。连续值用文化无关的无损往返表示，拒绝 NaN 和无穷值。
- 普通输入、集合、嵌套元素、字节及返回结果都深度不可变。`ImmutableArray` 不得为 `default`。保留旧视图、草稿、资产或捕获后，新发布和候选释放不会改变它们；可以共享真正不可变的数据，不要求每次复制整个目录。
- `IContentView` 是读取入口，不是公开内部索引。核心读取 `Core` 投影；体素读取具体场景与外观投影；界面读取摘要、显示和诊断。完整创作草稿只在需要编辑的入口公开，解析器及文件布局不会沿着查询泄漏给场景。

经[契约审计修订](https://github.com/odradekk/train/issues/296)，`CoreInputId` 只对外部定义、规则、源模板及它们的核心语义引用计算；`CoreContentProjection.Designs`、`DesignGoods` 与派生缺失清单不参与身份。蓝图库条目本身不加入前两者；它们只提供新局种子、普通恢复或重载所需的业务资料。某份蓝图被开局配置实际引用，或核心恢复清单引用其设计时，按这些用途加入投影；动态建造和实例目标通过 DesignInput 单独交接，设计正文在成功激活后由内容保留。`Goods` 只放定义型货物，衍生组装体货物放在 `DesignGoods`。同一外部输入下同一设计哈希及组装体身份的业务投影必须一致。功能复合体 Goods 条目的 Revision 覆盖其全部核心语义及依赖，包括占格、质量、价值、能力、耐久、返还规格及转动的轴、枢轴和角度范围（转速只影响表现），不能只摘要 GoodDefinitionProjection 的质量与价值字段。依赖规格改变即使设计哈希未变，也须改变外部输入身份并重算；纯显示／资产更新不改变身份。

`FindDefinitionDisplay` 返回的 `ContentDisplay.Major` 只表示聚居地类型的城市尺寸标记，对应[聚居地类型](../content/settlement-type.md)的 `display.major`，省略时为 `false`；其他定义及货物、生物聚落的显示资料为 `false`。宏观地图按[美术 07](../art/07-icons-and-ui.md#宏观地图)选择圆的尺寸。该值的变化属于 `ContentChange.Presentation`，不改变核心输入身份或生成内容修订。

保存／删除本局或用户蓝图不改变核心输入，不提交核心命令。建造、改装或立即应用时，内容随 `DesignInput` 提供固定外部输入身份、完整业务设计投影和本设计及嵌套设备的全部衍生组装体货物；核心接受后自行保留。`DesignInput` 不包含布局，完整设计仍由内容持有并供存档与复现捕获。

## 加载、包隔离与固定候选

`Discover` 只返回清单、依赖和诊断，供主菜单选择；它的成功不表示资产或全部定义已经有效。`PrepareLoad` 接收启用的 Mod 身份和宿主根句柄，自行执行文件发现、严格解析、依赖排序、引用与资产检查、投影计算。根句柄可指向开发目录、`user://mods` 或 PCK；内容解释包内格式，宿主只提供文件事实。

| 目的 | 输入及行为 |
| --- | --- |
| Initial | 内置包、启用的 Mod 和可选用户库；建立本局内容为空的候选，供选开局配置 |
| Restore | 当前安装内容、已解码内容捕获和核心引用清单；还原内嵌设计、库、草稿与模板，按当前外部定义计算占位和适配输入 |
| Reload | 当前视图和核心引用清单；固定新的包字节，在候选中保留本局内嵌资料并重算受影响投影 |

Mod 缺依赖、版本不够、依赖循环、未知行为／字段、定义或资产无效时整包排除，依赖它的包级联排除；内置包无效则失败。报告必须包含这些被排除的包，不能把“还有可用内容”显示为无诊断的全部成功。候选余下的包仍须自洽。运行中原有效必需引用因为排除变为缺失时，整次重载拒绝；不能以包隔离绕过活动世界保护。热重载的资产准备失败沿用已定整次拒绝规则。

普通读档的运行缺失在 `RuntimeMissing` 中报告；本局库、用户库和已保存本局草稿中的缺失引用放在 `AuthoringMissing`。Reload 额外保护 `DraftTarget.Instance` 草稿：原本有效的引用新增缺失时，整次返回 `reference_missing`；原有缺失可保留或恢复。Restore 只报告这些草稿的缺失，不以此拒绝恢复。本局库草稿、本局蓝图和用户蓝图不受这项热移除保护。缺内容继续的选择属于宿主，只批准当前固定候选及其清单；内容不提供全局忽略开关。已确认占位在重载中可保留或部分恢复，不能新增缺失。格式损坏、载荷不一致和不可理解的状态不能通过缺内容确认跳过。

候选已固定定义和检查通过的原始资产字节，确认后不重读目录。`View` 可用于核心准备和预览；`Basis` 绑定原视图。宿主复核操作、运行会话和基准版本，完成核心裁决及所需持久写入后，才一次 `Activate`。激活仅交出预备好的固定视图，不做正常可失败工作或任意通知。未激活的 `Dispose` 放弃发布资格，重复释放无副作用；重复激活／使用已消费候选属于编程错误。已取得的不可变值仍可保留。

一个固定候选视图可以作为下一步准备的输入，例如安装内容候选继续准备新局的实例模板；只激活最终完整候选。前一候选放弃不撤销已经取得的不可变数据，不能因此清掉最终候选仍使用的宿主缓存。

`ContentChange` 是可组合的变化摘要：表现、核心输入、本局持久记录、蓝图库分别报告。核心拒绝后不得激活新内容；核心内部故障在同步 Submit 返回前已落实冻结，放弃内容候选不会解除。有效内容完全不变时不生成新发布，调用方继续原视图。

## 文件与资产端口

`IContentFiles.Enumerate` 只列直接子项，内容进行稳定排序和种类识别。根内路径使用 `/`，禁止绝对路径、`..`、空段与链接穿越；宿主必须检查实际解析路径，不能只做字符串前缀判断。链接项明确标记，读取路径经过链接返回 `LinkedPath`。读取设字节上限，超限不返回截断文件；不可读和缺失分开诊断。端口不把任意目录中的临时文件、草稿或发布恢复材料自动列成内容包。

`IAssetInspector` 检查传入的固定字节和种类。PNG 只返回宽高；GLB 返回节点、表面透明方式、变换后米制包围盒及不支持特性的事实。内容执行[全部资产规则](../design/02-definitions-and-content.md#资产的交接)，例如贴图尺寸、单格包围盒、禁止骨骼和外部图片；检查器不替内容判定合格。

缺失、超限、链接路径来自文件端口；无法解码、不支持的内容来自检查端口；元数据不符合规则另报字段错误。IO 权限／占用等不可读故障明确报告，不伪装为缺失。诊断带包、路径、定义和字段。有效 `ContentAsset` 保留同一份字节、元数据及全部来源；种类只由 `AssetMetadata.Png` 或 `Glb` 变体表示，不另存可冲突的种类字段。相同字节只有一个资产修订。Godot 按修订取原字节自己创建运行资源，不重新按来源路径读取文件。

端口只在同步调用期间使用。返回后内容不再调用端口，不持有文件句柄或解码资源；宿主内部缓存仍由宿主按候选所有权清理。加载可由宿主在其适配器支持的线程上调用，禁止并发重载和重入。发布后的只读数据可在任意线程使用；运行资源创建、切换和退役仍遵循[资产交接](README.md#表现资产的交接)。

## 设计计算和场景查询

`DesignDraft` 是可编码的工作快照，不承诺完整合法。缺必填参数可为 null，空文字、重复格、暂时不连通等可保留；非法枚举、递归对象环或不能编码的非有限数字不属于有效快照。非网格表单尚未能形成领域值的输入仍由界面持有，不能把任意 JSON 当公开模块接口。

`EvaluateDesign` 一次返回内容计算的设计身份、构成身份、属性、BOM、合法性、包络和部件定位。`Complete.Core` 为车辆、建筑、组装体和功能复合体提供 DesignInput；后二者映射 DesignKind.LargeEquipment，货物身份分别为 GoodId.Assembly 和 GoodId.Composite；只有场景模型与生物为空。`Complete` 表示计算完整，`IsValid` 才表示合法；例如完整但不连通的布局仍应报告可算出的质量与不连通位置。`Partial` 区分已知小计与每项未知结果，不以零冒充未知；占位核心投影只供已有实例恢复，不能成为新建或改装目标。`Failed` 表示无法形成计算结果；内部程序故障通过外层 `ContentResult.Faulted` 报告。

当前计算支持车辆、建筑、组装体、包内功能复合体及场景模型，连同根最多四层。自有格按 x／y／z 排序，再按子对象锚点递归；路径保留原始输入下标，功能复合体整件占一个编号。复制设计任一层不能有功能性行为，功能复合体只含无功能货物体素且不嵌套；类型、重复锚点、展开后的占格、连通、包络及行为扫格统一校验。布局完整但不合法仍返回 `Complete` 和可算出的属性，`CanConstruct=false`；调色板序号越界或车辆长度超出 Int32 则返回 `Failed`。BOM 对嵌套设备按整件计算，返还单元关联实际部件和结构货物；`DesignInput.AssemblyGoods` 包含所需组装体投影。

恢复／重载中的已保存设计缺定义时返回 `Partial`，保留原引用、编号和编辑锁；缺失体素仍有单格位置，缺失功能复合体占格未知。占位不提供能力、质量、价值、耐久上限或受伤权重，不贡献结构，`CorePlaceholder` 纳入 `Core.Designs`。直接求值缺定义或缺少车辆 `Size`、建筑 `PlotSize`／`Kind` 的新草稿也返回 `Partial`，但 `CorePlaceholder=null`；独立可算的指标和已知小计照常提供，只把依赖缺失项的值标为 `Unknown`。缺定义体素仍可确定占格，因此车辆长度可以已知；缺功能复合体则可能使占格和长度未知。无法形成规范设计身份时 `Scene.Design=null`，不以默认规格补造身份。公开 `EvaluateDesign` 对根功能复合体草稿继续抛 `NotImplementedException`：草稿没有定义身份，不能按哈希推断；包加载器在内部传入身份。玩家蓝图保存、建造目标、实例目标及比较入口先拒绝根功能复合体，返回 `design_invalid`；这些路径不尝试推断定义身份。包内缺引用或不合法设计仍排除整包。已有实例的设计正文和哈希保留，当前功能定义的占格变化只令重算结果不合法，不拒绝恢复／重载；新建和改装重新校验。

全部调色板项均校验引用、涂装、允许朝向及形状，包括未使用项；哈希只展开实际使用项。名称和说明逐层去掉首尾空白，形状省略在哈希中为 null，场景取当前定义首形状；省略朝向等同 `px_py`。同一设计的编号不因外部定义变化而改变。功能复合体进入 `Core.Goods`，语义修订覆盖占格、质量、价值、能力、耐久、返还规格、转动的轴／枢轴／角度范围及依赖（不含转速）并参与 `CoreInputId`；作为本局所需业务资料引入的组装体进入 `DesignGoods`，不改变该外部输入身份。`FindGood` 支持三类货物，库存清单中的功能复合体引用同样受缺失保护。

内容统一生成所有体素与功能复合体的编号、嵌套路径、占格和对象局部坐标。体素层可以按这些对应定位、拾取和动画，不重做哈希、编号或耐久保留算法。根坐标表示零姿态占格；对象局部坐标和父路径用于嵌套转动。`NestedObjectDetails` 将组装体转动、功能复合体引用及编号、场景模型平整区域分在各自变体中；场景模型的平整区域不要求体素层从源蓝图重新解析。缺失功能复合体使用 `MissingFunctionalComposite`，保留原引用、锚点、朝向和整件编号，不附加未知的转动参数。未知占格用 `OccupancyComplete=false`，不能假装它只占一格。

缺失项及其祖先间接操作都受 `DesignEditLock` 约束。UI 与控制台必须显示缺失身份、所属包和阻止的操作；只在 UI 隐藏按钮不能代替体素编辑入口检查。Elements／EditLocks 只在 Scene 中保留一份，场景模型平整信息只用 Scene.RootFlatten。Partial.Scene 始终存在，承载已知布局及定位；不能取得规范设计身份时仅 Scene.Design 为空。Complete 与运行场景的 Scene.Design 必须有值并与设计一致。

`CompareDesigns` 统一计算新旧部件对应、BOM 差额与能否立即应用。完整合法的目标构成不变才能立即应用；否则整份目标走正式改装。预览结果不是永久发布凭证：提交使用固定草稿和内容基准，不在途中换成最新编辑内容。普通体素同位置、同定义时保留部件，单改朝向的改动单位为零，但构成改变仍需正式改装。组装体按每层放置位置对应：同构成或仅改安装朝向时整件保留；移动算移除加新增两个单位，同位置改变内部构成算整件替换一个单位。已有设计可完整但不合法，目标必须完整合法且类别兼容；已有设计不存在或含缺失占位，以及目标缺定义时返回 `reference_missing`，目标缺规格、不合法或类别不兼容时返回 `design_invalid`。比较只预览，不改变视图。根层结构体素按种类合并返还单元：`Good` 为该体素、`Quantity=n`、`Parts=[]`、`StructuralGoods=[该体素×n]`，核心将拆下总数 n 乘以当前结构耐久比例后向下取整。功能体素每格一个单元，`Parts` 指向该部件；大型设备每整件一个单元，与设计投影的 `ReturnUnits` 一致，内部 `StructuralGoods` 不另行返还。`ChangedItemCount` 仍按格和整件计数。

`IContentView.Voxels` 提供固定世代的只读体素目录，身份唯一，只含已接受且可放置的定义，不含缺失占位。`VoxelCatalogEntry.Good` 为 null 的条目供场景体素栏使用；非 null 的条目供玩家体素栏使用，其 Good 必须是同 Definition 的 `GoodId.Voxel`，与 `Core.Goods` 中对应投影一致，直接复用质量、价值等业务资料。目录不重复显示与外观，消费者按 Definition 调用 `FindVoxelDisplay` 和 `FindVoxelAppearance`。功能复合体从现有 `Blueprints` 查询，不新增目录。

其他场景查询按目标身份取得所需资料，不要求遍历所有定义。`ContentLookup.NotFound` 表示该视图中没有该对象，`Missing` 表示有已知缺失及诊断。组装体货物的显示设计取第一次引入本局时保存的记录；后来相同构成的重命名或重涂设计不能改写它。

这里的“引入”随有效设计输入成功发布确定；预览、未发布候选和不合法草稿不会登记。初始批次及同一次发布中有多个同构成设计时，按来源库、规范蓝图身份，再按规范嵌套路径的稳定顺序确定首次资料；不取文件枚举或后台完成顺序。内容持有并捕获本局全部已登记显示记录，库存暂时归零不删除记录。普通读档优先恢复原记录，不能用当前用户库重建。

`TerrainAppearanceProjection` 分别提供地表、下层与岩层的场景体素引用，对应地貌格式的 `surface_voxel`、`subsurface_voxel` 和 `rock_voxel`；具体分层生成算法仍归体素模块。体素目录与下层引用由[体素公开契约](https://github.com/odradekk/train/issues/289)补齐。地貌格式为 `schema_version` 2：`construction_site` 是施工地块场景模型，`railway_models[].repair_site` 是断轨修复场景模型，均为必填场景模型引用。旧版本返回 `unsupported_format`。这些引用只进入场景表现，不进入核心输入身份或生成语义修订。

`DesignSceneProjection.RotationSweeps` 是内容合法性计算的设备转动几何交接，只在 Scene 中保存一份。每个已知声明 `rotation` 的根或嵌套对象恰有一项；`NestedItemIndices` 与 `NestedObjectProjection` 使用同一对象路径，空路径表示根。缺失功能复合体也保留该路径的一项，两组结果均为 `Unknown`，不能因无法取得转动声明而将它当作没有转动行为。确定没有转动的对象不产生条目。

`SweptCells` 包含对象在行为全角度范围内扫过的全部格（含自身占格）；`ConflictCells` 是内容依照设计 03 判定其中与宿主其他占格冲突或超出适用包络的格，不把对象自身占格当障碍。内容负责嵌套转动组合与排除自身的规则。两组格均为最外层设计坐标，格不重复、集合不可为 default；根没有宿主时不虚构宿主障碍或包络。两组结果分别复用 `ComputedValue<T>`：例如范围可算出但宿主存在未知占格时，范围可为 `Known`，冲突必须为 `Unknown`。只有 `Known` 空冲突集合才表示该项完整检查没有冲突；`Unknown` 携带原因，不能用已知子集或空集合冒充完整结果，也不能据此判定设计合法。

体素按选中对象路径读取当前评估条目，利用已有层级放置与本帧原点变换生成覆盖线框，随 `Present` 返回的 `ISceneFrame.DebugLines` 交出；Godot 直接绘制帧中的线框，不从 `RotationAppearance` 重算合法性几何。该结果随整份 `DesignEvaluation` 经 `AcceptEvaluation` 接入，迟到结果不能更新范围、冲突或未知提示；离散 90° 编辑动作的 `HighlightCells` 仍是另外的动作预览。

## 聚居地自己的模板副本

每个聚居地直接持有建立时复制的模板，不增加模板哈希、共享副本目录或模板哈希校验。核心内部保存该实例的地块等业务字段；内容按相同 `SettlementId` 保存布局、固定场景模型设计和来源追溯。模板里的体素、功能复合体等外部引用仍按正常规则解析；复制模板不等于冻结全部外部定义。

| 时机 | 交接 |
| --- | --- |
| 新局 | PrepareNew 在独立核心候选内选模板；References.Templates 声明所有者与来源，内容 WithSettlementCopies 从同一固定源复制，完整配对后才整体激活 |
| 建城开始 | 同步 Submit 接受后，回执的 CommandDecision.Accepted.SettlementCopies 声明最终所有者与来源；宿主立即 WithSettlementCopies，完成配对后才派发事件、保存或继续推进 |
| 建城完成 | 创建已经预留身份的聚居地，继续使用同一份副本，无需搬迁 JobId 记录或另做内容同步 |
| 模板更新 | 只替换可供以后选择的外部目录；现有所有者副本原样保留，包括进行中的建城任务 |
| 保存／读档 | 核心清单与内嵌内容按所有者配对；副本直接入档。缺副本或地块对应不一致是载荷损坏，不用当前模板补造 |

`PrepareLoad` 已为可选源模板完成布局、场景设计及业务地块对应校验；核心只能从同一固定投影选择来源。`WithSettlementCopies` 只复制不可变内存资料并关联最终所有者，不做 I/O、解析或新的合法性裁决，不返回正常失败。来源不属该固定输入、地块不配对或已有所有者冲突属于调用前提错误。意外内部故障中断建城后的关联时，宿主停用该活动组并报告，不能继续运行半配对状态。

`PrepareLoad` 按[设计 16](../design/16-settlements.md#地块与建筑)的六档包络和适配规则校验源模板，并按[模板空间规则](../content/settlement-template.md#加载器检查的规则)校验旋转后的 XZ 占地重叠与场景范围；这不在复制时重新裁决。

复制只增加本局持久修订，不改变内容世代或核心输入身份。不能把它当作读档修复入口，也不能等首次打开场景或消费领域事件再补副本。来源身份只用于初次复制和追溯，来源定义以后删除不删除已有副本。

核心 `PlotView.TemplatePlotNumber` 连接实例地块身份与内容布局中的本地地块号，场景不得把数组下标或运行时 PlotId 猜成模板号。准备和恢复时由两模块各自校验自己持有的字段与公开对应关系，宿主只协调结果，不自行比较或重建地块。

`OwnedSettlementTemplateReference.Plots` 提供核心持有的本地地块号及尺寸，内容据此检查副本配对。它不携带位置或朝向，也不要求宿主从核心载荷提取地块数据。

## 草稿、蓝图库与实例目标

本局草稿、蓝图库和实例目标通过 `PrepareEdit` 准备。`PreparedContentEdit` 给出新 UUID、设计身份或差额，调用方不靠比较目录猜测创建结果；它是准备回执，激活后才成功。新建身份在准备时固定，取消后不发布。

- 保存本局草稿只要求可编码，目标必须为本局蓝图或实例，载荷必须为 Design，否则在 `/target` 或 `/payload` 返回 `invalid_field`；调用方提供非空 `DraftId`，同一身份原位覆盖，同一实例不能保存两份不同身份的草稿。实际改变只标记 `Persistent`，增加持久修订，不增加内容世代。删除草稿不存在时返回 `reference_missing`。外部草稿通过 `EncodeDraft`／`DecodeDraft` 编解码，由宿主写 `user://authoring-drafts/`；只有明确保存为本局草稿的记录才进入游戏存档。
- 保存蓝图要求玩家设计完整合法。新建分配新 UUID，覆盖保留原身份，覆盖不存在的条目返回 `reference_missing`；包库写入或删除返回 `read_only_target`；未配置 `UserLibrary` 时，用户库保存和删除在 `/target` 返回 `invalid_field`。本局库变化标记 `Library | Persistent`；用户库变化标记 `Library`，仅新增首次显示记录时再标记 `Persistent`。用户库 `RequiredWrites` 给出 `<UUID>.blueprint.json` 的字节，删除时 `Bytes=null`；宿主完成写入后再激活。本局库只更新本局记录，下一次保存游戏才写盘。
- 删除蓝图不检查实例引用，也不删除已有实例设计。删除草稿只删除明确指定记录，不影响当前未保存工作副本。
- ConstructionTarget 准备建造需要的完整设计和嵌套依赖，并以 PreparedContentEdit.ConstructionTarget 返回 DesignInput；来源可以是本局、用户或包蓝图的固定设计。
- InstanceTarget 准备立即应用／改装的目标和差额，DesignChangeProjection.Target 携带同类 DesignInput，不混入定义更新；准备回执不另存目标哈希，统一从 Difference.Core.Target.Design.Design 读取。两种目标均不改变 CoreInputId；宿主同步 Submit 后仅接受时激活候选，不先把目标当作成功写入本局。
- 拒绝或故障属于已经录制的 Completed，宿主仍保留所提交候选的固定视图作为录制依赖；它不成为活动内容或普通保存中的成功修改。NotAccepted 不制造命令记录。

`EncodeDraft`／`DecodeDraft` 支持 `DraftPayload.Design`（目标为 `Instance`、`LocalBlueprint` 或 `UserBlueprint`），以及配对的 `DraftPayload.Definition` 与 `DraftTarget.PackageDefinition`。定义的种类和本地 ID 必须与目标一致。编解码允许规格为空及玩法上不合法的布局，保留调用方的 `EditRevision`，内容不自增修订，也不把它当锁。

Initial、Restore、Reload 都独立读取当前 `UserLibrary` 根下的 `<UUID>.blueprint.json`。读取、JSON、格式、正文身份与文件名不符、设计不合法或引用缺失只令该条目 `Usable=false` 并附诊断，不令整次加载失败，也不排除包。文件名不是规范 UUID 时，只在加载报告中诊断，不列入蓝图目录；引用缺失另列入 `AuthoringMissing`。Reload 的用户库文件变化或任一库条目的摘要变化（含可用性和诊断）标记 `Library`；用户库文件不嵌入普通存档。有效用户库设计和包库一样，按来源库、规范蓝图身份及规范嵌套路径顺序登记首次组装体显示记录；Restore 优先保留原记录，因此新增记录时持久修订增加 1。

同内容保存视为无变化，返回 `Changes=None`。所有编辑都不改变 `CoreInputId`；同次编辑的世代和持久修订各自最多增加 1。有效设计成功激活时才登记新的首次显示资料，预览、未激活候选和不合法草稿不登记。

所有编辑结果由宿主按编辑会话、EditRevision 和内容基准检查；旧结果不清除新修改的脏标记。内容不拥有活动编辑会话、选区、撤销栈或界面焦点。暂停和禁止其他世界修改沿用编辑协议，不引入并发编辑合并。

## 开发创作与包写入

开发模式用 `OpenAuthoringPackage` 取得源包的固定表单输入与来源基准。公开表单只覆盖既定的体素定义、蓝图行为、聚居地类型、生物聚落类型和模板；其他配置继续手写文件，不扩成通用表单系统。能力依据代码提供的行为推导，不能提交手写聚合能力替代计算。

当前实现提供体素、蓝图（含场景模型与生物类别）、聚居地类型、生物聚落类型和模板。生物聚落进入 `EditableDefinitions`，读取、写入、删除和外部草稿往返使用 schema 1；未完成字段在具体路径返回 `invalid_field`。

打开时只固定 `mod.json` 与 `resources/` 的文件和目录，不读取包根下的 `.git`、模型源文件或其他资料；资源文件沿用加载器的大小上限。之后 `Read` 与 `PreparePackageWrite` 只使用固定字节，不再访问文件端口，也不要求依赖包已安装。`SourceVersion` 是宿主提供的不透明字符串，原样回显。Mod 元数据来自固定 `mod.json`；没有 manifest 的 core 使用只读的创作元数据（ID／名称为 `core`，版本为 `0.0.0`，作者和依赖为空），这个版本不表示游戏版本或来源身份。

模板草稿包含显示文本，按 `display` 的 `name` 与 `description` 读写。库存项保存体素定义引用或蓝图引用及数量，草稿不判断该蓝图是功能复合体还是组装体。缺少 `display` 的模板草稿在 `/display` 返回 `invalid_field`。

`PreparePackageWrite` 把指定草稿、资产和 manifest 变更编码成确定的文件补丁。包 ID 不可通过这次操作修改；删除定义自己的目录时明确列出关联文件，不顺手删除 shared。补丁不包含未编辑文件的重写，也不表示整包已经合法。

补丁按包内相对路径的 Ordinal 顺序返回，字节与固定源文件相同的写入项不列入补丁。定义和 manifest 输出紧凑 UTF-8 JSON，中文不转成 Unicode 转义，末尾带换行。定义删除包括该定义文件、其同名目录在快照中的全部文件（包括 `.import`），以及定义文件自身的 `.import`。没有 manifest 的 core 拒绝 `Manifest`；资产编辑不能代替 manifest 编辑（不区分大小写），manifest 的原描述和依赖 `name` 保留，只按依赖 ID 增删或调整最低版本，不输出未改动的隐式 core 依赖。`PreparePackageWrite` 不检查库存蓝图类别，由普通加载确认。越出包根的路径、同次编辑重复指向同一文件（不区分大小写）或无法编码的编辑使整次准备失败，不返回部分可写补丁；非有限表单数值返回 `invalid_field`，不当作内容内部故障。PNG／GLB 的 Godot 导入声明由宿主负责。

宿主在单一写入者约定下复核源基准，按[包写入与重载](editing.md#创作模式包写入与重载)逐文件临时写后替换或执行明确删除，再调用普通 PrepareLoad／Submit／Activate。未知、未编辑文件原样保留。写入前发现外部修改拒绝覆盖并保留草稿；中途失败可能已有文件更新，报告本次已知完成项并由开发者修复，不承诺整包回滚或启动恢复。

`PackageSourceVersion` 仅传递宿主固定基准，不把时间戳比较当作完整来源比对。文件写入与内存重载是两项结果：磁盘修改不会直接修改活动视图，普通重载拒绝仍保留旧内存内容。单文件安全写入与真实文件错误由宿主验证。

## 保存与严格复现

在一致边界使用核心捕获的公开引用调用 `Capture`，内容补齐实例设计、在途任务目标、组装体货物显示资料以及本局蓝图库和已保存草稿。宿主不遍历核心 JSON。普通捕获不暗中嵌入旧外部定义；恢复使用当前安装内容，缺失原引用必须保留。内嵌条目自身损坏或缺失则报告载荷不一致，不拿当前蓝图补造旧实例。

捕获不可变，Encode 可在后台进行，不再读取活动内容。解码只接受当前格式；内容载荷、草稿载荷和完整存档各自版本化，不复用契约基线版本。结构有效但玩法不合法的已保存草稿可以恢复；损伤、模拟进度和冻结执行参数仍由核心保存，内容不重建它们。

当前普通内容载荷为版本 5；其他版本返回 `unsupported_format`。顶层字段固定为 `format_version`、`revision`、`packages`、`designs`、`assembly_displays`、`templates`、`local_blueprints`、`local_drafts`。包按身份、设计按哈希、首次显示记录按构成哈希、模板副本按所有者排序；本局蓝图按 UUID 的 Ordinal 顺序，草稿按 `DraftId` 排序。它不嵌入市场等外部规则定义或用户库文件。引用包元数据也覆盖本局库及草稿的定义引用。设计捕获覆盖核心清单、模板场景、全部已登记组装体显示记录及已有本局保存设计；蓝图库条目本身不因此加入运行设计目录。

设计条目包含 `hash`、`definition`，根功能复合体还必须带 `composite` 定义身份，其他设计不得携带该字段。设计正文复用 blueprint v3，引用全限定；模板副本复用模板 v2 的地块布局字段，场景模型原设计按设计哈希入档。本局蓝图使用玩家蓝图 v3，字段为 `schema_version`、`id`、`used_packages` 及设计正文；解码只读取结构，恢复时按当前定义重算求值。本局草稿内嵌独立的草稿 v1 对象，字段固定为 `format_version`、`id`、`revision`、`target`、`payload`；`target.kind` 为 `instance`、`local_blueprint` 或外部草稿才可用的 `user_blueprint`，`payload.kind` 为 `design`，`payload.value` 保存设计草稿。实例目标用 `target.instance` 的 `kind`（`vehicle`／`building`）和数值 `id` 表示；库目标用可为空的 UUID `target.id`。`payload.value` 复用 blueprint v3 的 `category`、`simulation`、`layout`、`appearance`、`display` 设计字段，仅允许省略 `size_class`、`plot_size`、`building_type` 以及调色板序号越界；未知字段、非 snake_case 枚举和非有限数值仍拒绝。UUID 只接受规范小写写法，实例身份必须非零。普通载荷的 `local_drafts` 只接受实例及本局库目标；重复草稿身份、同一实例多份草稿或损坏草稿返回 `inconsistent_capture`。

外部包草稿同用草稿 v1：`target` 字段固定为 `kind="package_definition"`、`package`、`definition_kind`、`local_id`、`source_version`。包 ID 为 `core` 或规范 UUID；`source_version` 不修剪、不解析，空字符串、空白和换行均原样往返。`payload` 固定为 `kind="definition"`、`definition_kind`、`value`，定义种类使用 `voxel`、`blueprint`、`settlement_type`、`settlement_template`、`lair_type`，两处种类和 `value.id` 必须与目标配对。`value` 复用对应定义文件的字段及 `schema_version`，体素／蓝图／聚居地类型为 3，模板为 2，生物聚落类型为 1；引用保持原身份，未完成的可空字段以省略表示，显示文本不修剪。模板草稿包含 `display` 的 `name` 与 `description`，缺少该字段时在 `/display` 返回 `invalid_field`，因此省略 `display` 的旧模板外部草稿不再可解码；草稿生产计划额外用 `process_kind`（`production`／`recovery`）保存工序引用种类，输出有效包时移除该字段。生物蓝图允许缺少 creature 行为的未完成草稿，完整行为使用 `hp`、`speed`、`retreat_threshold`、`raid_duration`，可选 weapon 使用 `damage`、`interval`。这类外部草稿不进入普通存档、检查点或核心命令记录。

正文哈希与键不一致、缺内嵌设计、缺副本、地块不配对、组装体缺显示记录或显示记录构成不配对，均返回 `inconsistent_capture`；JSON 损坏返回 `invalid_json`。Restore 优先使用捕获中的全部组装体显示记录，再按与 Initial／Reload 相同的稳定顺序登记当前新增构成；没有新增记录时持久修订取捕获值，有新增时增加修订。运行引用缺失列入 `RuntimeMissing`；本局库、用户库和已保存草稿的缺失引用列入 `AuthoringMissing`。

Reload 保留已有组装体显示记录并优先使用，按稳定顺序登记尚无记录的构成；新引入显示记录时增加持久修订并标记 `Persistent`，其他变化不增加持久修订。原有效必需定义或设计依赖新增缺失时整次返回 `reference_missing`；已经确认的缺失可保留或恢复。有效变化才增加内容世代；表现与开局目录资料、核心输入、包蓝图库分别按实际变化标记 `Presentation`、`CoreInput`、`Library`。包蓝图增加、删除或正文变化不因其设计目录变化而修改核心输入身份；源模板的核心语义及依赖仍按既有身份规则计算。无有效变化时复用基准视图。

`CaptureReplay` 使用宿主保留的原内容视图及核心所需输入清单；缺历史即失败，不能读取安装目录的当前文件补齐。它保存复现需要的语义资料及原资产字节，DecodeReplay 返回仅由包内材料重建的历史投影，交给核心核对原输入身份。普通存档与严格复现不共用“缺失时补当前定义”的策略。宿主按核心录制反馈释放历史视图前，还须确认活动会话、场景和其他捕获不再使用它。

当前复现内容载荷独立使用版本 6；其他版本返回 `unsupported_format`。顶层字段固定为 `format_version`、`blobs`、`history`，字节块按 SHA-256 的 Ordinal 顺序，历史保持调用方传入顺序。PrepareLoad 固定安装描述、目录枚举、文件读取（含失败）及资产检查结果；原始字节按 SHA-256 去重。每个历史视图按 `input`、`generation`、`runtime_designs`、`source`、`content`、`missing` 保存原输入身份、世代、运行设计哈希集合、来源事实、普通内容 v5 载荷及缺失引用；运行设计哈希按 Ordinal 排序，独立于仅供显示或库使用的内嵌设计；因此本局库和草稿随各自历史视图保留。来源事实包含当时的用户库枚举和文件读取结果，严格复现不读取当前用户库。DecodeReplay 校验字节哈希，使用内存文件端口及记录的检查结果重跑同一个加载器，再接入内嵌资料并核对输入身份；不会重新访问安装目录或外部检查器。资产规则继续执行，PNG／GLB 检查事实来自原加载。清单中的输入身份、设计或模板所有者缺历史材料时返回 `inconsistent_capture`。

两种载荷先检查版本，再检查该版本的字段。复现中的枚举用线上名称编码，PNG／GLB 检查事实使用显式 snake_case 字段；被排除包的非有限包围盒坐标用 `nan`、`positive_infinity`、`negative_infinity` 保留，解码后仍由资产规则拒绝，不成为有效业务数值。缺失记录必须与重建内容的缺失清单逐条一致。CaptureReplay 在准备时就固定并校验各视图的内嵌捕获，Encode 不重新生成它们。

Restore、Reload、Capture 及 CaptureReplay 使用本内容实现产生的视图或捕获；外来对象、null 或未初始化的引用／历史数组属于调用前提错误，在加载或结果包装之前抛 `ArgumentException`／`ArgumentNullException`。宿主端口违约仍按内部故障报告。Reload 对启用包的资产准备失败整次拒绝，诊断阶段为 `reload`，不会以包隔离继续发布。

历史数组按视图产生的先后顺序传入并保持该顺序。同一 `CoreInputId` 可以有多个持久修订和设计集合；不能只按输入身份去重而丢掉后来引入的设计或实例副本。命令拒绝／故障所引用的目标及新局起点的实际设计同样保留；不能因候选未激活而遗漏。捕获必须覆盖录制引用清单中的全部模板所有者，缺任一必需副本即失败。重放读取布局时，在当前核心输入身份对应的历史中使用包含该所有者的最后一个视图，不跨核心输入取布局，也不调用 WithSettlementCopies 重新生成。严格复现保证原核心输入与固定副本，不承诺还原纯表现发布的逐帧时间线。

`SettlementTemplateProjection.PlatformLengthMeters` 由 Content 按设计 16 的站点包络，取模板内站点地块沿轨长度的最大值；有站点地块时为正有限值，没有时为 0。建立聚居地时，该值随模板业务副本保存，作为节点站台段长度。

## 规则格式与验证责任

内容解析[人口](../content/population-rule.md)、[市场](../content/market-rule.md)、[施工](../content/construction-rule.md)、[铁路](../content/railway-rule.md)、[战斗](../content/combat-rule.md)、[聚落](../content/lair-type.md)、[生成配置](../content/generation-profile.md)和[地貌](../content/terrain-type.md)，直接生成核心已有输入及场景投影。铁路规则使用 `schema_version = 2`，向核心提供 `ApproachLengthMeters`、`DepartureLengthMeters`、`NodeSpeedLimitMetersPerSecond`，均为正有限值，初值分别为 80 米、80 米、15 米/秒。市场规则使用 `schema_version = 2`，新增必填正 `int` 的 `MaximumLeaseDurationMultiple`（JSON 字段 `maximum_lease_duration_multiple`），并纳入语义修订；内容是该投影的唯一生产方，负责固定租期及最大倍数的正数校验。旧市场版本返回 `unsupported_format`。地貌类型使用 `schema_version = 2`，旧版本同样返回 `unsupported_format`。每类独立 Schema；未知字段、重复身份、错类引用和依赖未声明都拒绝。临时市场表仅作历史格式记录，目标聚居地类型移除临时供需和站台数。生产计划由模板表达，生成器执行库存与现车初始化；内容不计算运行库存目标、报价、消费满足率或当前劳动力。

六项人口需求率均为有限正数，六类生活货物各指定一种，只有加工食品具有已定的食物／改善饮食双换算。价格参数使用万分比整数，金额与费率用整数；超出字段表示范围是无效文件，不增加运行中金额越界的业务分支。按合法输入计算时发生意外溢出属于内部故障，不能伪装为设计不合法。

`ContentResult.Failure` 用于文件、格式、依赖、引用或发布准入等预期失败；Faulted 用于内容内部程序故障。它们都不修改传入视图，不冻结当前核心。核心创建／恢复候选或同步执行命令时的故障才按核心契约处理。稳定诊断代码至少覆盖 `file_missing`、`file_too_large`、`linked_path`、`file_unreadable`、`invalid_json`、`unsupported_format`、`invalid_field`、`duplicate_identity`、`dependency_missing`、`dependency_version`、`dependency_cycle`、`reference_missing`、`reference_wrong_kind`、`asset_decode_failed`、`asset_unsupported`、`asset_rule_failed`、`design_invalid`、`inconsistent_capture`、`stale_basis`、`read_only_target`；调用方不能解析 Message 作判断。

本票验证声明／消费样例可编译、实际独立目录可构建、Schema 与样例及文档一致性。后续共享用例必须覆盖包级失败和级联、固定字节、旧视图保留、部分设计、锁定缺失项、合法性门槛、差额对应、两类保存、候选放弃、原输入复现及源包冲突。真实 Godot 解码和真实文件发布分别验证，替身和 Schema 不能代替。

实例化和加载的时延、设计计算规模、保留世代内存、原始资产字节去重以及捕获编码体积由内容实现测量。同步入口不承诺固定时限或零分配；宿主按固定输入调度长操作。取消使结果失去提交资格，不承诺强制打断线程或立即停止文件读取。

## 本次规划验证

2026-10-10 使用仓库锁定的 .NET SDK 10.0.112，核心与内容消费样例在工作树均构建成功，零警告、零错误。另在新临时目录仅复制 `global.json`、两个契约项目及两个样例项目的源文件；未提供产品实现源码或程序集，两组样例再次构建通过。该检查证明声明依赖和调用结构成立，不证明业务行为。

`python docs/content/tools/validate_examples.py` 检查 57 份示例，零错误；本次改动的 20 份 Markdown 中 328 个本地链接和锚点检查通过，新增与修改文本的空白检查通过。接口审查与格式对照已处理实例模板配对、同输入身份的复现副本保留，以及地貌、生物聚落和生成配置显示资料的源字段。完整独立行为用例、真实文件发布和 Godot 验证仍未执行。

## 施工能力输入（#501）

`construction` 使用 `work_rate`、必填 `max_plot_size` 与可选 `max_size_class`。建筑上限仅普通小、中、大、巨型四档，站点取值拒绝；车辆档位缺省表示不能造车。体素与功能复合体复用同一解析、规范写回及语义数据，voxel／blueprint Schema 保持 v3。建筑蓝图加载时按自身 `plot_size` 包络检查所有施工部件的最高车辆档位（含嵌套功能复合体），高度须容纳，水平允许旋转 90°；不满足则拒绝。内容层不在本票执行施工命令，运行期档位检查交接见 [Core 契约](core.md)。
