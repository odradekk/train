# 三个开发者工具界面盘点

三个界面都靠代码逐控件拼装，没有 .tscn。「确认」表示直接读到代码；「推测」表示由 Godot 默认行为或代码推断，未实机验证。

## 1. 控制台

文件：`Ui/GameConsole.cs`；分发 `App/Console/ConsoleCommands.cs:77-123`；装配 `App/App.cs:92-112`。

| 命令 | 语法（原文） | 作用 | Release |
|---|---|---|---|
| help | `help` | 列命令名，无子命令说明，多余参数忽略 | 可用，只列 help, clear, quit, loglevel (:147) |
| clear | `clear` | 清空内存日志缓冲 | 可用 |
| quit | `quit` | 经存档离开保护后退出 | 可用 |
| reload | `reload` | 重载内容（Debug 另有 Ctrl+Shift+R） | 不可用：`reload is only available in debug builds.` |
| loglevel | `loglevel` / `loglevel debug\|info\|warning\|error` | 查询或设置全局日志阈值 | 可用 |
| sim | `sim [status\|pause\|resume\|speed 1\|2\|4\|8\|step [N]\|checkpoint]` (`SimulationConsoleCommands.cs:25`) | 查状态、暂停、倍速、手动步进、取检查点摘要 | 不可用：`Unknown command: sim` |
| train | `train form <车辆ID>...`、`train disband\|start\|stop <列车ID>`、`train plan <列车ID> once\|loop station:<节点ID>:<停站秒数> waypoint:<节点ID>...` (`TrainConsoleCommands.cs:35`) | 编组、解编、启停、替换运行计划 | 同 sim |
| lighting | `lighting [daylight\|overcast\|night\|restore]` | 切换检查光照 | 不可用：`lighting 仅在 Debug 构建中可用。` |
| dev | `dev [open\|close]` | 开关开发检查器 | 同 sim |

**输入体验**
- 打开：反引号或 `~` 切换，Esc 关闭 (`GameConsole.cs:89-106`)。这是全局 `_Input`，在其他文本框里输入反引号也会触发（推测）。
- 位置尺寸：CanvasLayer 128，贴窗口底边，全宽，高度固定 360px，背景半透明深色 (:33-57)。未见拖动或缩放代码。
- 输入：单个 LineEdit，占位文字 `help`，提交后保持焦点。确认没有上下键历史、自动补全、引号和转义（整份文件无相关代码）。
- 输出：RichTextLabel，BBCode，自动跟随底部，可鼠标选择 (:61-68)。格式为 `[级别][时间][分类][事件ID] 消息 key=value`。
- 颜色 (`LogEvent.cs:281-292`)：Error `#ff6b68`、Warning `#e5c07b`、Debug 和时间分类 `#7f7f7f`、命令回显 `#56b6c2`，Info 用默认色。
- 容量：缓冲 500 条 (`LogSinks.cs:37`)；消息截断 200 字符 (`LogEvent.cs:298`)。
- 内容：显示所有分类的日志，不只命令结果。
- 刷新：缓冲一变就清空并重建全文 (:162-164)，所以选区和滚动位置可能丢失（推测）。
- 复制：没有按钮或菜单；`FocusMode.None` 下键盘 Ctrl+C 很可能无效（推测）。
- 清屏：只有 `clear`。
- `loglevel` 的阈值在日志发出前生效 (`TrainLogger.cs:223`)，所以它同时改变磁盘日志。默认 Debug 构建 Debug，玩家构建 Info (`App.cs:55`)。

**失败输出（原文）**
- `foo` → `[WARN ][..][console][unknown_command] Unknown command: foo`
- `sim speed 3` → `[WARN ][..][simulation][simulation_usage] 用法: sim [status|pause|resume|speed 1|2|4|8|step [N]|checkpoint]`
- 运行中 `sim step` → `请求推进 1 步，实际推进 0 步。模拟操作未完成：simulation_requires_pause/<field>。` (`SimulationConsoleCommands.cs:128,203`)
- `train` 语法错：写 Warning 日志 `train_usage`，同时弹出游戏里的模态面板「操作未完成」(`TrainConsoleCommands.cs:26-30`，`GameUi.cs:678`)。Core 拒绝时记 `train_command_failed`，例如「列车没有运行计划。」(`CommandDiagnostics.cs:24,131`)。
- `dev foo` → 控制台无输出。用法提示只写进检查器自己的消息栏 (`DeveloperInspector.cs:54`)，窗口没开就看不到。
- 基础命令的提示是英文，sim、train、lighting 是中文。

## 2. 开发检查器

文件：`Ui/DeveloperInspector.cs`、`App/Development/DeveloperTools.cs`、`InspectorData.cs`。

