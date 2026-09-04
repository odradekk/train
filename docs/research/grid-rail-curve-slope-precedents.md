# 网格与体素列车游戏的曲线、坡道方案先例

研究票：[调研网格列车游戏的曲线与坡道轨道方案](https://github.com/odradekk/train/issues/28)

研究日期：2026-09-04

本票只收集先例与可用启发。单格 90° 弯与 1:1 坡道是否保留、要不要跨格平滑，交给原型票决定。

## 结论

- **没有一款以“真实铁路观感”为卖点的游戏使用单格 90° 弯。** 唯一使用的是 Minecraft 原版，而它的车辆只有 1 格长，且明确允许矿车“沿对角线切过弯道格”。Factorio 的最小转弯半径是 13 tile，Create 的最小弯道半径是 8 格，Transport Fever 2 默认最小半径 60 m（吸附时 44 m）。
- **曲线半径与车辆接触点间距的比例是可迁移的量。** Factorio 全部车辆的转向架间距（`joint_distance`）都是 4 tile、车间距 3 tile，最小半径 13 tile ≈ 3.25 倍转向架间距。本项目若保留单格弯（等效半径 0.5 格），车厢两接触点间距必须压到 1–2 格，否则必然切角。
- **格子拓扑与连续几何必须解耦，而且解耦方式高度一致：拓扑定义连接与合法性，几何由沿路径放样（loft/extrude）的程序化网格生成。** Voxel Tycoon 的 `.rail` 资产把轨道拆成 rails/sleepers/ballast 分别放样与重复，TF2 用 4 m 的 `shapeStep` 步进生成，Create 用“aligned 轨道 + 只能整组增删的 unaligned 平滑段”。这正是 §59 TrackRenderer 的做法。
- **Factorio 的几何铁律值得直接照抄：每个连接点必须落在整数网格坐标上。** 他们为此把曲线半径从 11 提到 13——只因为半径 11 时曲线中点会落在格心这种“不允许定义的位置”，导致曲线锯齿。判定标准是开发者自己的一句话：不像自然圆弧就不接受。
- **1:1 坡道（100%）在同类游戏中是极端值。** OpenTTD 一格升一级等于 1:2（50%），TF2 建造上限 7.5%、引擎允许到 12%/15%，Factorio 用一个 16 tile 长的整体坡道实体完成一次层间过渡。只有 Minecraft 原版用 45°，代价是坡顶坡底完全没有竖曲线。
- **坡顶坡底的折角是所有先例都单独处理过的问题。** Create 直接拒绝手工搭的平→45° 过渡（“列车不会识别”），必须用辅助放置生成平滑段；Factorio 把过渡封进不可拼错的单一实体；OpenTTD 让每节车各自带 GoingUp/GoingDown 标志，跨坡顶时上下坡阻力自然抵消。本项目的 Front/Rear Contact 姿态模型（§53）本身就能吃掉大部分坡顶折角，前提是接触点各自求值、车体姿态由两点连线决定。
- **玩家对“陡”和“急”的容忍度取决于速度与车长，不取决于坡度数字本身。** Voxel Tycoon 官方教程建议干线避开坡道、弯道尽量大半径，但明确说站场环线的急弯无所谓，因为那里本来就慢；OpenTTD 对 90° 转弯锁死 61 的速度上限并提供“禁止 90° 转弯”开关，而不是禁止建造。

## 事实、结论和建议

### Factorio 2.0：整数网格上的曲线几何

**观察**：官方 wiki 记载最小转弯半径为 13 tile，最小 180° 掉头两侧轨道中心相距 26 tile，因轨道本身两格宽，实际占地 28 tile；2.0.7 引入 rail ramp、rail support、高架轨道，同时更换了全部曲线形状并新增 22.5° 轨道。[Rail - Factorio Wiki](https://wiki.factorio.com/Rail)

**观察**：铁轨放置在**两格网格**上，一条轨道不能只平移一格；两条直轨的十字交叉不能当道岔用，因为列车有最小转弯半径；高架轨道每 16 格直轨需要一个 rail support。[Railway - Factorio Wiki](https://wiki.factorio.com/Railway)

**观察**：开发者在重写轨道系统时给出的唯一硬性几何规则是“每个连接点都必须落在整数网格坐标上”，理由是铺轨工具与信号位置需要靠它保持简单。把曲线一分为二后，新的控制点会落在无法定义的位置（格心），成品曲线呈锯齿；把最小半径从 11 提到 13 之后，两端是完整圆弧的一段、中间控制点正好落在整数网格上，曲线才成立。他们同时承认 1.1 的旧曲线在端部“比自然圆弧尖锐得多”。[Friday Facts #377 - New new rails](https://factorio.com/blog/post/fff-377)

**观察**：拆半曲线的动机是让相反方向的两个半弯直接对接形成 S 弯，再用半对角（22.5°）轨道把 S 弯拉到任意长度，从而在不新增“S 弯专用件”的前提下实现 2 格、4 格的横向偏移。他们否决了“直接加一个 S 弯特殊件”，因为那会让同一位置的曲线与 S 弯互斥，或者把每个轨道的可选连接从 3 个增加到 5 个，既拖慢寻路又让手动驾驶时方向键语义不清。[Friday Facts #377](https://factorio.com/blog/post/fff-377)

**结论**：Factorio 的经验是“格子对齐”只约束**连接点**，不约束曲线中间的形状；而一旦控制点被迫落在非法位置，唯一的出路是把半径调大到几何自洽，而不是接受锯齿。

**建议**：把“Port 中心与格边界对齐、跨格样条控制点只允许落在格边中点或格角”写成 TrackRenderer 的显式约束；若某个半径下控制点无法落位，优先加大等效半径（跨格），而不是加特殊件。

### Factorio 2.0：坡道作为单一多格实体

**观察**：rail ramp 是游戏中最长的实体，16 tile 长、4 tile 宽，只能有 4 个朝向；rail support 有 8 个朝向、约 4×4 的碰撞箱；高架轨道与地面轨道拥有完全相同的形状集合，信号可以直接装在高架轨上。[Friday Facts #378 - Trains on another level](https://factorio.com/blog/post/fff-378)

**观察**：坡道的前 4 tile 必须建在实地上；停在坡道上的机车不会下滑；坡道不影响列车加速度。[Rail ramp - Factorio Wiki](https://wiki.factorio.com/Rail_ramp)

**结论**：Factorio 没有“逐格坡轨”，一次层间过渡就是一个不可拼错的整体件，坡顶坡底的竖曲线被封装在实体内部，且刻意不做重力与坡度阻力模拟。

**建议**：如果以后要 1:2、1:4 缓坡，优先考虑“多格坡道由 RailConstructionPlan 一次性生成并校验”（§49），而不是给 Port 增加高度语义的组合爆炸。首版 1:1 坡也可以按同样思路把“入坡格 + 出坡格”的竖曲线固定在生成器里。

### Factorio：车辆姿态由两个转向架 joint 决定

**观察**：`RollingStockPrototype` 有两个几何字段：`joint_distance` 是本车前后两个 joint 之间的长度，`connection_distance` 是本车 joint 到相邻车 joint 的距离；两者上限都是 15；文档明确写着“轮子精灵按 joint 位置绘制”，并要求碰撞箱边缘到 joint 至少留 0.2 的余量。[RollingStockPrototype - Prototype Docs](https://lua-api.factorio.com/latest/prototypes/RollingStockPrototype.html)

**观察**：基础游戏的 locomotive、cargo-wagon、fluid-wagon、artillery-wagon 全部使用 `connection_distance = 3`、`joint_distance = 4`。[wube/factorio-data base/prototypes/entity/trains.lua](https://github.com/wube/factorio-data/blob/master/base/prototypes/entity/trains.lua)

**结论**：Factorio 的车辆姿态模型与 §53 完全一致——两个接触点分别在路径上求值，车体挂在两点之间；最小曲线半径 13 与转向架间距 4 的比值约 3.25，是“半径不能小于车辆自身尺度”的量化表达。

**建议**：把 Composite 的 Front/Rear Contact 间距作为一个显式参数，并在原型里测量“接触点间距 : 等效曲线半径”的可接受下界；先例给出的参考区间是 1:3 左右，本项目若坚持单格弯（等效半径 0.5 格），接触点间距应压到 1–2 格。

### Voxel Tycoon：一切绑定网格的代价与轨道几何生成方式

**观察**：官方开发日志承认最初的轨道与道路系统“很快就触到了上限”，再加平交道口、复杂交叉或多车道只能在一个僵硬的基础上堆特例，因此做整体重写；重写后的路口系统内部支持 360° 任意角度，但“实际游戏里交叉沿网格走，玩家通常在 90° 或 45° 建”。开发者明确写道：Voxel Tycoon 相比同类交通与城建游戏的**额外**约束就是“一切都紧绑在网格上”，在这个限制下把程序化几何与网络交互做可靠是这次重写耗时的主要原因。[Development Update: Rails and Roads Rework](https://voxeltycoon.xyz/devlog/2025-winter-development-update)

**观察**：官方 modding 文档显示轨道由三部分构成——rails、sleepers、ballast。rails 与 ballast 用 `Loft` 模式沿 Z 轴挤出生成（因此模型中任何非挤出元素都会变形），sleepers 用 `Repeat` 模式按固定间距重复；坡道另有 `EmbankmentLofterIngredients` 定义路基；端帽区分 `StraightCap` 与 `DiagonalCap`（45°）。[Creating a new track type mod - Voxel Tycoon Modding Documentation](https://docs.voxeltycoon.xyz/guides/content-mods/creating-a-new-track-type-mod)

**观察（社区来源）**：官方教程建议规划路线时尽量避开会拖慢列车的坡道，需要弯道时“建议采用大半径”；但同一篇明确说站场环线用急弯没关系，因为列车在那里本来就跑不快。[How to play: Your first train route（Fandom 社区 wiki）](https://voxeltycoon.fandom.com/wiki/How_to_play:_Your_first_train_route)

**观察（社区来源）**：Steam 创意工坊有一个订阅量约 7.7k 的 mod，唯一功能是把默认轨道高差拉长 4 倍，并允许把默认转弯半径按 2/3/4 档放大（作者称第 3 档对应半径 10）；作者说明这需要在代码层改变铁路建造逻辑，且只能用于新存档。[Smoothed rail curves（Steam 创意工坊，社区）](https://steamcommunity.com/sharedfiles/filedetails/?id=2396353093)

**结论**：Voxel Tycoon 与本项目的约束最接近（体素、格子、以列车运行观感为卖点），它给出的教训是负面的：把轨道形状写死在格子上的第一版撑不住后续的交叉、平交与多轨需求，最终必须整体重写。同时，“默认曲线和坡道太急”在玩家侧有明确的、可量化的不满。

**建议**：本项目的 Ports + InternalConnections（§44）已经绕开了 Voxel Tycoon 踩的第一个坑（形状枚举膨胀）；剩下要提前守住的是第二个坑——**渲染几何不要写死成“一格一段”**，而是从一开始就按 Section 沿路径放样，使得日后加大等效半径不需要改拓扑。

### Minecraft 原版：单格 90° 弯加单格 45° 坡的极端

**观察（社区维护的事实标准 wiki）**：`rail` 只有 10 种状态——2 种直轨（NS/EW）、4 种坡轨（向东/西/北/南爬升）、4 种弯轨（SE/SW/NW/NE）；形状完全由邻接自动推导，放置时按 west、east、south、north 的优先级选择；四向路口不会形成十字互通，中心格恒定弯成 south-east；相邻格高差为 1 时新放的轨道自动变成坡轨。[Rail - Minecraft Wiki](https://minecraft.wiki/w/Rail)

**观察**：矿车每个行进轴的速度上限是 8 格/秒，因此对角行进最高可达 11.314 格/秒；“矿车过弯时是沿对角线切过那一格”；上坡时减速明显快于平地；1.21.2 起矿车在轨道以坡道结尾时保留竖直动量并在空中相应倾斜。[Minecart - Minecraft Wiki](https://minecraft.wiki/w/Minecart)

**结论**：原版正是本项目首版拓扑的最近亲，而它能成立的三个前提本项目都不满足：车辆只有 1 格长、允许可见的切角、坡顶坡底完全不做竖曲线（1.21.2 才补上“空中倾斜”这种表现补偿）。GDD §42 列为不可接受的“车厢切角”“坡道姿态跳变”，恰好就是原版的既有行为。

**建议**：可以直接复用原版的两条设计：形状由邻接派生而不入存档（转录 Q106 已采纳），以及“对角线通过弯道格”的路径——但要把它从“矿车切角”升级为跨格样条，让长车厢有连续可跟随的曲线。

### Create（Minecraft 模组）：对齐轨加不可分割的平滑段

**观察（社区 wiki）**：轨道有 8 个朝向（4 个主方向加 45°），也可以贴在方块侧面形成 45° 竖直坡。辅助放置（placement assist）可以铺出“unaligned”轨道：这些轨道不对齐 8 个标准方向，**只能整组放置或整组拆除**。弯道只能是 45° 或 90°，必须从对齐轨开始、到对齐轨结束，最小（也是默认）半径 8 格，按住 CTRL 可以做更大半径。[Train Track - Create Wiki（Fandom，社区）](https://create.fandom.com/wiki/Train_Track)

**观察（社区 wiki）**：坡道有两种。45° 直坡可以任意高度与长度，可手工搭；但**从平地到 45° 的过渡必须用辅助放置完成，手工做出来的是急剧折角，列车不会识别**。另一种是 S 形竖曲线坡：起止都是水平轨，单段最大升 11 格、最大水平跨度 31 格，且每个高差都有最小水平距离（例如升 6 格至少要 16 格的水平距离），多段可首尾相接，接头处会自动变成非水平以平滑更长的坡。[Train Track - Create Wiki（社区）](https://create.fandom.com/wiki/Train_Track)

**观察（社区 wiki）**：列车由 bogey 决定姿态——每节车厢最多 2 个 bogey，车厢就是围绕两个 bogey 底座搭出的平台；整列车最多 20 个 bogey；列车不能在弯道或对角轨道上组装或拆解。[Train - Create Wiki（社区）](https://create.fandom.com/wiki/Train)

**结论**：Create 是“体素格子加真实铁路观感”这一组合里最直接的解法：格子只承载**对齐的**轨道单元，所有平滑（曲线、竖曲线）都以“不可单独编辑的一整段”存在，并强制要求过渡段由工具生成。它的车厢姿态就是两点式转向架。

**建议**：把 Create 的 aligned/unaligned 二分当作本项目“单格 Port 拓扑加跨格渲染样条”的现成心智模型：Port 拓扑等价于 aligned 轨；跨格平滑段等价于 unaligned 集合，由 RailConstructionPlan 整体生成、整体失效，绝不逐格编辑。

### Immersive Railroading（Minecraft 模组）：为了真实感彻底放弃格子形状

**观察**：轨道蓝图的形状有 Straight、Slope、Turn、Switch、Crossing、Turntable、Custom Curve；Turn 与 Switch 的转角按 22.5° 增量（把圆等分 16 份）；轨道相对路基的位置可选 Fixed（完全锁定）、Pixels（对齐到 1/16 格）、Pixels Locked、Smooth（自由）、Smooth Locked；轨距有 5 档（Model/Minecraft/Narrow/Standard/Brunnel）。[Track Blueprint - Immersive Railroading Wiki](https://teamopenindustry.gitlab.io/IRWiki/reference/tools/trackblueprint.html)

**观察**：1.5.0 起 Turn 与 Crossing 被 Custom Curve 取代并标为过时，官方说明 Custom Curve“更不挑剔、更灵活、更好用”；Custom Curve 可放置长达 1000 格的自由曲线，并且支持带坡度变化的曲线（即曲线坡）。[How to do Curved Track - Immersive Railroading Wiki](https://teamopenindustry.gitlab.io/IRWiki/guides/curvedtrack.html)

**结论**：在同一个体素世界里，越是追求真实铁路观感的实现，越会从“格子形状枚举”迁移到“自由曲线加亚格对齐”；连角度枚举（22.5° 增量）都被自由曲线淘汰了。

**建议**：不必在首版引入自由曲线，但本项目的 Path 表示应该允许 Segment 的端点带亚格偏移与切线（而不是硬编码“进出格心”），这样以后加缓和曲线不需要改动 RailPosition 的语义。

### OpenTTD：格内多轨位、坡度查表、按车节推导曲线限速

**观察（源码）**：一格最多容纳 6 个 track bit——X、Y 两条贯穿整格的直轨，加 Upper/Lower/Left/Right 四条半格转角轨；官方教程也说明对角轨“只占半格，因此一格里可以放两条对角轨”，同时明确“不能在山坡上铺对角轨”。[rail_cmd.cpp](https://github.com/OpenTTD/OpenTTD/blob/master/src/rail_cmd.cpp)、[Building tracks - OpenTTD Wiki](https://wiki.openttd.org/en/Manual/Building%20tracks)

**观察（源码）**：坡上能否铺轨是一张查表加地基决策。`_valid_tracks_without_foundation[slope]` 给出可以直接贴坡面的轨道组合；单条 X/Y 轨在“一角抬高”的坡上得到 `Foundation::InclinedX/InclinedY`（真正的坡轨）；否则要么用 `Foundation::Leveled` 把地块垫平，要么返回 `Foundation::Invalid` 并报错 `STR_ERROR_LAND_SLOPED_IN_WRONG_DIRECTION`。陡坡（对角两级高差）上只有单独的 X 或 Y 能拿到倾斜地基，其余组合走 `SteepLower`/`SteepBoth` 或直接非法；代码里还有一个专门的 `SpecialRailFoundation`，注释写作 “special anti-zig-zag foundation”，用来避免坡上单条转角轨出现锯齿。[rail_cmd.cpp `GetRailFoundation`](https://github.com/OpenTTD/OpenTTD/blob/master/src/rail_cmd.cpp)

**观察（源码）**：世界坐标里 `TILE_SIZE = 16`、`TILE_HEIGHT = 8`，即“一格升一级”等于 1:2（50%）的坡度。[tile_type.h](https://github.com/OpenTTD/OpenTTD/blob/master/src/tile_type.h)

**观察（源码）**：`Train::GetCurveSpeedLimit()` 不看轨道几何，而是遍历列车链表中**相邻两节车的方向差**：每出现一次 45° 差就记一次弯，若两次弯之间的距离不超过一个车辆长度则把上限压到 88；出现 90° 方向差直接锁 61；恰好一次左弯加一次右弯（S 弯）不限速；其余情况按弯与弯的平均间距 `sum` 计算 `232 - (13 - clamp(sum,1,12))²`，随后再叠加轨道类型加成与摆式列车 20% 加成。[train_cmd.cpp](https://github.com/OpenTTD/OpenTTD/blob/master/src/train_cmd.cpp)

**观察（源码）**：坡度阻力按车节累加——`GetSlopeResistance()` 遍历每一节车，标记为 GoingUp 的加、GoingDown 的减，因此长列车跨过坡顶时上下坡阻力自然抵消。[ground_vehicle.hpp](https://github.com/OpenTTD/OpenTTD/blob/master/src/ground_vehicle.hpp)

**观察（官方 wiki）**：单个 45° 连接列车通过时不减速，但同时压在多个 45° 连接上就会减速；90° 连接每次都明显减速，并且有一项高级设置可以彻底禁止列车与船只做 90° 转弯（开启后列车会像到达线路尽头一样停车折返）。[Building tracks - OpenTTD Wiki](https://wiki.openttd.org/en/Manual/Building%20tracks)

**结论**：OpenTTD 提供了两个可直接搬运的机制：其一，“坡度 → 合法轨道组合 → 需要什么地基”的查表式建造校验，正好对应本项目 §49 的 Plan Validate 与生成器整地；其二，用**多接触点之间的姿态差**反推曲线严苛程度，本项目的 Front/Rear Contact 天然能提供同样的输入。

**建议**：把“哪些 Port 组合允许出现在有高差的格上”写成一张显式表（而不是散落的判断），并在生成器与未来的玩家铺轨里共用；曲线限速即使首版不做，也应保证姿态计算能给出相邻接触点的方向差。

### Transport Fever 2：自由样条的对照组

**观察**：轨道由 `res/config/track/*.lua` 定义。半径与坡度参数为：`minCurveRadiusBuild = 60.0`（自由拖动时的最小半径，米）、`minCurveRadius = 44.0`（吸附与并行铺设时的最小半径）；`maxSlopeBuild = 0.075`、`maxSlope = maxSlopeBuild * 1.6`（12%）、`maxSlopeShape = maxSlope * 1.25`（15%），三者分别是“起终点直线的坡度上限”“对齐两端坡度的轨道曲线上限”“被交叉道路或轨道挤压后实际轨道的上限”。[Tracks and Streets - Transport Fever 2 Wiki（模组文档）](https://wiki.transportfever2.com/doku.php?id=modding:tracksstreets)

**观察**：直线段限速为 `speedLimit`，弯道限速按 `a * (radius + b) ^ c` 计算，默认 `speedCoeffs = {0.9, 15.0, 0.63}`。程序化几何由 `shapeWidth = 4.0`、`shapeStep = 4.0`（米）控制：步长越小曲线越平滑但图元越多、碰撞检查越慢，文档明确提示“允许小半径且轨道较窄的类型应该用更小的 shapeStep，以免轨道出现折角”。[Tracks and Streets（模组文档）](https://wiki.transportfever2.com/doku.php?id=modding:tracksstreets)

**观察**：游戏手册说明轨道建造没有道路那样的“直线/曲线”两种模式，工具总是用圆弧安排轨道以避免与既有段之间出现尖折；曲线半径直接以限速数字实时显示在铺设预览上；轨道不能像道路那样陡，并且有最小曲线半径。[Streets and Tracks - Transport Fever 2 Wiki（游戏手册）](https://wiki.transportfever2.com/doku.php?id=gamemanual:streetstracks)

**结论**：完全自由样条的做法把所有约束移到了“建造期参数加实时反馈”上：最小半径、最大坡度是数字而非形状，玩家看到的是限速数字而不是“不能建”。它也确认了一个渲染层事实——曲线平滑度由固定步长的细分决定，半径越小需要的步长越细。

**建议**：TrackRenderSection 的细分步长应当是一个可调常量（对标 `shapeStep`），并且在等效半径较小的地方允许加密，而不是固定“一格一段”。

### 横向对比

| 方案 | 最小弯（角度 / 占格） | 对角与半格概念 | 坡道跨度与坡度 | 车辆姿态 | 坡顶坡底竖曲线 |
| --- | --- | --- | --- | --- | --- |
| Factorio 2.0 | 半径 13 tile，180° 掉头占 28 tile 宽 | 对角 45° 加半对角 22.5°，轨道落在 2 格网格 | 坡道为 16×4 tile 的整体实体，一次完成层间过渡 | 两个 joint（间距 4）加车间距 3，轮子按 joint 绘制 | 封装在坡道实体内，且不模拟坡度阻力 |
| Voxel Tycoon | 官方未公布数值；官方教程建议大半径 | 直线端帽与 45° 对角端帽 | 官方未公布；社区 mod 把默认高差拉长 4 倍 | 未查到官方描述 | 未查到官方描述 |
| Minecraft 原版 | 单格 90°（10 种方块状态） | 无对角轨，对角靠弯轨交替形成阶梯 | 单格 45°（1:1） | 单点实体，过弯时沿对角切过该格 | 无；1.21.2 才加入空中倾斜表现 |
| Create（模组） | 45°/90°，最小半径 8 格 | 8 个朝向（含 45°），另有不可分割的 unaligned 平滑段 | 45° 直坡任意长；S 形竖曲线坡最大升 11 格、跨 31 格，每个高差有最小跨度 | 每节车厢最多 2 个 bogey，车体挂在两 bogey 之间 | 强制：平→45° 过渡必须由工具生成，手工折角列车不认 |
| Immersive Railroading（模组） | Turn 按 22.5° 增量；Custom Curve 自由，最长 1000 格 | 轨道位置可对齐到 1/16 格或完全自由 | Slope 类型加 Custom Curve 支持曲线坡 | 真实转向架，1:1 尺度，多种轨距 | 由自由曲线自身承担 |
| OpenTTD | 一格内 90°（两条半格转角轨）；90° 转弯锁速 61，可设置禁止 | 对角轨只占半格，一格可并放两条 | 一格升一级等于 1:2（50%）；坡上轨道组合查表，不合法需地基或禁止 | 每节车独立位置与方向；限速由相邻车节方向差推导 | 无显式竖曲线；每节车各带 GoingUp/GoingDown，跨坡顶自然抵消 |
| Transport Fever 2（对照） | 最小半径 60 m（吸附 44 m），半径直接换算限速 | 无格子概念，自由样条 | maxSlopeBuild 7.5%，引擎允许 12%/15% | 样条跟随加转向架 | 由样条的高程曲率参数控制，可手动调 |

### 对单格正交 Port 拓扑的可用启发（候选，不做决定）

1. **拓扑与几何分层，先例一致。** Port + InternalConnections 只负责“能不能连、允许怎么走”，可见几何交给沿路径放样的 Section。Create 的 aligned/unaligned、TF2 的 shapeStep、Voxel Tycoon 的 loft/repeat 都是同一件事的不同实现。
2. **跨格样条平滑。** 单格 N↔E 在拓扑上是 90°，但渲染与 RailPosition 的路径可以由**相邻若干格**共同拟合：进出格的切线取自相邻格的 Port 方向，中间用一条跨越 2–3 格的曲线。等效半径因此不再受限于单格。
3. **阶梯拼 45°。** N↔E 与 W↔S 交替形成阶梯，平滑后等效于一条 45° 对角线，等效半径远大于单格弯；这正是 Minecraft 玩家事实上的对角铺法与 Factorio 半对角想解决的同一个问题。可以把“阶梯”识别为一种可平滑的模式（模式识别发生在渲染与路径生成阶段），而不是新增 Port 类型。
4. **连接点落格、控制点合法性。** 借用 Factorio 的铁律：Port 连接点严格落在格边界；若某个平滑方案要求控制点落在无法定义的位置，改的是等效半径而不是加特例件。
5. **接触点间距与等效半径的比例是首版最该量的数。** 先例给的参考是 3 倍左右（Factorio 13:4）。原型里应当同时试“缩短车厢与接触点间距”和“加大等效半径”两条路，看哪条先让切角消失。
6. **坡顶坡底的竖曲线可以由两接触点姿态免费获得一半。** 车体姿态取 Front/Rear Contact 两点连线，跨坡顶时姿态自然是两段坡的平均，不会瞬跳；剩下的是路径本身在坡顶处切线不连续的问题，可以在入坡与出坡格内做一段短竖曲线（对标 Create 强制的过渡段）。
7. **1:1 坡的定位要明确。** 100% 坡度是 OpenTTD 的两倍、TF2 建造上限的十几倍。若首版保留，宜按 Minecraft 的定位理解（短车、允许夸张），并在原型里同时评估“1:2 缓坡由多格坡道 Plan 生成”的成本——多格坡道在 Factorio 是一个整体实体，不需要给 Port 增加高度组合。
8. **建造期校验用查表。** OpenTTD 的“坡度 → 合法轨道组合 → 地基类型”三段式，对应本项目 §49 的 Validate 与 §50 的净空与整地；把“有高差的格上允许哪些 Port 组合”写成显式表，生成器与未来玩家铺轨共用。
9. **几何严苛度可以转成速度而不是禁止。** OpenTTD 用相邻车节方向差、TF2 用 `a*(r+b)^c`，都把急弯变成限速而非非法。本项目首版即使不做限速，也应让姿态计算暴露出“相邻接触点方向差”这一输入。
10. **Section 切分与细分步长。** Factorio 每 16 格一个高架支撑、TF2 每 4 m 一段几何，说明固定步长的分段是常规做法；§60 的 TrackRenderSection 可以直接采用固定步长加 Chunk 或控制点边界的组合，并允许小半径处加密。

### 未能核实的事实

- Factorio 高架轨道相对地面的**具体高度**（多少 tile）没有在官方 wiki 或 FFF 中找到明确数值。
- Factorio 1.x 旧曲线轨道的占格尺寸无法引用：官方 wiki 的 `Curved rail` 页面已被删除（当前为空页）；只有 FFF-377 提到旧曲线端部比自然圆弧尖锐，以及新旧半径 11→13 的关系。
- Voxel Tycoon 的默认最小弯道半径、坡道跨格数与最大坡度**没有官方数值来源**；本文只能引用官方 devlog 的定性描述、官方 modding 文档的几何生成方式，以及社区 mod 的间接说法（把默认高差拉长 4 倍、第 3 档半径为 10）。
- Voxel Tycoon 车辆在弯道与坡道上的姿态实现（是否多接触点）未找到官方描述。
- Minecraft Wiki（minecraft.wiki）为社区维护，但其方块状态表与游戏数据一致，本文按事实标准引用并已标注来源性质；Create Wiki 与 Voxel Tycoon Fandom Wiki 同样为社区来源。