- **构造**：只在 Debug 构建创建，唯一入口是 `dev` 命令 (`App.cs:97-103`)。Godot `Window` 900×700，居中弹出，非独占，每 0.25 秒刷新。`project.godot` 未改子窗口设置，按默认应嵌在主窗口内（推测）。
- **页面外**：顶部有状态和消息两个 Label。状态含「只读重放/活动会话 · 步:序号 · 自动推进」、「暂停：玩家= 焦点= 操作= 冻结=」和停止原因。
- **消息栏**：`_localMessage` 优先于 `LastMessage` (:226)，本地提示会一直保留，直到下一次 `Run` 或捕获成功才被清掉。
- **标签**：除 CheckBox 外，控件都没有独立标题，只靠占位文字、下拉项和按钮文字区分。

| 页 | 控件（原文） | 动作与调试任务 |
|---|---|---|
| 推进与重放 | 说明文字「重放为只读…」；`推进步数`（默认 1）；按钮 `暂停` `单步` `推进指定步数`；下拉 `到达步号/命令被拒绝/对象故障/领域事件`；`步号 / 命令 kind / 事件类型名`；CheckBox `拒绝条件匹配已选对象（可选）`；`可选最大步数`；`运行至条件` `取消自动推进`；`重放位置 step:sequence`；`重放至位置` `打开复现包` `导出当前重放边界` | `暂停`只暂停，没有「继续」按钮。`单步`和`推进指定步数`先暂停主机，再按每帧 5ms 预算逐步推进。`运行至条件`仅普通会话；事件条件填 CoreEvent 嵌套类型名，如 `TrainArrived`。`重放至位置`和导出仅重放会话。`打开复现包`选 zip 做隔离重放。导出遇已有文件弹「覆盖文件」确认。用途：复现故障、定位出问题的步、把重放边界转成普通存档。 |
| 对象 | 下拉 12 项（节点、线路、列车、车辆、建筑、聚居地、库存、任务、战斗、生物、生物群、巢穴）；`稳定对象编号`；`查询对象`；只读文本框 | 按类型和非零编号查只读视图，显示 Found/Missing/Unavailable 加缩进 JSON，随刷新更新。用途：看实体内部字段，并为「对象故障」和「拒绝匹配」条件指定对象。 |
| 命令 | `kind 筛选（空为全部）`；下拉 `全部/accepted/rejected/faulted`；CheckBox `按检查对象筛选`；关联对象下拉（每项带「存在/已失效」）；`检查关联对象`；只读文本框 | 显示录制或重放窗口范围、缺口，以及每条命令的序号、前后位置、kind·结果·来源、关联对象和规范参数。kind 精确匹配。只有 content.publish、train.form、train.disband/start/stop/replace_run_plan、rail.debug_add_break 能解析关联对象，其他 kind 只给原始参数 (`InspectorData.cs:82-96`)。`检查关联对象`把它设为已选对象，不切到对象页。用途：查命令为何被拒、谁动了某对象。 |
| 检查点与标记 | `检查点说明`；`捕获内存检查点`；两个无标题下拉（期望、实际）；`比较检查点`；`说明标记`；`添加说明标记`；只读文本框 | 检查点含位置、世界种子、运行环境、核心输入身份和 SHA-256，只存内存。比较先判位置、种子、环境（不同即「不可比」），再判输入（不同即「内容适配」），一致才让核心比较，结果是「检查点一致。」或首个差异字段。用途：确定性比对、给时间点写注记。 |
| 场景与性能 | 5 个 CheckBox，文字是枚举英文名 `Chunks` `RailwaySegments` `Origin` `PickRay` `Centerline`；`重新生成当前场景`；性能文本 | 开关场景调试线；重新生成不改权威状态。文本显示进程帧耗时（秒）、静态内存（字节）、对象数、请求与实际速度（跟得上/未跟上/已暂停）、调试线数，以及「模拟耗时与录制容量：未采集」。 |

按钮禁用规则在 :335-352：忙碌或冻结时推进类禁用，重放时禁用`运行至条件`，普通会话时禁用`重放至位置`。

## 3. 创作面板

文件：`Ui/AuthoringPanel.cs`、`AuthoringDefinitionForm*.cs`、`App/Authoring/*`。

- **入口**：顶栏按钮「创作…」，仅 Debug 构建且有非重放会话时显示 (`GameUi.cs:243-303`，`AuthoringService.cs:35`)。
- **窗口**：面板标题「内容包创作」，位置 (30,70)，宽 520，滚动区至少 480×650 (`AuthoringPanel.cs:56-57`)。

**两种源**

| | 开发源码 core 包 | 本地 Mod 目录 |
|---|---|---|
| 按钮 | `打开开发源码 core 包`，仅 Debug 且从 Godot 编辑器运行时显示 (`App.cs:67`) | `Mod 包 ID` 输入框加`选择本地 Mod 目录` |
| 目录 | 仓库 `builtin/core` | 必须是 `user://mods` 的直接子目录，ID 不能是 `core` (`AuthoringService.cs:68-71`) |
| 写入 | 写回仓库源文件；新增 png/glb 自动补 `.import` (`PackageFiles.cs:155-161`) | 写入 `user://mods/<目录>` |

另有`重新载入当前源包`。

**定义种类**
- 可编辑的是 `DefinitionDraft` 的 5 种：Voxel、Blueprint、SettlementType、LairType、SettlementTemplate (`Contracts/Content/Authoring.cs:62-70`)。`DefinitionKind` 其余 8 种（Process、PopulationRule、MarketRule、ConstructionRule、RailwayRule、CombatRule、GenerationProfile、TerrainType）没有表单。
- 新建下拉 10 项：车辆、建筑、组装体设备、功能复合体、场景模型、生物（都是 Blueprint，规格不同）、体素、聚居地类型、生物聚落类型、聚居地模板。
- 新建 Blueprint 进体素设计编辑器，不是表单。Blueprint 的表单要在定义列表点`编辑表单`。非 Blueprint 种类的`网格编辑`和`编辑表单`打开同一个表单 (`AuthoringService.cs:121-129`)。

**表单字段**（均带`本地 ID`）
- 字段形式：全是单行 LineEdit 加上方 Label。布尔值用 CheckBox，枚举是自由文本（不是下拉），列表用逗号分隔文本。行列表有`添加X`和`删除此行`。
- 校验：解析错误只在`保存草稿`或`更新预览`时出现，格式为「<标签>：<输入> 格式无效。」(`AuthoringDefinitionForm.cs:75-82`)。

- **体素**
  - 基本：`名称` `说明` `图标资产路径`。
  - 模拟层：CheckBox `启用模拟层`；`基础价值（可空）` `质量吨（可空）` `最大耐久（可空）` `单位燃料能量（可空）`；CheckBox `原材料`。
  - 行为行：动力(`牵引力 kN` `额定速度 m/s`)、控制、库存(`库存容量`)、生产(`生产工作量/秒`)、开采(`开采工作量/秒`)、施工(`施工工作量/秒` `最大建筑地块尺寸` `最大车辆尺寸（可空）`)、供能(`能量/秒`)、武器(`武器伤害` `武器间隔秒数`)、站台。
  - 朝向与碰撞：`允许朝向 None/FourWay/All24`；`碰撞 None/FullBox/LocalBox`；`碰撞偏移 X/Y/Z` `碰撞尺寸 X/Y/Z`。
  - 表现：`表现 None/Cube/Mesh`；Cube 为`形状（逗号分隔）` `材质类别` `贴图跨度` `顶面贴图` `侧面贴图` `底面贴图` `正面贴图（可空）`；Mesh 为`模型路径` `各表面材质（逗号分隔）`。
  - 其他：CheckBox `轮组表现` `灯光表现`；`灯光颜色`（默认 `#ffffff`）`灯光范围格数`。
- **蓝图**：没有名称和说明字段。
  - 车辆、建筑、场景模型：只有按钮`编辑体素网格`，规格值在设计编辑器属性页改。
  - 组装体设备：CheckBox `转动表现`、`转轴 X/Y/Z`、`支点 X/Y/Z`、`最小角度`、`最大角度`、`角速度`。
  - 功能复合体：行为行（同体素）加转动表现。
  - 生物：CheckBox `生物行为`(`生命` `移动速度` `撤退阈值` `袭击持续秒数`)、CheckBox `生物武器`(`伤害` `攻击间隔秒数`)。
- **聚居地类型**：`名称` `说明` `地图标记资产`；CheckBox `主要聚居地`；`权重（可空）` `偏好地形 ID（逗号分隔）` `最少人口（可空）` `最多人口（可空）`。
- **生物聚落类型**：`名称` `说明` `场景模型蓝图 ID（可空）` `生成权重（可空）`；成员行(`生物定义 ID` `成员权重`)；`最大成员数（可空）` `生成间隔秒数（可空）` `袭击间隔秒数（可空）` `游荡半径跳数（可空）` `袭击半径跳数（可空）` `破坏铁轨工作秒数（可空）` `清除奖励（可空）`。
- **聚居地模板**：`名称` `说明` `聚居地类型 ID（可空）` `权重（可空）` `场景模型蓝图 ID（可空）`。
  - 地块行：`地块编号` `地块尺寸` `位置 X/Y/Z` `朝向` `预建建筑蓝图 ID（可空）`。
  - 起始建筑行：`地块编号` `建筑蓝图 ID`。
  - 初始库存行：`引用类型 voxel/blueprint` `引用 ID` `数量`。
  - 生产计划行：`地块编号` `能力 Production/Extraction/Construction` `工序类型 Production/Recovery` `工序 ID` CheckBox `启用计划` `优先级`。
  - 按钮`更新预览`。

**枚举取值**
- PlotSize：Small、Medium、Large、Giant、SmallStation、LargeStation。
- SizeClass：Small、Medium、Large、ExtraLarge、Titan。
- AllowedVoxelOrientations：None、FourWay、All24。
- MaterialCategory：PaintedMetal、BareMetal、Concrete、StoneSoil、Glass、PolymerRubber、Organic。
- RotationAxis：X、Y、Z。
- CapabilityKind：Production、Extraction、Construction。
- VoxelOrientation：24 个，`PxPy` 到 `NzNy`（`Contracts/Content/Designs.cs:9-14`）。
- 解析区分大小写（`Enum.TryParse` 默认）。`地块尺寸`、`朝向`、`材质类别`、`最大建筑地块尺寸`、`最大车辆尺寸`的标签没有列出可选值。

**引用字段**（全是纯文本，表单里没有选择器，也不检查存在性）

| 字段 | 引用的定义 |
|---|---|
| `偏好地形 ID` | TerrainType |
| `聚居地类型 ID` | SettlementType |
| `场景模型蓝图 ID`、`预建建筑蓝图 ID`、`建筑蓝图 ID` | Blueprint |
| `生物定义 ID` | 生物蓝图（CreatureDefinitionId） |
| `引用 ID` | 随`引用类型`：voxel 指体素，blueprint 指蓝图 |
| `工序 ID` | Process，随`工序类型`分生产工序和回收工序 |
| `地块编号`（起始建筑和生产计划） | 同模板内的地块编号 |
| 图标、贴图、模型、地图标记 | 包内资产相对路径，不是定义 |

**写入流程**
1. 选源后后台读取快照，提示「正在读取源包…」「源包已载入。…」。
2. 表单打开时主机进入编辑暂停 (`host.BeginEditing`)，世界视图回到地图，此时 `sim`、`train` 返回 `editing_active`。
3. `保存草稿`写入 `authoring-drafts/packages/<包ID>` 并加入待写入集，提示「包草稿已保存，尚未写入有效包。」。关闭有未保存修改的表单时弹「表单有未保存修改」：`保存草稿并继续` / `放弃本次修改` / `取消`。
4. 资产：`资产包相对路径，如 textures/metal.png` 加`选择并加入资产`，读入字节后提示「资产已加入待写入修改。」。没有待写资产列表。
5. 点`写入包并普通重载`前，按钮上方有提示「最终源基准复核到写入完成期间，请确保只有此工具写入源包。」
6. 点击后先关闭表单。没有已保存修改时提示「没有已保存的包修改。」。
7. 否则后台依次校验补丁、加载并校验候选包（目标包必须 Available）。再复核源基准：源包被外部改过则一个文件都不写。通过后逐文件原子写入，最后普通重载内容 (`AuthoringService.cs:231-270`)。
8. 结果、进度和所有错误都显示在面板最底部的同一个 Label `AuthoringResult` (`AuthoringPanel.cs:86,136-139`)。没有弹窗；只有 Core 拒绝才写日志。
9. 写入后追加「已完成：… 失败：… 未执行：…」，必要时加「临时文件清理失败：…」。成功时提示「文件已写入。普通重载成功；草稿保留。请重新载入源包后继续创作。」。失败时提示「内存内容未切换；草稿保留。」，后接逐条诊断 `code：message · subject · 包 · field · element`。

**模板预览与地块编辑**
- **条件**：`TemplateSceneController` 只在 Debug 构建创建，是独立 3D 视图和相机。
- **预览**：`更新预览`异步生成预览。
- **清空**：任何字段编辑都会清空预览 (`MarkFormDirty`，`AuthoringService.cs:153`)，需要再点一次。
- **拾取**：点击预览中的地块，射线拾取返回地块在 `地块` 行列表中的下标 (`DraftIndex`，见 `Scenes/TemplatePreview.cs:123`、`docs/contracts/scenes-editing.md:51`)。
- **回到表单**：面板对该行调用 `GrabFocus` (`AuthoringDefinitionForm.cs:35-38`)。它不高亮预览，也不写回字段；是否自动滚到该行未见设置（推测不会）。
- **缺口**：没有在预览里拖动地块或改坐标的交互。

## 需注意的现状问题
- **`dev` 用法提示**：`dev` 的用法提示在窗口未开时不可见。
- **控制台复制**：控制台没有明确的复制方式。
- **检查器标题**：检查器的下拉和输入框缺少标题，两个检查点下拉无法分辨期望和实际。
- **创作结果位置**：创作结果和错误都在滚动区最底部，表单很长时容易看不到。
- **引用字段**：引用字段和枚举都是手输文本。