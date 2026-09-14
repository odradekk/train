# ChatGPT 分享对话转录：设计资源系统

- 来源：https://chatgpt.com/share/6aa7b904-8ab8-83e8-bd87-338d8c7019e7
- 模型：gpt-5-6-thinking
- 对话创建/更新：2026-09-14 09:06:12 UTC / 2026-09-14 09:06:16 UTC
- 说明：由页面内嵌的 turbo-stream 数据还原，共 282 个节点。系统消息、隐藏的上下文节点、已被隐藏的工具输出（"The output of this plugin was redacted."）
  以及形如 "思考了 2m 11s" 的耗时提示已省略；助手的思考摘要以 "> [思考摘要]" 形式保留，工具调用以 "> [工具调用 → 目标]" 加代码块形式保留，
  未被隐藏的工具输出以 "> [工具输出]" 加代码块形式保留。正文文本未作修改。


---

## 1. User  (2026-09-10 06:28:26 UTC)

---
name: grilling
description: Grill the user relentlessly about a plan, decision, or idea. Use when the user wants to stress-test their thinking, or uses any 'grill' trigger phrases.
---

Interview the user relentlessly until you reach a shared understanding. Map this as a **design tree**: every decision branches into the decisions that hang off it.

Work the tree in **rounds**. The **frontier** is every decision whose prerequisites are already settled: the questions you can ask _now_ without guessing at answers you haven't heard yet. Ask the whole frontier in one round: number each question and give your recommended answer. Then wait for the user's answers before the next round.

Format a round like so:

```
❓ **Q1** - **<question title>**: <question body, might be multiple paragraphs, including multiple choices>

➡️ <your recommended answer>

---

❓ **Q2** - **<question title>**: <question body, might be multiple paragraphs, including multiple choices>

➡️ <your recommended answer>
```

Each round the user answers reshapes the tree: settled decisions push the frontier outward and unblock questions that depended on them. Recompute the frontier and ask the next round. A question whose answer depends on another question still open in this round belongs to a _later_ round, not this one.

Finding _facts_ is your job, never the user's. When a frontier question needs a fact from the environment (filesystem, tools, etc.), dispatch a sub-agent to find it; don't ask the user for anything you could look up yourself. Don't block on it: a running exploration is an unsettled prerequisite, so only the questions downstream of it wait for the sub-agent to report; ask the rest of the frontier now. The _decisions_ are the user's: put each to them and wait.

The session is done when the frontier is empty: every branch of the design tree visited, nothing left silently assumed. Do not act on it until the user confirms you have reached a shared understanding.


体素的设计已经完成，我也根据我们的讨论完成了正式版的设计（可看参考文档，项目的github内也有对应文档）当前的游戏的体素系统正在开发中，让我们继续游戏设计任务。接下来我希望对整个游戏的资源系统进行详细的设计，和之前一样我需要你和我一起完成初稿。下面是我的初步构想：

我计划设计一种多级的树状资源系统

## **自然资源**
首先所有产物的源头都是自然资源，高级产物只是对自然资源的再加工。因此我们需要先设计出几类最基础的自然资源，而之后所有的产物的源头都来自于它们（这种资源结构十分常见，我的世界，Factorio都有类似的设计）

## **生产和加工**
在完成自然资源的设计后，我们需要在自然资源的基础上设计一个树状（或网状但有序关系）的多级结构，每一级的物品都会参与到市场中去。可以通过冶炼，加工，组合等各种操作（也需设计）生成各种各样的东西。产物涵盖这个世界的方方面面，从食材，菜品，到衣物，到各类零部件（但注意设计时应当是宏观的，抽象的例如没有必要设计苹果，香蕉这样的粒度，一个水果就足够了。）

## **顶层产物**
之前设计的资源都是宏观的，抽象的，而有一部分顶层产物比较特殊，它们是玩家可以使用上述资源生产后用于提升自身能力的，包括：列车部件，列车武器，乘员武器，列车设备，乘员设备。

## **复合产物**
复合产物和顶层产物类似，但复合产物不是体素，之前的资源和产物都是体素，而复合产物是复合体，例如玩家用于自己的聚居点时使用各类资源和产物建造的建筑，玩家生产自己的列车，都是消耗资源直接产生一个复合体。

以上还是一个非常粗略的构想，需要我们细化并设计出良好的结构，合适的抽象粒度，正确的关系。让我一步一步来完善

---

## 2. Assistant  (2026-09-10 06:28:40 UTC)

> [思考摘要] 梳理设计边界：我先核对现有设计与实现，避免重复询问已确定内容。第一轮会覆盖资源系统的根本边界：资源是什么、如何存在、如何转化、如何进入经济循环，以及哪些范围明确不纳入。 / 梳理了设计边界

我会把这次设计拆成一棵从**资源语义 → 生产网络 → 经济参与 → 顶层装备 → Composite 制造**逐层展开的设计树。第一轮只问最上游、会决定后续所有分支的基础问题；现有文档已经确定的体素、Definition、Price/Value、Inventory/Instance 等约束不会重新询问。 

> [工具调用 → web.run]

> [工具调用 → api_tool.list_resources]

```
{"paths":["GitHub"],"query":"search"}
```

> [思考摘要] 已搜索 1 个网站

> [工具调用 → api_tool.call_tool]

> [工具调用 → api_tool.call_tool]

> [工具调用 → api_tool.call_tool]

> [工具调用 → api_tool.call_tool]

> [工具调用 → api_tool.call_tool]

> [思考摘要] 确定资源本体：我正在锁定最基础的定义：资源是否始终具有数量、质量与位置；资源图是树、无环网络还是允许循环；以及拆分资源类型的最低标准，是物理差异还是经济决策差异。 / 确定资源本体

> [工具调用 → api_tool.call_tool]

> [工具调用 → api_tool.call_tool]

> [思考摘要] 检查 GitHub 仓库及体素自然资源实现，确认定价生产规则

我已经对照了当前正式设计文档和仓库 `master`。现有架构已经把几个会直接约束资源系统的前提钉死了：货物不再拥有独立 Definition，而统一引用 `VoxelDefinitionId`；`natural_resource` 已经是正式 Tag；`Value` 是定义上的稳定基础价值，`Price` 属于运行时 Market Context；库存中的数量不是空间实例。fileciteturn0file0L19-L27 fileciteturn0file0L35-L38 当前仓库实现也已经落到了 `VoxelDefinition` 这一方向。fileciteturn5file0L2-L5

同时，Composite 已经被定义为“多个 Voxel 构成的独立对象”，且禁止递归嵌套；Tag / Behavior / Capability 也已经严格分层。资源系统应当建立在这些现有不变量之上，而不是再造一套 Item/Good 类型体系。fileciteturn0file5L21-L29 fileciteturn0file5L97-L108

我把目前的设计树根部整理成：

```text
资源系统
├─ A. Gameplay 职责
├─ B. 系统边界：什么算“资源/产物”
├─ C. 谁进行生产
├─ D. 生产关系的拓扑结构
├─ E. 物品抽象粒度原则
├─ F. 顶层产物的领域地位
├─ G. Composite 与资源网络的边界
└─ H. 长期资源循环与枯竭模型
    ↓
    [下一层才会展开]
    自然资源分类
    → 加工层级
    → Process / Recipe 模型
    → 能源 / 时间 / 劳动力
    → 副产品 / 废料 / 回收
    → 产业设施
    → 市场供需
    → 装备体系
    → Composite 制造
```

因此第一轮 Frontier 是下面八个问题。具体有哪些矿物、食材、零件等，现在还不能问；它们依赖这一轮的答案。

---

❓ **Q180** - **资源系统究竟服务于什么 Gameplay**：资源生产系统本身要做到什么深度？

一种方向是接近 Factorio：生产布局、机器连接、吞吐量和流水线本身就是核心玩法。另一种方向是更宏观：生产链存在并真实运行，但它的主要 Gameplay 作用是制造**地域分工、供需差异、货物流和贸易机会**，玩家主要解决的是列车运输、贸易策略、产业配置和装备制造，而不是手动优化传送带。

这会决定后面我们到底是在设计一个“生产游戏 + 火车”，还是“火车贸易经营游戏中的生产经济”。

➡️ **我推荐后者。** 生产链应该足够真实、完整，可以产生复杂经济关系，但不要把游戏变成 Factorio。当前已经确定的核心循环仍然是“配置贸易方案 → 自动运行 → 阅读报告 → 策略迭代”，生产系统应该强化这个循环，而不是取代它。fileciteturn0file4L34-L39

---

❓ **Q181** - **“所有产物都来自自然资源”的边界**：这里的“资源系统”是否只描述**物质性商品**？

例如：

`铁矿 → 金属 → 机械零件 → 引擎`

显然属于资源网络。

但生产还可能需要电力、热量、劳动力、生产时间、机器产能、金钱等。我们需要决定这些东西是不是也成为资源树里的“物品”。

➡️ **我推荐把资源树严格限定为“物质性、可库存、原则上可实体化的商品”。**

因此燃料是一种 Voxel 商品；但燃烧燃料产生的“能量”不是一种 Voxel。电力、热量、劳动力、时间、设备产能等属于 **Production Factor / Constraint**，货币属于经济记账，它们可以约束 Process，但不进入物质资源树。

这样可以维持一个很重要的原则：

```text
Natural Resource
      ↓
Physical Goods Network
      ↓
Voxel Products

而：

Energy / Labor / Time / Capacity
          ↓
     constrain Process
```

不会出现“10 个电力 Voxel + 3 个劳动力 Voxel制造发动机”这种抽象污染。

---

❓ **Q182** - **玩家与聚居地是否使用同一套生产规则**：NPC 聚居地的工业生产和玩家自己的生产，是不是同一个系统？

例如 `Metal Ore → Metal`，我们可以让 NPC 城市有一套简化经济公式，而玩家工厂使用另一套真实 Recipe；也可以让双方都调用相同的 ProcessDefinition，只是生产能力和设施不同。

➡️ **我强烈推荐统一。**

同一个：

```text
ProcessDefinition
Inputs
Outputs
Requirements
```

既可以被聚居地经济系统使用，也可以被玩家设施使用。

NPC 不应该“凭空每分钟 +100 Steel”，而玩家却必须遵守另一套炼钢规则。差异应该来自设施、产能、效率、当地资源、经济策略等，而不是两套物理规律。

这与现有“World Model 是事实来源、避免重复维护权威状态”的整体设计原则也一致。fileciteturn0file8L63-L67

---

❓ **Q183** - **资源结构是 Tree 还是 DAG**：你最初称它为“多级树状资源系统”，但严格的 Tree 会带来很强的限制。

例如一个机械零件可能需要：

```text
Metal ─────┐
           ├→ Mechanical Parts
Polymer ───┘
```

而高级设备又可能同时使用电子零件、机械零件、化工材料。这样天然就已经不是树，而是有向图。

另一个问题是是否允许：

```text
A → B → C → A
```

这样的生产循环。

➡️ **我推荐核心生产网络采用有向无环图 DAG，而不是 Tree。**

允许多输入、多输出、不同分支汇合，也允许某些工艺跨“层级”；但**主生产关系禁止循环**。

以后如果做拆解、回收、维修，可以建立显式的 Recycling / Recovery 反向关系，而不是让主生产图本身充满环。

这样我们仍然拥有明确的“低加工度 → 高加工度”方向，同时不会被树结构束缚。

---

❓ **Q184** - **物品抽象粒度的唯一判定原则是什么**：你已经明确不希望出现“苹果、香蕉、橙子”这种无意义粒度，那么我们需要一个以后可以反复使用的判定标准，否则内容一多仍然会失控。

例如到底应该有：

`Iron Ore / Copper Ore / Aluminum Ore`

还是只有：

`Metal Ore`

同样，应该有棉花和羊毛，还是只有 Fiber？应该有牛肉、猪肉、鸡肉，还是只有 Meat？

➡️ **我推荐采用“Gameplay Difference Test”作为硬规则：只有拆分后能产生新的有效决策，才拆成不同 VoxelDefinition。**

也就是说，如果两个东西在**资源地域分布、加工路线、运输属性、市场需求或最终功能**上都没有值得玩家关心的差别，就合并。

所以：

```text
Apple + Banana + Orange
→ Fruit
```

几乎肯定正确。

但：

```text
Iron / Copper
```

是否合并则不能现在拍脑袋决定——如果以后一个主要进入结构机械，一个主要进入电气设备，并形成不同地域贸易，那么拆开就有 Gameplay 意义。

我们下一轮可以基于这个原则真正设计自然资源集合。

---

❓ **Q185** - **“顶层产物”是不是一种特殊领域类型**：你提出的列车部件、列车武器、乘员武器、列车设备、乘员设备很重要，但它们究竟应该成为资源系统中特殊的 `TopLevelProductDefinition`，还是依然只是普通 VoxelDefinition？

➡️ **我推荐后者：不要创建特殊的“顶层产物”基础类型。**

它们仍然只是生产 DAG 中的 VoxelDefinition：

```text
Metal
 + Mechanical Parts
 + Electronic Parts
        ↓
    Train Engine
```

它之所以是“引擎”，不是因为继承了 `TopLevelProduct`，而是因为它组合了相应 Tag / Behavior，并因此提供 Capability。

这正好吻合现在已经确定的架构：Tag 负责分类，Behavior 负责机制，Capability 是行为产生的能力。fileciteturn0file5L97-L108

因此“顶层产物”可以保留为**设计和 UI 上的内容分类**，但不要成为新的领域基础抽象。

---

❓ **Q186** - **Composite 是否终止商品生产链**：资源和顶层设备最终会被用于建造列车、车厢、建筑等 Composite。那么 Composite 本身要不要继续成为普通“货物”？

例如制造一台机车：

```text
Structural Parts
Engine
Control Equipment
...
        ↓
Locomotive Composite
```

制造完成后，是得到“1 个 Locomotive 商品”放进库存，之后再实体化；还是直接产生 CompositeInstance？

➡️ **我推荐 Composite Construction 是普通商品网络的终点，直接创建 Composite。**

也就是：

```text
Voxel Goods / Components
        ↓
Construction / Manufacturing
        ↓
CompositeInstance
```

而不是：

```text
Voxel Goods
→ Locomotive Item
→ Locomotive Composite
```

这样不会引入一个尴尬的“整栋楼/整列车到底占几个 Voxel 的库存物品”中间态。

如果以后希望买卖整台机车或整栋设施，可以单独设计 **Asset / Vehicle Market**；它交易的是 Composite 所有权，而不是把 Composite 强行塞回 Cargo Stack。

这也符合当前已经确定的规则：Composite Creation 必须来自显式 Gameplay 行为，而且 Composite 直接拥有 Voxel，不存在递归 Composite Tree。fileciteturn0file5L11-L17 fileciteturn0file5L23-L29

---

❓ **Q187** - **资源经济是否必须能够长期持续运行**：世界是有限的，而自然资源又是真实存在于世界中的 Voxel；开采后实际资源会减少，聚居地周边资源种类也会随之变化。fileciteturn0file9L44-L53

那么长期目标有两个完全不同的方向：

**A. 有限资源世界。** 矿最终会采光，某些产业永久消失，整个世界经济理论上最终可以耗尽。

**B. 长期可持续世界。** 局部资源可以枯竭，从而改变贸易格局，但世界经济整体不会因为运行足够久而进入不可逆的“所有东西都生产不了了”。

➡️ **我推荐 B，而且强调“局部枯竭，整体可持续”。**

我不建议因此让所有矿脉简单自动刷新。更好的方向是以后组合几种机制：部分自然资源可再生；不可再生资源真实枯竭；废品和旧设备可以部分回收；必要时再设计极长期的外部输入机制。

这样“某矿业城镇的矿采完了”可以成为很好的经济事件，迫使产业和贸易路线迁移；但不会导致一个运行了很久的桌面存档最终数学意义上必然死亡。

---

这一轮先只决定 **Q180–Q187**。你可以直接逐项回答，例如 `Q180 同意；Q181……`。你的答案落定后，我会重新计算 design tree 的 frontier；下一轮大概率才会真正进入**自然资源应该有哪些基础类别**以及**资源 DAG 应如何分层**。

---

## 3. User  (2026-09-10 06:49:22 UTC)

Q180 没错是后者，一个精致的生产链主要是为了之后核心的经济玩法打好基础，整个生产链的设计就是为了让玩家能够有更多贸易策略上的选择，同时玩家也可以通过收集资源来发展和建设自己的聚居点和列车。（当然直接购买也是可以的，但给了玩家自己低价收购原料自己建设的选择空间）

Q181 没问题，或者更严谨一些，一切可交易的实物都在资源树中。即资源树中的东西一定是物质性、可库存的；同时也一点是可交易的。一个更详细地例子是：无人机就可以是资源树中的一个产物，因为其满足了物质性、可库存，且是可交易的。但是乘员就是一个反例：一个乘员是物质性、可库存的，但你不能交易一个乘员（人口买卖），你只能雇佣或辞退一个乘员。

Q182 使用统一的系统

Q183 同意无环DAG，DAG的表达更加丰富且符合当前的需求（即我说的有序关系的图）

Q184 同意基于gameplay进行拆分的原则

Q185 同意，没有必要为此单独抽象出一层领域模型

Q186 同意，之后可以专门设计复合体所有权转换的交易

Q187 同意，长期可持续运行对放置游戏十分重要，同时我认为虽然不能采用简单粗暴的让矿物刷新的方案，但是可以使用一个取巧的方案：即当某处的资源彻底耗尽的时候，在世界的某处尚未开发的区域随即刷新资源等待AI和玩家去探勘发掘

---

## 4. Assistant  (2026-09-10 06:49:35 UTC)

> [思考摘要] 梳理下一轮决策：我正在重新计算当前前沿：资源分类、分层加工图、生产过程表达，以及可再生资源的生成与迁移机制。基础边界已大致稳定，接下来应优先解决会影响数据结构的选择。 / 梳理了下一轮决策方案

> [思考摘要] уточнением границ ресурсов：我正在发现一个关键歧义：复合品既属于可交易实物，又可能只是加工图中的产物。下一步需要先明确液体、气体与矿藏的表示方式，以及“资源节点”究竟指储藏源还是可交易商品。 / Уточнил границы ресурсов

这一轮的答案把资源系统的根结构基本钉死了，但也暴露出一个需要马上澄清的边界：你把“所有可交易实物都在资源树中”作为定义，而 Composite 又可以被交易但不应成为普通库存货物。这个冲突必须先显式解决，否则后面的商品分类会反复摇摆。

另外，你对长期资源循环的补充我先记为方向：**资源点局部永久耗尽，但世界通过在尚未开发区域生成新的可勘探资源，维持长期总供给**。这一机制的触发、可预测性和是否算“刷新”可以等资源分布模型确定后再细化。

先进入下一轮。当前已确定的原则与正式文档兼容：自然资源目前是带 `natural_resource` Tag 的 VoxelDefinition；库存中的数量不是空间实例；Composite 则是独立对象。fileciteturn0file0L19-L27 fileciteturn0file0L35-L38 fileciteturn0file5L21-L29

这一轮先解决**“资源网络到底由什么节点和关系组成”**。自然资源具体有哪些，要等这些基础语义确定后才能进入。

---

❓ **Q188** - **“资源树包含所有可交易实物”与 Composite 的边界**：这里存在一个轻微但实质性的冲突。

你刚才定义：

> 一切可交易的实物都在资源树中。

但我们同时已经确定：

```text
资源 / 部件
    ↓
制造
    ↓
CompositeInstance
```

列车、建筑之类 Composite 未来也可以进行所有权交易，只不过不是 Cargo Stack。这样严格来说它们也是“可交易实物”。

我们需要决定“资源 DAG”的正式边界究竟是：

**A. 所有可交易实物。** 那么 Composite 也属于同一个生产 DAG，只是在存储和交易机制上特殊。

**B. 所有可作为标准化库存货物进行交易的实物。** Composite 不属于资源 DAG；它进入独立的 Asset / Composite Economy。

➡️ **我推荐 B，并稍微修正 Q181 的定义：**

> **资源网络包含所有标准化、可计数库存、可作为普通货物交易的物质性对象。**

因此：

```text
Metal Ore       ✓
Food            ✓
Mechanical Part ✓
Rifle           ✓
Drone           ✓  （如果它是单体库存设备）

Crew            ✗  Employment
Building        ✗  Composite Asset
Locomotive      ✗  Composite Asset
Money           ✗  Accounting
Electricity     ✗  Production Factor
```

这样“交易”本身不是唯一边界，**标准化库存货物**才是。

---

❓ **Q189** - **自然资源究竟是“自然资源源”还是“开采出来的原料”**：目前文档把带 `natural_resource` Tag 的世界 Voxel 视为自然资源，聚居地也通过周围实际存在的这些 Voxel 推导资源种类。fileciteturn0file9L44-L53

但进入完整生产系统后，会遇到一个重要语义问题。

例如：

```text
地下油田 → 原油
森林 → 木材
农田/野生植被 → 农作物
矿床 → 金属矿
```

这里左边是**空间中的自然资源源 / Deposit**，右边才是**可以装车交易的 Raw Good**。

我们有两个选择：

**A. 两者尽量视为同一种 VoxelDefinition。**

例如世界里的一格 `Metal Ore` 被开采后，直接变成库存中的一个 `Metal Ore`。

优点是极简。

**B. 明确区分 Resource Source 与 Raw Good。**

例如：

```text
Metal Deposit --Mining--> Metal Ore
Forest Resource --Logging--> Timber
Oil Deposit --Extraction--> Crude Oil
```

Resource Source 是世界中的可开采事实；Raw Good 才正式进入商品 DAG。

➡️ **我推荐 B。**

矿石这种东西表面上可以做到“一格矿石 = 一单位货物”，但油田、森林、农业、地下水等很快就会破坏这个等价关系。

我建议以后形成：

```text
Natural Resource Source
        │ extraction
        ▼
Raw Commodity
        │ processing
        ▼
Intermediate Goods
        │ manufacturing
        ▼
Finished Goods
```

而现有的 `natural_resource` Tag 是否继续标记 Source、还是 Raw Good、或者拆成两个 Tag，等这个决定落定后下一轮再处理。

---

❓ **Q190** - **生产关系是否成为一等领域对象**：是否把“加工行为”本身定义成显式对象，而不是简单写成：

```text
Iron Ore → Metal
```

也就是采用类似：

```text
ProductionProcessDefinition
├─ Inputs
├─ Outputs
├─ Requirements
├─ Duration / Work
└─ ...
```

生产网络实际成为一个二部图：

```text
Good → Process → Good → Process → Good
```

而不是 Good 之间直接连边。

➡️ **我推荐明确采用一等 `ProductionProcessDefinition`。**

原因不是为了架构漂亮，而是后面一定需要表达：

- 多输入；
- 多输出；
- 不同生产路线；
- 所需设施；
- 能源；
- 时间；
- 效率；
- 副产品。

简单的 `A → B` Edge 很快就会被迫塞入大量特殊字段。

---

❓ **Q191** - **是否存在严格的“一级、二级、三级产品”**：虽然我们可以直观地说：

```text
自然资源
→ 初级加工品
→ 中间材料
→ 零部件
→ 成品
```

但 DAG 一旦允许多种路线，同一个物品的“等级”可能并不唯一。

例如某种材料：

```text
Raw A → Material X

Raw B → Intermediate B → Material X
```

那么 `Material X` 到底是一级还是二级？

➡️ **我推荐不要把 Tier 作为生产合法性的硬领域结构。**

真正的事实只有 DAG。

可以保留用于 UI、内容组织和平衡的宏观分类，例如：

```text
Raw
Processed
Material
Component
Finished
```

甚至计算一个 `ProcessingDepth` 用于分析，但不能要求：

> Tier 2 只能由 Tier 1 制造。

这样不会让内容设计被人为层级卡死。

---

❓ **Q192** - **同一种产物是否允许多条生产路线**：例如某种 Fuel 可以：

```text
Crude Oil → Fuel
Biomass → Biofuel
Synthetic Feedstock → Fuel
```

甚至同一种 Metal 也可能有高效工业路线和低效率简易路线。

➡️ **我推荐允许。**

这是非常重要的贸易策略来源之一。

玩家面对的不应该永远是唯一链条：

```text
A → B → C
```

而可以面对：

```text
       A ──Process 1──┐
                      ▼
                      C
                      ▲
B + D ──Process 2─────┘
```

不同路线可以在原料成本、设备要求、能耗、速度、副产品等方面形成差异。

但我建议**不要做基于 Tag 的任意自动替代配方**，例如“任何 metal × 2 都能造 engine”。替代路线应当是明确设计的 Process，否则非常容易产生无法平衡的组合爆炸。

---

❓ **Q193** - **生产过程是否允许多输出和副产品**：例如：

```text
Crude Oil
   ↓ Refining
Fuel + Chemical Feedstock
```

或者：

```text
Ore
 ↓ Smelting
Metal + Slag
```

如果只允许单输出，很多工业链会非常干净，但经济网络也更线性。

➡️ **我推荐模型上从一开始允许 `N inputs → M outputs`，内容上克制使用副产品。**

这样以后化工、食品加工、冶炼和回收都可以自然表达。

但不是每一个 Process 都需要副产品。否则玩家会陷入处理大量边角库存的微操，反而伤害你的宏观贸易玩法。

---

❓ **Q194** - **货物数量采用什么基本单位**：现在库存模型本身已经是：

```text
VoxelDefinitionId + Quantity
```

而且 Quantity 表示可实体化单位。fileciteturn0file0L35-L38

那么生产系统是否继续坚持所有货物都是**整数单位**：

```text
3 Metal Ore
→
2 Metal
```

而不是：

```text
2.73 t Ore
→
1.41 t Metal
```

➡️ **我推荐所有标准货物库存继续使用整数 Quantity。**

这里的“1 单位”是一个**抽象标准货运单位**，不一定等于一个现实物件，也不一定等于 1 kg。

例如：

```text
1 Metal Ore
Mass = 8

1 Fruit
Mass = 2

1 Mechanical Component
Mass = 1
```

质量仍然由现有 `VoxelDefinition.Mass` 表达。

这样库存、生产、贸易、装卸、存档和确定性模拟都会简单很多；同时不会因为采用宏观资源粒度而被现实单位制绑架。

---

❓ **Q195** - **DAG 是否只描述“净生产”，还是也纳入拆解和回收**：Q183 已经确定主生产网络无环，但长期可持续显然会需要回收。

例如：

```text
Mechanical Parts
      ↓
     Engine
```

而报废 Engine 又可能：

```text
Engine
  ↓ Recycling
Scrap Metal + Components
```

如果全部放在一个图里，它很容易形成：

```text
Metal → Engine → Scrap → Metal
```

整个图就不再是 DAG。

➡️ **我推荐明确分成两个方向：**

```text
Forward Production DAG
Natural → Processed → Finished

Recovery Network
Finished / Waste → Recoverable Goods
```

`RecoveryProcessDefinition` 可以复用与 ProductionProcess 相同的基础 Process 模型，但**不参与“Forward DAG 必须无环”的约束**。

因此我们真正需要保证的是：

> **正向制造链无环，而整个物质经济系统允许通过显式 Recovery Process 形成受损耗的循环。**

这和你提出的“长期可持续但不简单刷新矿物”会非常契合。

---

请继续回答 **Q188–Q195**。这一轮落定之后，下一层就可以正式开始决定：

**自然资源源与原料的分类体系，以及第一版到底需要多少种基础资源。**

---

## 5. User  (2026-09-10 07:02:35 UTC)

Q188 同意

Q189 B，需要明确区分，因为一个Resource Source的体素块中存储中复数个raw good，例如你将一个森林块开采殆尽可能有1000个log

Q190 没问题，采用一等

Q191 不存在等级，只依据DAG

Q192 允许

Q193 同意

Q194 严格使用整数单位

Q195 拆分为两个图，即生产图和回收图，都都无环DAG

---

## 6. Assistant  (2026-09-10 07:02:54 UTC)

> [思考摘要] 继续厘清资源：我会基于现有项目约束，先集中确认基础资源分类、资源来源与采集规则。下一轮只处理这一层，不提前混入加工配方、建筑或交易依赖。 / 继续厘清资源

这一轮进一步收紧了模型：**Resource Source 是世界中的“储量容器”，Raw Good 才进入标准货物网络；正向生产与回收各自都是独立无环 DAG。** 这样资源系统的图结构已经足够稳定，可以开始进入“自然资源层”的实际设计。

> [思考摘要] 厘清资源来源：我会继续确认资源源的运行时储量、单体素支持的资源种类、储量与耐久度是否分离，以及采集过程是否纳入统一流程定义，避免后续模型反复调整。 / 厘清资源来源

❓ **Q196** - **Resource Source 是否仍然是普通 VoxelDefinition**：既然我们已经确定 Source 与 Raw Good 分离，那么 Source 在领域模型中的身份需要明确。

一种方案是建立独立的 `ResourceSourceDefinition` 类型；另一种是继续遵守“一切可实体化对象共享 VoxelDefinition”，把矿脉、森林资源块等定义为普通 VoxelDefinition，只通过 Tag / Behavior 表达其资源属性。

➡️ **我推荐继续使用普通 `VoxelDefinition`，通过 `ResourceSourceBehavior` 表达资源源机制。**

```text
VoxelDefinition
└─ ResourceSourceBehavior
   ├─ Reserve / Yield semantics
   └─ Extraction rules
```

这样不会破坏现有“一切可实体化对象共享 VoxelDefinition”的架构。现有文档也明确要求所有可实体化对象统一建立在 Voxel 模型上。fileciteturn0file2L26-L30

---

❓ **Q197** - **一个 Source Voxel 能否储存多种 Raw Good**：你已经明确：

> 一个森林块中可以储存 1000 个 Log。

这里已经确定的是**一个 Source 可以持有多个单位**。还需要决定是否可以同时持有**多种货物**。

例如：

```text
Polymetallic Deposit
├─ 700 Metal Ore
└─ 120 Rare Mineral

Forest
├─ 1000 Log
└─ 80 Plant Material
```

还是严格规定：

```text
一个 Source Voxel
→ 一种 Raw Good + Quantity
```

副产品通过 Extraction Process 产生，而不是直接作为地下库存。

➡️ **我推荐一个 Source 可以拥有多种 Reserve。**

即：

```text
ResourceSourceState
└─ Reserves
   ├─ RawGood A × 700
   └─ RawGood B × 120
```

理由是这样能自然表达伴生矿、混合生物资源等，而且不会增加太多复杂度。具体 Extraction Process 可以决定每次开采哪些 Reserve、以什么比例取出。

---

❓ **Q198** - **Resource Reserve 与 Durability 是否严格分离**：现有 Voxel 已经拥有统一的 `CurrentDurability / MaxDurability`。fileciteturn0file2L32-L35

但资源源还需要例如：

```text
Forest
Durability = 100
Reserve(Log) = 1000
```

玩家开采木材时，是扣 Durability，还是扣独立 Reserve？

➡️ **我推荐严格分离。**

- `Durability`：这个体素作为物理对象是否被摧毁。
- `Reserve`：里面还剩多少可提取资源。

因此：

```text
Mining / Logging → consume Reserve
Weapon Damage    → reduce Durability
```

资源耗尽和物理摧毁是两件不同的事情。

这也意味着以后炸毁一个尚有 500 单位矿物的矿源时，可以明确设计资源损失或散落，而不是让 Durability 同时承担两个完全不同的语义。

---

❓ **Q199** - **资源耗尽后 Source Voxel 怎么处理**：当：

```text
Reserve = 0
```

这个 Voxel 应该：

**A. 直接消失，Cell 变 Empty。**

**B. 转化成另一个普通 Voxel。**

例如：

```text
Forest Resource → Bare Ground / Stump
Ore Deposit → Exhausted Rock
```

**C. Source Voxel 保留，只是变成 Exhausted 状态。**

➡️ **我推荐 B 为一般机制，但允许 Definition 指定耗尽结果。**

即：

```text
ResourceSourceBehavior
└─ OnDepleted
   ├─ Remove
   └─ ReplaceWith(VoxelDefinitionId)
```

森林、矿床、采石场等可以有不同结果。

不建议永久保存大量没有任何作用的 `ExhaustedSourceState`；如果耗尽后的地形仍有意义，就直接转换成真正的地形 Voxel。

---

❓ **Q200** - **Extraction 是否属于 Production DAG**：我们已经有：

```text
Resource Source
→ Raw Good
→ Processed Good
→ ...
```

但 Source 本身不是普通可库存货物，因此严格来说 Extraction 不完全是 `Good → Process → Good`。

有两个选择：

**A. Production DAG 从 Raw Good 开始。**  
Extraction 是独立于生产 DAG 的资源采集系统。

```text
Source --Extraction--> Raw Good

Raw Good --Production--> ...
```

**B. 把 Extraction 也纳入统一 Process 图。**  
只是 Process 的输入除了 Goods，还允许 World Resource Source。

➡️ **我推荐 A。**

正式结构变成三个相邻但边界清楚的系统：

```text
Resource Source
     │
 Extraction
     ▼
  Raw Goods
     │
     ▼
Forward Production DAG
     │
     ▼
 Finished Goods

Finished / Waste Goods
     │
     ▼
Recovery DAG
```

`ExtractionProcessDefinition` 和 `ProductionProcessDefinition` 可以共享很多数据结构，但领域语义最好不同。否则以后生产设施和采矿行为会出现大量“这个 Input 到底是库存还是世界体素”的条件分支。

---

❓ **Q201** - **“自然资源”是否包含农业、林业、畜牧等可再生初级产物**：这是接下来设计基础资源集合前必须解决的分类问题。

矿石、石油显然来自 Source。

但：

```text
Farm → Grain
Orchard → Fruit
Livestock → Meat
Forestry → Log
```

这些东西到底算不算整个资源体系中的“自然资源起点”？

如果不算，就会产生一个问题：`Grain` 的上游是什么？种子、肥料、水……继续追下去不可能得到一个真正的 DAG 根节点。

➡️ **我推荐扩大“Primary Resource”的概念，不要求所有 Raw Good 都来自有限矿藏式 Source。**

形成两类一级来源：

```text
Primary Resource Acquisition
├─ Extraction
│  └─ finite / geological / wild Source
│
└─ Primary Production
   └─ renewable biological production
```

例如：

```text
Ore Deposit → Metal Ore
Oil Deposit → Crude Oil

Farm → Grain
Forestry → Log
Livestock Facility → Meat
```

而这些产出的 `Metal Ore / Grain / Log / Meat` 都是 **Raw Good**，随后进入同一 Forward Production DAG。

也就是说，“所有高级产品最终来自自然基础”仍成立，但不强迫所有自然基础都必须以矿脉式 Source 存在。

---

❓ **Q202** - **天然存在但几乎没有物流价值的物质是否进入资源网络**：比如水、空气、普通土壤等都是物质，也理论上可以装进容器出售。

如果严格按照“物质 + 可库存 + 可交易”判断，它们似乎都应该成为商品。但这可能制造大量没有策略意义的内容。

➡️ **我推荐继续让 Q184 的 Gameplay Difference Test 拥有最高优先级。**

也就是说：

> **“可以被交易”是进入资源网络的必要条件之一，但不是要求游戏必须为现实世界每种可交易物质建立 Definition。**

例如如果普通水在绝大多数区域无限易得，对贸易和产业选址没有意义，那么就不需要 `Water` Good；生产过程可以把当地水源视为环境 Requirement。

但如果以后荒漠地区的水运输成为真正有价值的物流问题，再把 `Water` 提升为标准 Raw Good。

同理，空气、普通土壤等默认不进入商品体系。

---

❓ **Q203** - **现有 `natural_resource` Tag 的语义是否迁移**：正式文档和当前实现现在规定：带 `natural_resource` Tag 的 VoxelDefinition 就是自然资源，聚居地也通过周边这些体素判断当地资源种类。fileciteturn0file0L21-L27 fileciteturn0file9L49-L53

但 Q189 已经推翻了原先“世界资源体素本身就是货物”的隐含模型。

我们需要决定新的分类。

➡️ **我推荐把语义正式拆开：**

```text
resource_source
    世界中能够被采集/开采的 Source Voxel

raw_good
    从初级采集或初级生产得到、
    尚未进行工业加工的标准货物
```

原有 `natural_resource` 不再同时承担这两层含义。

聚居地的“周边自然资源”查询应该查看 `resource_source`，然后根据 Source 能够产出的 Raw Goods 派生：

```text
Nearby Iron Deposit
      ↓
Settlement resource potential:
Metal Ore
```

至于最终是**删除 `natural_resource` Tag**，还是把它重新定义为 `resource_source` 的兼容名称，我更推荐直接迁移并删除旧语义，因为项目仍处开发阶段，没有必要长期保留含糊概念。

---

本轮 Frontier 是 **Q196–Q203**。

这批决定完成后，下一轮我们就不再讨论抽象边界，而可以真正开始列出第一版的**基础 Raw Goods 集合**：矿物、能源、建材、生物资源、食品原料等，并逐项用 Gameplay Difference Test 判断哪些应该合并、哪些必须拆开。

---

## 7. User  (2026-09-10 07:17:58 UTC)

Q196 继续使用体素定义，既然当前体素已经有耐久和伤害的设计了，对自然资源来说直接复用就是很自然的表达，例如一个自然资源块的耐久为500, 那么我们就可以认为其包含了500个raw good，而开采这一希望本质就是对该资源块产生damage并获得raw good

Q197 同意，如Q196所说，多产物就对应这每次造成damage按比例获得不同的raw good，例如1 damage或者2个log和4个树枝。这些都可以直接用behavior去定义

Q198 我不太认可，这意味这要单独为自然资源添加一个Reserve字段，而其它体素都用不上

Q199 按照耐久耗尽的逻辑处理，即被摧毁

Q200 同意

Q201 同意

Q202 同意，无意义的罗列是没有必要的，只对游戏玩法和内容有价值的东西才会被加入

Q203 同意

---

## 8. Assistant  (2026-09-10 07:18:11 UTC)

> [思考摘要] 厘清资源开采：我正在把资源耐久度定义为可消耗储量，并用开采伤害换算产出。现在需要确认：任意伤害是否都能产出，还是只有带开采标记的伤害；多种产出也需要确定整数换算与余数处理方式。 / 厘清资源开采情况

Q196–Q199 把模型进一步简化成一个很强的统一规则：**自然资源源不新增 Reserve；其 CurrentDurability 就是剩余可开采量，开采是受控的 Damage，归零即销毁。** 这比我上一轮建议的双状态模型更符合你当前“少量强规则、复用已有机制”的总体设计原则。

不过这一选择马上引出几个必须先钉死的语义问题，尤其是“普通武器伤害是否也会产出资源”“每点伤害怎样换算整数货物”。这些会直接决定 ResourceSourceBehavior 的定义，所以我先把这一层问完，再进入具体 Raw Goods 列表。

> [思考摘要] 确定开采规则：我建议区分普通伤害与正式开采：开采伤害消耗耐久并产出资源，普通伤害只造成损耗；产出按实际消耗耐久计算，溢出伤害不额外产出，比例不足一单位时用累计取整。 / 确定了开采规则

❓ **Q204** - **开采伤害与普通伤害是否区分语义**：既然开采本质上复用 Damage，那么必须决定“任何 Damage 都会掉资源”还是只有明确的 Extraction 行为才会产生 Raw Good。

例如：

```text
森林资源块 CurrentDurability = 500

伐木机造成 10 Damage
→ Durability 490
→ 获得木材

列车炮造成 100 Damage
→ Durability 390
→ 是否也获得木材？
```

当前设计只有统一 Durability，复杂 Damage Type Matrix 明确延后，所以我不建议为了这个问题引入 `mining_damage / weapon_damage` 之类伤害类型。fileciteturn0file2L32-L35

➡️ **推荐：只有显式 Extraction 行为产生资源，但 Extraction 最终仍通过正式 Damage Transaction 扣除 Durability。**

即：

```text
Extraction Command
→ ResourceSourceBehavior 计算产物
→ Damage Transaction
→ 扣除 Durability
→ 发放 Raw Goods

Ordinary Damage
→ Damage Transaction
→ 扣除 Durability
→ 不产生 Raw Goods
```

因此“开采就是 Damage”成立，但“Damage 就是开采”不成立。这样武器不能自动变成最高效矿机，也不需要建立完整 Damage Type 系统。

---

❓ **Q205** - **产出应按照实际造成的 Damage 还是请求 Damage 计算**：假设矿只剩 3 Durability，矿机请求造成 10 Damage。

如果按请求值：

```text
10 Damage × 2 Ore
= 20 Ore
```

就可以通过 Overkill 凭空制造资源。

➡️ **推荐严格按 Effective Damage 计算：**

```text
EffectiveDamage =
    min(RequestedDamage, CurrentDurability)
```

所以：

```text
CurrentDurability = 3
RequestedDamage = 10
Yield = 2 Ore / Damage

实际获得 = 6 Ore
CurrentDurability → 0
Source destroyed
```

这条应成为 ResourceSourceBehavior 的不变量。

---

❓ **Q206** - **产出比例是否允许小于“每 1 Damage 产 1 个货物”**：你给出的例子：

```text
1 Damage
→ 2 Log
→ 4 Branch
```

很好处理。

但以后很可能出现：

```text
5 Damage → 1 Rare Mineral
3 Damage → 2 Chemical Feedstock
```

由于货物 Quantity 已经确定必须为整数，我们要么强制所有 Yield 都能写成“每一点 Damage 对应整数货物”，要么允许有理数比例。

➡️ **推荐允许整数比率 `N Damage → M Goods`，但不增加 remainder 状态。**

例如：

```text
rare_mineral:
5 Damage → 1 unit
```

产出根据**累计已损失 Durability**计算：

```text
TotalExtractedDamage =
    MaxDurability - CurrentDurability

TotalYield =
    floor(TotalExtractedDamage × M / N)
```

一次 Extraction 实际新增的库存量就是操作前后 `TotalYield` 的差。

这样：

```text
第 1~4 点 Damage → 0
第 5 点 Damage   → +1
第 6~9 点        → 0
第 10 点         → +1
```

不需要给 Source 增加“0.6 个矿石”或 YieldAccumulator，同时无论玩家把 10 Damage 分成十次还是一次，最终产量都完全一致。

---

❓ **Q207** - **同一个 Source 的总产出是否应当确定**：例如一个完整森林资源块：

```text
MaxDurability = 500
Yield:
1 Damage → 2 Log
1 Damage → 4 Branch
```

是否意味着它完整开采后**必然**得到：

```text
1000 Log
2000 Branch
```

还是每次开采可以随机：

```text
1 Damage → 1~3 Log
Rare drop chance = 2%
```

➡️ **推荐基础资源总产出完全确定，不做逐次随机掉落。**

资源分布本身已经可以由世界生成创造稀缺性；生产经济又高度依赖供给可预测性。若单个矿块的总产量还大量随机，会给贸易策略增加噪音而不是有意义的决策。

未来如果确实需要“富矿”“罕见伴生资源”，更适合在**世界生成阶段决定 Source 是什么、在哪里、数量多少**，而不是开采时反复掷骰子。

---

❓ **Q208** - **不同地点的同种 Source 是否拥有相同单位储量**：如果 `core:forest-source` 的 `MaxDurability = 500`，那么是不是世界中每一个这种 Voxel 都固定包含相同总量？

有两个方向：

**A. Definition 决定单格储量。**

```text
Forest Source
MaxDurability = 500
```

所有完整森林格都是 500。一个地区资源丰富，表现为**森林资源格更多、更密集**。

**B. Instance 可以有不同初始 Durability。**

```text
Forest #1 = 500
Forest #2 = 320
Forest #3 = 85
```

这样单格本身具有“资源丰富度”。

➡️ **推荐首版采用 A。**

这是 Q196 简化模型最自然的结果：

> `MaxDurability` 同时就是该 Source Definition 的标准单位储量。

宏观资源丰富度由：

```text
Source 类型
× Source 数量
× 空间密度
```

决定。

这样不需要引入新的 Richness State，也不会出现一个“从未受损但出生时只有 37% Durability”的奇怪实例语义。

如果 Playtest 以后证明单格丰富度差异确实能产生重要 Gameplay，再扩展也不迟。

---

❓ **Q209** - **Tag 与 ResourceSourceBehavior 的职责是否严格分离**：Q203 已确定用 `resource_source` 取代旧 `natural_resource` 语义，现在还要确定“这个 Source 能产什么”存在哪里。

我建议：

```text
Tag:
resource_source
```

只回答：

> 这是一个自然资源源。

真正的产出关系：

```text
ResourceSourceBehavior
Yield:
  log:
    damage: 1
    quantity: 2
  branch:
    damage: 1
    quantity: 4
```

由 Behavior 定义。

这样聚居地资源查询也不只是看到“附近有 forest”，而可以：

```text
附近 resource_source
        ↓
读取 ResourceSourceBehavior
        ↓
推导可开采 Raw Goods
        ↓
Log / Branch
```

➡️ **推荐同意这一严格分工。**

这完全符合已经确定的架构：Tag 只能承担被动分类，真正的逻辑机制属于 Behavior。fileciteturn0file5L97-L108

同时，原文目前“聚居地资源种类直接从带 `natural_resource` 标签的 VoxelDefinition 得到”的规则需要在这次资源设计完成后修改为新的 Source → Yield 推导关系。fileciteturn0file9L49-L53

---

❓ **Q210** - **Raw Good 是否只是语义分类，而不是“DAG 根节点”的同义词**：Q201 确定农业、林业、畜牧等 Primary Production 也可以产生 Raw Good，这会出现：

```text
Farmland → Grain
Livestock Production:
Grain + Production Factors → Meat
```

那么 `Meat` 虽然是未经进一步加工的初级商品，却未必是图论意义上的根。

➡️ **推荐把 `raw_good` 定义成内容语义，而不是拓扑语义：**

> **Raw Good = 通过 Extraction 或 Primary Production 得到、尚未经过我们认为有 Gameplay 意义的工业加工的标准货物。**

它**不意味着 indegree = 0**，也不代表 Tier 0——因为我们已经明确整个系统不存在 Tier。

例如完全可以存在：

```text
Grain              raw_good
Meat               raw_good
Log                raw_good
Metal Ore          raw_good

Flour              processed_good
Lumber             processed_good
Metal              processed_good
```

即使 Meat 的生产过程消耗 Feed，它仍然可以是 Raw Good。

---

这一轮是 **Q204–Q210**。

尤其是 Q204–Q208 会决定我们能否只靠现有的 `Durability + Behavior + Damage Transaction` 干净地完成整个自然资源开采模型。等这些确定后，下一轮就可以正式开始**列第一批自然资源 Source 与 Raw Good 候选集合并逐项砍粒度**。

---

## 9. User  (2026-09-10 07:42:31 UTC)

Q204 同意

Q205 严格按 Effective Damage 计算

Q206 同意

Q207 同意完全确定

Q208 同意，先采用A

Q209 同意

Q210 同意

---

## 10. Assistant  (2026-09-10 07:42:44 UTC)

> [思考摘要] 规划资源轮廓：我正在把自然资源与一级产出拆成可玩的具体类别，同时保持类别数量可控。我会优先确认矿产、能源、林业、农业和畜牧业的边界，再为每类提出首选清单。 / 规划资源轮廓

这一层已经闭合：自然资源开采可以完全复用现有 `Durability + Damage Transaction + Behavior`，不需要新增 Reserve；而 `resource_source` 只承担分类，具体 Yield 由 Behavior 定义。这样下一轮可以正式开始砍**基础自然资源与 Raw Good 的粒度**。

> [思考摘要] 确认资源分类：我正在准备最后一轮资源问题：先确认分类仅用于界面与区域差异，还是直接决定配方；随后分别敲定金属、非金属矿产、化石资源、林业、农业与畜牧业的具体首版类别。

❓ **Q211** - **资源大类是否只是内容组织，而不是新的领域层级**：接下来我们会使用“金属资源、能源资源、生物资源、食品原料”等大类来组织内容，但是否需要建立 `ResourceCategory`、`RawGoodCategory` 这样的强类型领域结构？

➡️ **我推荐不要建立新的 Category 层。** 保持与当前设计一致：

```text
VoxelDefinition
├─ Tags
└─ Behaviors
```

例如：

```text
core:ferrous-ore
Tags:
- raw_good
- mineral
- metal_ore
```

这些标签主要服务于内容查询、UI、世界生成和经济规则；真正的开采机制仍属于 Behavior。现有架构已经明确 Tag 只负责被动分类，Behavior 才负责机制。fileciteturn0file5L97-L108

---

❓ **Q212** - **金属资源应该拆成几种**：按照 Gameplay Difference Test，我认为不能只有一个万能 `Metal Ore`，否则从基础工业一直到电子设备、列车武器都会共享同一种矿物，地域分工会明显不足。

但也没必要做铁、铜、铝、镍、钛、钨等现实元素表。

我建议首轮采用 **3 类功能性金属原料**：

```text
Ferrous Ore
    ↓
Structural Metal
    ↓
结构件、机械件、车辆主体

Conductive Ore
    ↓
Conductive Metal
    ↓
电气件、电子件、电机等

Rare Mineral
    ↓
Specialty Material
    ↓
高端电子、先进设备、武器等
```

这里的名称最终可以继续调整；关键是语义。

`Ferrous Ore` 代表廉价、大宗、结构用途金属矿。

`Conductive Ore` 代表类似铜这一类电气工业核心矿产。

`Rare Mineral` 则抽象稀有金属、稀土和其他高级工业矿物。

➡️ **我推荐采用这三类，不再额外增加 Light Metal Ore。** 铝、钛等现实差异以后如果真的能形成独立 Gameplay，再拆分；首版可以通过高级合金工艺表达轻质或高性能材料。

---

❓ **Q213** - **非金属地质资源的基础集合**：除了金属，我认为还有四种资源具有足够明显且互不替代的产业作用：

```text
Stone
→ 建材、混凝土类产品

Silica
→ Glass、电子/精密工业材料

Coal
→ Fuel + 冶金 + 化工路线

Crude Oil
→ Fuel + Polymer + Chemical Feedstock
```

我暂时**不建议**加入天然气、黏土、盐、硫、磷矿等更细资源，因为目前它们产生的 Gameplay 价值还不足以证明拆分。

其中 Coal 和 Crude Oil 尤其值得保留为两种东西：前者可以同时连接能源与冶金，后者连接燃料与化工，因此会自然产生完全不同的产业地域。

➡️ **推荐初始地质资源集合就是：**

```text
Ferrous Ore
Conductive Ore
Rare Mineral
Stone
Silica
Coal
Crude Oil
```

共 **7 种 Raw Goods**。

---

❓ **Q214** - **农业与食品 Raw Goods 应该拆到什么粒度**：食物体系很容易失控。如果做成：

```text
Wheat
Rice
Corn
Apple
Banana
Potato
Beef
Pork
Chicken
Milk
...
```

就直接违背我们已经确定的抽象原则。

但一个万能 `Food` 又太粗，会让食品工业完全没有生产结构。

➡️ **我推荐初级食品只保留三类：**

```text
Grain
Produce
Animal Products
```

对应大致含义：

```text
Grain
→ 主粮、面粉、酿造等基础

Produce
→ 水果、蔬菜等新鲜植物食品

Animal Products
→ 肉、乳、蛋等动物性食品
```

然后高级食品由生产 DAG 继续加工，例如：

```text
Grain → Staple Food

Grain + Animal Products
→ Processed Food

Produce + Animal Products
→ Prepared Food
```

具体菜品不成为独立商品。

---

❓ **Q215** - **非食品生物原料的基础集合**：农业和林业还需要支持建筑、衣物、纸张等产业。

我认为目前真正有意义的是：

```text
Timber
Natural Fiber
```

`Timber` 抽象原木、木材原料，连接建筑、家具、纸浆等产业。

`Natural Fiber` 抽象棉、麻、羊毛等纺织原料，连接衣物和其他纺织品。

➡️ **推荐先只有这两类。**

因此整个生物类 Raw Good 初步就是：

```text
Grain
Produce
Animal Products
Timber
Natural Fiber
```

没有单独的 Cotton、Wool、Leather 等，除非以后装备或产业设计证明必须拆开。

---

❓ **Q216** - **树枝、碎料这类低价值副产物是否值得成为独立货物**：前面我们举过：

```text
1 Damage
→ 2 Log
→ 4 Branch
```

从 Behavior 模型上完全可以这样做。

但从 Gameplay Difference Test 来看，`Branch` 很可能过细。它真正代表的可能是各种：

```text
树枝
木屑
农业残余物
低价值植物废料
```

➡️ **我推荐如果确实需要这条产业路线，统一抽象成 `Biomass`，而不是 Branch。**

例如：

```text
Forest Source
1 Damage
→ 2 Timber
→ 1 Biomass
```

之后：

```text
Biomass
→ Fuel

Biomass
→ Chemical Feedstock
```

这样它能成为一个真正有用途的低价值大宗货物。

但这里还有另一种选择：**首版完全不加入 Biomass**，直到能源/化工 DAG 证明它真的产生值得保留的贸易路线。

我的推荐稍偏向后者：**先不加 Biomass，把它作为第一候选扩展资源。**

---

❓ **Q217** - **不同获取方式能否产生完全相同的 Raw Good**：例如 Timber 既可以来自天然森林：

```text
Forest Resource Source
--Extraction-->
Timber
```

也可以来自长期经营的林场：

```text
Forestry
--Primary Production-->
Timber
```

同理 Animal Products 以后可能来自畜牧业，也可能来自野生资源。

是否需要：

```text
Wild Timber
Farmed Timber
```

两种商品？

➡️ **推荐完全不要区分。**

只要进入标准商品体系以后没有 Gameplay 上值得关注的差异，它们就是同一个 `VoxelDefinitionId`：

```text
core:timber
```

**来源属于 provenance / 生产历史，不属于商品身份。**

这会产生很好的经济效果：一次性天然资源与可持续产业可以直接竞争同一个市场。

---

❓ **Q218** - **Extraction 是否只能产出 Raw Good**：我们已经确定 Extraction 与 Forward Production DAG 分离，那么应该进一步限制：

```text
Resource Source
--Extraction-->
Raw Good
```

还是允许天然资源直接掉落 Processed Good，例如森林直接产出 Lumber、矿床直接产出 Metal？

➡️ **我推荐建立硬语义边界：自然资源 Extraction 只产生 Raw Good。**

因此：

```text
Forest
→ Timber

Ore Deposit
→ Ferrous Ore
```

而不是：

```text
Forest
→ Lumber

Ore Deposit
→ Steel
```

这样 Extraction、Primary Production 和工业 Production 的职责非常清晰。

如果以后存在世界中的废墟、垃圾堆、战场残骸等可以“搜刮出零件”，它们应该属于另一类 **Salvage Source**，而不是 `resource_source`。

---

❓ **Q219** - **资源是否拥有固有“稀有度”属性**：例如 `Rare Mineral` 是否需要：

```text
Rarity = Rare
```

这种 Definition 字段？

这看起来方便 UI 和生成，但容易把两个不同概念混在一起：

- 世界中到底生成多少；
- 这个商品现在到底多稀缺、多贵。

而后者显然会随着市场和世界状态变化。

➡️ **我推荐完全不增加固有 Rarity 字段。**

资源实际稀缺程度应该来自：

```text
World Generation Distribution
× Remaining Resource Sources
× Geographic Accessibility
× Production Capacity
× Demand
```

`Rare Mineral` 这个名称只是内容语义，不意味着系统里必须存在 `Rarity.Rare`。

同理，它的 `Value` 仍只是稳定基础估值，不能替代实际市场稀缺性；现有设计已经明确实际 `Price` 属于 Market Context。fileciteturn0file0L21-L27

---

如果以上全部采用，那么我们目前会得到一个非常克制的 **12 种基础 Raw Goods 候选集**：

```text
Geological
├─ Ferrous Ore
├─ Conductive Ore
├─ Rare Mineral
├─ Stone
├─ Silica
├─ Coal
└─ Crude Oil

Biological / Primary
├─ Grain
├─ Produce
├─ Animal Products
├─ Timber
└─ Natural Fiber
```

这个数量我认为比较合适：已经足以产生明显的地域分工和交叉产业链，但还没有进入“为了丰富而丰富”的内容膨胀。

请继续回答 **Q211–Q219**。下一轮我会根据你保留下来的 Raw Goods，开始设计它们对应的 **Resource Source / Primary Production 来源结构**，然后再向上推第一层加工品。

---

## 11. User  (2026-09-10 07:50:23 UTC)

Q211 同意，不需要

Q212 同意，是合理的划分

Q213 同意

Q214 同意

Q215 同意

Q216 不需要，且树枝只是一个例子，并且如果是没有价值的副产物就没必要进行设计，如果有副产物，那么它应当对游戏内容和玩法有正向意义才添加，不然就是纯粹的噪声

Q217 不用区分

Q218 同意Extraction只产生raw good

Q219 不需要，这对游戏玩法没有任何贡献，我们已经设计了value和price属性

---

## 12. Assistant  (2026-09-10 07:50:30 UTC)

Q211–Q219 已把首批 **12 种 Raw Goods** 冻结下来，同时也确定了一个内容原则：**副产品不是“真实所以加入”，而是只有能创造正向玩法价值才加入。** 这条原则后续同样适用于中间品和成品，否则 DAG 很容易膨胀成目录而不是游戏系统。

现在的 frontier 是这些 Raw Goods **如何进入经济系统**：哪些来自世界 Source，哪些来自可持续的 Primary Production，以及 Primary Production 与普通加工的关系。

> [思考摘要] 梳理生产边界：我正在把“初级生产”放回统一的生产模型：它可以沿用同一套流程定义，只是作为资源获取或原料转换的一类。这样既支持农场产出，也保留生产图的统一性。 / 梳理了生产边界

> [思考摘要] 设计采集机制：我正在继续细化初级生产：资源源点默认对应一种原料，特殊情况下才多产出；矿藏和森林表现为源点簇。接下来需要确认农业、畜牧、纤维、林业，以及设施与地图资源的关联方式。

❓ **Q220** - **首批 Resource Source 应覆盖哪些 Raw Goods**：12 种 Raw Goods 并不都需要在自然界中以可开采 Source Voxel 的形式出现。

我建议首批只建立 **8 类 Resource Source**：

```text
Geological Sources
├─ Ferrous Ore Deposit      → Ferrous Ore
├─ Conductive Ore Deposit   → Conductive Ore
├─ Rare Mineral Deposit     → Rare Mineral
├─ Stone Deposit            → Stone
├─ Silica Deposit           → Silica
├─ Coal Deposit             → Coal
└─ Oil Deposit              → Crude Oil

Biological Source
└─ Natural Forest           → Timber
```

而以下四种主要依赖可持续生产：

```text
Grain
Produce
Animal Products
Natural Fiber
```

我不建议首版再增加“野生动物资源”“野生水果资源”“天然棉花资源”等 Source。它们即使现实中存在，对当前宏观经济玩法贡献也很有限。

➡️ **推荐采用上述 8 种 Source。** `Timber` 特殊地同时存在天然森林开采和人工林业两条来源。

---

❓ **Q221** - **多产物 Source 应该是常态还是例外**：Q197 已经允许：

```text
Resource Source
→ Raw Good A
+ Raw Good B
+ ...
```

但我们还需要决定内容设计原则。

例如是否让：

```text
Ferrous Deposit
→ Ferrous Ore
+ Rare Mineral
+ Stone
```

成为常态，以模拟真实伴生矿；还是通常保持：

```text
Ferrous Deposit
→ Ferrous Ore
```

只有当多产物真正产生经济决策时才采用复杂 Yield。

➡️ **推荐“机制允许，内容默认单主产物，多产物属于有意识设计的例外”。**

理由与 Q216 相同：多产物本身不是价值。只有例如某种特殊矿区能同时支撑两个产业、从而明显改变当地经济结构时，才值得设计复合 Yield。

---

❓ **Q222** - **一个“矿床/森林”在空间上如何表示**：Q208 已确定同一 Source Definition 的完整 Voxel 拥有相同 `MaxDurability`，所以资源丰富度不能靠每格随机储量表达。

我建议一个宏观资源点本身就是**多个相邻或近邻 Source Voxels 构成的空间分布**：

```text
Large Ferrous Deposit

■■■■■
■■■■■■
 ■■■■
  ■■
```

如果每个：

```text
Ferrous Source Voxel
MaxDurability = 500
Yield = 1 Ore / Damage
```

那么 100 格矿床就是标准意义上的 50,000 单位储量。

同理，一片森林也是大量 Forest Source Voxels，而不是一个“森林体素里塞 500,000 木材”。

➡️ **推荐采用这种空间化 Cluster 模型。**

这能让“矿区规模、资源密度、开采推进、最终枯竭”直接来自真实 World State，而无需额外建立 `DepositInstance.Reserve` 之类第二事实来源。

---

❓ **Q223** - **首批可再生 Primary Production 应有哪些**：针对已经确定的 Raw Goods，我建议首批生产活动正好对应五类：

```text
Grain Farming
→ Grain

Produce Farming
→ Produce

Livestock Production
→ Animal Products

Fiber Farming
→ Natural Fiber

Forestry
→ Timber
```

这五种已经足以形成农业城镇、畜牧区域、林业区域等经济差异。

这里特意不设计：

```text
Fishing
Hunting
Dairy Farming
Fruit Orchard
Cotton Farm
...
```

因为这些目前都可以被上述宏观过程吸收。

➡️ **推荐首批就是这五种 Primary Production。**

以后如果例如 Fishing 真能创造独立的沿海经济和贸易策略，再拆出 `Aquatic Products`，而不是现在提前占坑。

---

❓ **Q224** - **Primary Production 是否继续使用普通 `ProductionProcessDefinition`**：此前我们将 Extraction 从生产 DAG 中拆了出去，因为它直接操作世界 Source。

但农业不同。它实际上非常接近普通生产：

```text
Facility + Time + Labor + Site
        ↓
      Grain
```

Livestock 甚至可能进一步出现：

```text
Grain + Production Factors
        ↓
Animal Products
```

因此这里可以有两种设计：

**A.** 再建 `PrimaryProductionProcessDefinition`。

**B.** 所有这些都使用普通 `ProductionProcessDefinition`；“Primary Production”只是一个设计语义，表示输出 Raw Good，并不形成新的领域类型。

➡️ **我推荐 B。**

这样整个模型只需要：

```text
ExtractionProcess
World Source → Goods

ProductionProcess
Goods / Factors → Goods

RecoveryProcess
Goods → Recovered Goods
```

其中 Production DAG 完全可以存在：

```text
[无货物输入]
    ↓ Farming
  Grain

Grain
    ↓ Livestock
Animal Products

Raw Goods
    ↓ Industry
Processed Goods
```

这会稍微修正我们 Q200 时画的示意图：**Extraction 仍独立，但 Production DAG 可以包含产生 Raw Good 的 Primary Production Process。** `raw_good` 本来就已经在 Q210 定义为语义分类，而不是拓扑层级，所以没有矛盾。

---

❓ **Q225** - **Primary Production 的“自然条件”应该如何表示**：农业显然不能完全无条件凭空生产。

但我们已经明确不应该因为现实存在就把 Water、Soil、Sunlight 等全做成货物。

因此例如 Grain Farming 可以需要：

```text
Production Factors
├─ suitable site / biome
├─ facility capacity
├─ labor
└─ time
```

而不是：

```text
10 Water
+ 3 Soil
+ 1 Seed
+ 2 Fertilizer
→ 5 Grain
```

➡️ **推荐首版把土地、水源、气候等抽象成 Site / Environmental Requirements，而不是库存输入。**

这同时创造了真正有价值的地域差异：

```text
肥沃地区
→ 高农业潜力

矿业地区
→ 高矿业潜力

两者之间
→ 产生贸易
```

以后如果 Fertilizer 被证明能形成值得玩的化工—农业贸易链，再把它提升成真正货物即可。

---

❓ **Q226** - **生产能力是否必须来自世界中实际存在的设施**：现有聚居地设计已经确定，建筑是实际存在的 Composite，建筑数量也从当前实际实例派生，而不是由 Layout 保存另一份权威清单。fileciteturn0file9L55-L66

那么经济系统还可以选择：

**A. 聚居地拥有抽象数值：**

```text
IndustrialCapacity = 500
AgriculturalCapacity = 300
```

即使世界里的实际建筑没有对应关系。

**B. 生产能力最终由实际 Building Composite 提供。**

例如：

```text
Farm Building
→ enables Grain Farming
→ capacity 20

Smelter Building
→ enables Smelting
→ capacity 10
```

聚居地只是汇总这些能力。

➡️ **我推荐 B。**

这正好服务于你“玩家也能收购原料建设自己的聚居点”的目标：

```text
资源
→ 建造生产设施
→ 获得真实生产能力
→ 制造商品
→ 贸易 / 再投资
```

同时 NPC 聚居地与玩家聚居点继续遵守 Q182 的同一系统，而不是 NPC 使用虚构的 `Production += 100`。

这里依然可以保持宏观：一个 Farm Composite 可以代表整个农业设施，而不需要模拟每株小麦。

---

❓ **Q227** - **生产设施与 Process 是多对多还是一对一**：如果采用 Q226，那么 Facility 和 Process 的关系也要定原则。

一种极端设计：

```text
Grain Farm
只能执行 Grain Farming

Produce Farm
只能执行 Produce Farming
```

另一种则是：

```text
Agricultural Facility
可以执行若干兼容 Process
```

工业同理，一个通用 Factory 是否可以根据配置制造不同产品。

➡️ **推荐采用多对多，但必须显式声明兼容性。**

即：

```text
Facility Capability
        ↕
Production Process Requirements
```

而不是硬编码“一建筑对应一配方”。

例如：

```text
Farm Facility
├─ Grain Farming
└─ Produce Farming

Basic Workshop
├─ Mechanical Parts
├─ Textile Goods
└─ ...
```

更高级过程则要求更专业设施。

这样玩家以后真正拥有一个**产业配置问题**：有限设施能力应该拿来生产什么，而不是每一种货物都必须建一种名字完全对应的建筑。

---

❓ **Q228** - **“自然资源种类”和“当地可生产商品”必须继续严格区分吗**：当前正式设计中的“聚居地自然资源种类”本来就是从周边实际自然资源体素派生的。fileciteturn0file9L44-L53

在新模型下，一个农业聚居地可能：

```text
附近没有任何 Grain Source
但拥有大量农场
因此大量生产 Grain
```

所以不能再把：

> 当地存在 Grain

和：

> 当地拥有 Grain 的自然资源

混为一谈。

➡️ **推荐严格分离三个事实：**

```text
Local Resource Sources
    世界周围实际存在什么可开采 Source

Production Capabilities
    当前设施能够运行什么 Process

Current Goods Supply
    当前市场实际能提供多少货物
```

例如：

```text
农业城镇
Natural Resources:
  none / Forest

Production:
  Grain
  Produce

Market Supply:
  Grain 1800
  Produce 700
  Fuel 50
```

这三层以后会成为经济系统非常重要的边界。

---

请继续回答 **Q220–Q228**。

这一轮之后，如果 Source 与 Primary Production 的入口都确定，frontier 就会向上推进到真正的**第一层加工品**。届时我们可以从这 12 种 Raw Goods 开始设计 `Metal / Lumber / Fuel / Chemical Feedstock / Textile Material / Food...`，并第一次画出生产 DAG 的实际骨架。

---

## 13. User  (2026-09-10 08:14:04 UTC)

Q220 同意

Q221 同意

Q222 没错，同意

Q223 同意

Q224 B

Q225 同意

Q226 B 更具体的来说，生产设施都是复合体，而复合体的所有权是聚居地，即聚居地的生成设施在产生资源

Q227 同意

Q228 同意

---

## 14. Assistant  (2026-09-10 08:14:18 UTC)

> [思考摘要] 继续设计生产层：我正在把第一层加工品收敛成实际生产图：金属、建材、玻璃、燃料、化工原料、食物与材料。接下来需要确认加工边界、产物粒度，以及副产物是否进入同一体系。 / 继续设计生产层

Q220–Q228 之后，资源系统已经从“自然资源在哪里”推进到了“经济网络里实际流通什么”。下一轮先只决定**第一层加工品的抽象粒度和骨架**；具体配方比例、生产速度、产能数值都依赖这些节点，因此放到更后面的轮次。

> [思考摘要] 收敛加工品体系：我建议第一层只保留能改变用途的通用产物：结构金属、导电材料、建材、玻璃、燃料、化工原料、基础食品与纤维材料。需要确认多产物加工和直接消耗原料如何处理。 / 收敛加工品体系

❓ **Q229** - **加工网络是否要求每种 Raw Good 都有对应的“一级加工品”**：现在最容易犯的错误，是为了让结构看起来整齐，强行设计：

```text
Raw A → Processed A
Raw B → Processed B
Raw C → Processed C
...
```

但这会制造大量只是“换了个名字”的节点。例如 `Produce → Processed Produce` 如果没有新的贸易、保存、用途或生产决策，就没有意义。

➡️ **我推荐不追求层级对称。** DAG 只表达实际有 Gameplay 意义的加工关系。Raw Good 可以：

```text
直接作为最终消费品
直接进入较高级制造
经过一层加工
经过多层加工
```

例如 `Coal` 可以直接成为某些 Process 的输入；`Produce` 可以直接用于食品生产；而 `Ferrous Ore → Structural Metal` 则明显值得保留。

---

❓ **Q230** - **基础金属加工是否采用三条对应路线**：Q212 已经把矿产拆成三类，我建议第一批对应三个基础工业材料：

```text
Ferrous Ore
    ↓ Refining / Smelting
Structural Metal

Conductive Ore
    ↓ Refining
Conductive Metal

Rare Mineral
    ↓ Refining
Specialty Material
```

其中：

- `Structural Metal`：车体、建筑、机械结构的大宗材料；
- `Conductive Metal`：电机、电气、电子系统的重要材料；
- `Specialty Material`：高级设备、先进武器、精密部件等稀缺材料。

这里特意不用 Steel、Copper、Titanium 等现实名称，因为我们设计的是功能差异而非元素周期表。

➡️ **我推荐这三个节点全部保留。**

它们会成为之后机械、电子、武器、列车设备三条制造路线非常重要的分叉基础。

---

❓ **Q231** - **Stone 和 Silica 是否分别保留独立加工路线**：我建议：

```text
Stone
  ↓ Processing
Construction Material

Silica
  ↓ Processing
Glass
```

`Construction Material` 抽象砖、混凝土、加工石材等大宗建筑材料。

`Glass` 则不仅用于建筑，还可以进入车辆、精密设备、电子设备等路线，因此具有独立 Gameplay 意义。

➡️ **我推荐两者都保留。**

同时允许 `Stone` 本身被某些简单建筑直接消耗，并不要求所有建筑都先加工成 Construction Material。

---

❓ **Q232** - **煤是否应该直接作为商品使用，而不创建“加工煤”节点**：Coal 本身已经同时拥有几种潜在价值：

```text
Coal
├─ Fuel / Energy-related Process input
├─ Metallurgy input
└─ Chemical industry input（如果以后需要）
```

我们当然可以额外创建 Coke、Processed Coal 等，但目前看不到足够的 Gameplay 差异。

➡️ **我推荐 Coal 本身直接参与后续 Process，不为它设计固定的第一层加工品。**

以后如果某条产业链确实需要“煤经过加工后才能承担另一种战略角色”，再添加对应商品，而不是因为现实工业里存在焦炭就提前加入。

---

❓ **Q233** - **石油路线是否保留 `Chemical Feedstock` 这一中间商品**：Crude Oil 可以有两种抽象方式。

最简单的是：

```text
Crude Oil
├→ Fuel
└→ Polymer
```

更丰富的是：

```text
Crude Oil
   ↓ Refining
Fuel + Chemical Feedstock

Chemical Feedstock
   ↓
Polymer
```

第二种多了一个可贸易中间品，但也确实创造了新的产业关系：

```text
油田地区
→ 炼化地区
→ 化工制造地区
→ 最终工业地区
```

➡️ **我推荐保留 `Fuel`、`Chemical Feedstock`、`Polymer` 三个节点。**

大致骨架：

```text
Crude Oil
   ├── Refining ──→ Fuel
   │
   └──────────────→ Chemical Feedstock
                         ↓
                      Polymer
```

具体是一个多输出 Refining Process，还是两个不同 Process，后面设计 Process 配方时再决定。

---

❓ **Q234** - **Timber 与 Natural Fiber 的加工品是否分别是 `Lumber` 和 `Textile`**：我建议：

```text
Timber
  ↓ Milling
Lumber

Natural Fiber
  ↓ Textile Processing
Textile
```

`Lumber` 进入建筑、家具、车体内部结构等。

`Textile` 进入衣物、乘员装备、软质内饰以及其他需要柔性材料的产品。

这里不继续拆：

```text
Plank
Plywood
Paper Pulp
Thread
Fabric
Leather
...
```

除非未来某项 Gameplay 证明需要。

➡️ **我推荐只保留 `Lumber` 和 `Textile` 两个宏观加工品。**

---

❓ **Q235** - **食品分支是否避免设计“Food Ingredient”这种万能中间品**：我们已经有：

```text
Grain
Produce
Animal Products
```

这三种本身就是宏观食材。

如果再制造：

```text
Grain → Food Ingredient
Produce → Food Ingredient
Animal Products → Food Ingredient
```

会立即把我们刻意保留下来的地域差异重新压平。

➡️ **我推荐不建立通用 `Food Ingredient`。**

三种 Raw Goods 可以直接参与不同食品 Process，例如以后可能出现：

```text
Grain
→ Staple Food

Grain + Animal Products
→ Prepared Food

Produce + Animal Products
→ Prepared Food
```

具体最终需要几个食品商品，我们以后单独设计食品分支；当前只确定**不会先汇聚成万能中间原料**。

---

❓ **Q236** - **第一批工业材料是否先冻结为这 10 种**：如果采用以上建议，我们会从 12 种 Raw Goods 向上得到一组很克制的基础加工品：

```text
Industrial Materials
├─ Structural Metal
├─ Conductive Metal
├─ Specialty Material
├─ Construction Material
├─ Glass
├─ Fuel
├─ Chemical Feedstock
├─ Polymer
├─ Lumber
└─ Textile
```

食品暂时另开分支，因此不强行塞进这张表。

它们不是“Tier 1”，也不是新的领域类型，只是目前 DAG 中已经证明有意义的一组 VoxelDefinition。生产网络依然只有图关系，没有等级。现有设计本身也已经明确所有这些可实体化商品继续共享 `VoxelDefinition`，而不是建立新的 Good/Material 类型体系。fileciteturn0file0L19-L27

➡️ **我推荐先冻结这 10 种作为第一批工业中间品。** 后续如果某个制造分支确实无法用它们表达，再通过 Gameplay Difference Test 新增，而不是现在提前铺满材料目录。

---

❓ **Q237** - **同一商品是否允许既是“中间品”又直接具有最终用途**：例如：

```text
Fuel
```

既是某些生产过程的 Input，也可能直接被列车设备消耗。

```text
Construction Material
```

既可以继续制造 Building Component，也可能直接被建筑 Construction 消耗。

如果我们把“Intermediate / Finished”做成互斥类别，就会产生不必要的限制。

➡️ **我推荐商品不存在互斥的生产阶段身份。**

一个 VoxelDefinition 可以同时：

```text
被消费
被交易
作为其他 Process 输入
用于 Construction
用于装备运行
```

所谓 Raw、Material、Component、Finished 等最多只是内容 Tag / UI 语义。

这也与已经确定的“没有 Tier，只依据 DAG”完全一致。

---

❓ **Q238** - **是否允许 Raw Good 跨过基础材料，直接参与高级 Process**：例如：

```text
Coal + Ferrous Ore
→ Structural Metal
```

或以后：

```text
Rare Mineral + Conductive Metal
→ Electronic Component
```

也就是说 Process 不需要遵循：

```text
Raw → Material → Component → Finished
```

这样的固定层级。

➡️ **我推荐明确允许跨层引用。**

DAG 唯一的结构约束仍然是**无环**；不存在“一个 Process 只能消费前一层产品”的规则。

这很重要，因为它能让一些商品拥有独特的长期战略价值，而不是加工一次以后原料就永远退出产业链。

---

本轮 Frontier 是 **Q229–Q238**。

如果这一轮落定，我们就会拥有第一张真正有内容的生产 DAG 骨架。下一轮 frontier 会自然推进到两个方向：一边设计**机械 / 电气 / 电子 / 化工等 Component 层**，另一边设计**食品、衣物等民用消费品分支**。这两个分支完成后，才有足够基础继续向列车设备、武器、无人机等顶层产物推进。

---

## 15. User  (2026-09-10 08:21:09 UTC)

Q229 同意

Q230 同意

Q231 同意

Q232 同意

Q233 同意

Q234 同意

Q235 同意

Q236 同意

Q237 同意

Q238 同意

---

## 16. Assistant  (2026-09-10 08:21:23 UTC)

> [思考摘要] 继续确定组件层：我会继续围绕机械、电气、电子、化学组件与民用品分类提问，同时保持现有材料体系、体素定义、生产链和装备建模约束不变，避免引入尚未决定的独立物品类型。 / 继续确定组件层

上一轮已经把 **12 种 Raw Goods → 10 种基础工业材料** 的第一段 DAG 固定下来。现在 frontier 正式进入两条并行分支：一条是**功能性零部件**，另一条是**民用消费品**。这两条都只依赖已经确定的材料层，因此可以在同一轮询问。

> [思考摘要] 细化组件分类：我会优先确认组件层的抽象边界：机械、电气、电子是否分开；精密组件和通用化学品是否值得独立存在；同时避免为每类产品增加重复的中间节点。 / 细化组件分类

❓ **Q239** - **零部件应按“功能”抽象，还是按具体机械结构拆分**：到了 Component 层，很容易开始出现轴承、齿轮、活塞、弹簧、电线、电容、电路板等大量现实零件。

这会迅速把宏观经济系统变成 BOM 模拟器。

➡️ **我推荐继续采用功能性抽象，第一批只设计真正能形成不同产业链的 Component。**

例如：

```text
Structural Metal ────────┐
Polymer ─────────────────┼→ Mechanical Components
                         │
Conductive Metal ────────┼→ Electrical Components
                         │
Conductive Metal ────────┤
Specialty Material ──────┼→ Electronic Components
Glass / Polymer ─────────┘
```

`Gear`、`Bearing`、`Wire`、`Circuit Board` 等默认不成为货物；它们只是我们对 `Mechanical Components` 等宏观商品的现实解释。

---

❓ **Q240** - **是否保留独立的 Mechanical Components**：我们可以让所有机械产品直接消耗 Structural Metal，也可以增加：

```text
Structural Metal
+ Polymer
→ Mechanical Components
```

然后发动机、武器机构、工业设备、无人机等大量高级产品共享这个中间品。

➡️ **我推荐保留 `Mechanical Components`。**

它的 Gameplay 意义很明显：它把“拥有矿和冶炼能力”与“拥有机械制造能力”区分开。

于是：

```text
矿业区
→ Structural Metal

机械工业区
→ Mechanical Components

车辆/装备工业区
→ Finished Equipment
```

可以成为三种不同经济角色。

---

❓ **Q241** - **Electrical 与 Electronic 是否值得拆成两种 Component**：现实上二者相关，但 Gameplay 上用途可以非常不同。

我建议：

```text
Electrical Components
```

代表电机、发电、配电、线圈、执行器等**动力电气系统**。

```text
Electronic Components
```

代表控制、计算、传感器、通信、瞄准、自动化等**信息与控制系统**。

➡️ **我推荐明确拆分。**

这样未来：

```text
Traction Motor
Generator
Power Equipment
→ 强依赖 Electrical Components

Drone
Sensor
Fire Control
Advanced Train Equipment
→ 强依赖 Electronic Components
```

高科技产业也因此不会只是“多加一点 Rare Mineral”。

---

❓ **Q242** - **是否再增加 `Precision Components` / `Advanced Components`**：一个看似自然的继续细分是：

```text
Mechanical Components
+ Specialty Material
→ Precision Components
```

供高级引擎、精密武器、无人机等使用。

➡️ **我暂时推荐不加。**

如果某件高级设备需要更高工业门槛，可以直接：

```text
Mechanical Components
+ Specialty Material
+ Electronic Components
→ Advanced Equipment
```

这已经表达了高端制造。

只有当以后发现大量高级产品重复依赖完全相同的这组输入，并且“精密零件”本身能够成为有意义的独立贸易品时，再抽象 `Precision Components`。

这正是我们一直采用的“重复需求出现后再抽象”，而不是预先铺层。

---

❓ **Q243** - **是否需要一个泛化的 `Industrial Chemicals` 中间品**：现在已经有：

```text
Crude Oil
→ Chemical Feedstock
→ Polymer
```

还可以继续加：

```text
Chemical Feedstock
→ Industrial Chemicals
→ Explosives / Medicine / ...
```

但 `Industrial Chemicals` 很可能只是一个没有明显 Gameplay 差异的中间转手节点。

➡️ **我推荐暂时不增加。**

不同化工产品以后可以直接从 `Chemical Feedstock` 与其他材料生产。

例如：

```text
Chemical Feedstock + Textile
→ Medical Supplies

Chemical Feedstock + ...
→ Explosives
```

是否真的需要这些成品，要等相应 Gameplay 分支出现后再决定。

---

❓ **Q244** - **第一批工业 Component 是否冻结为三种**：

如果 Q239–Q243 按推荐落定，第一批 Component 就非常克制：

```text
Mechanical Components
Electrical Components
Electronic Components
```

这三者全部仍然只是普通、可库存、可交易的 `VoxelDefinition`，没有新的 ComponentDefinition 类型。现有统一 Definition 架构继续保持。fileciteturn0file0L19-L27

➡️ **我推荐先冻结这三种。**

以后只有出现明确 Gameplay 缺口，才增加新的 Component。

---

❓ **Q245** - **基础食品是否需要加工成一个 `Staple Food` 商品**：`Grain` 可以直接贸易，但它与真正供人口日常消费的食品仍然可以有经济上的区别：

```text
Grain
→ Staple Food
```

这里的 `Staple Food` 可以抽象面包、面食、米饭、基础加工食品等。

➡️ **我推荐保留 `Staple Food`。**

这会产生一个非常清晰的农业加工链：

```text
农业地区
→ Grain

食品加工地区
→ Staple Food

人口中心
→ 大量消费
```

相比直接让城市无限吃 Grain，它多出了一层有意义的贸易和工业选择。

---

❓ **Q246** - **是否还需要一个高价值 `Prepared Food`**：我们可以进一步设计：

```text
Grain
+ Produce
+ Animal Products
→ Prepared Food
```

它抽象餐食、肉制品、综合加工食品等。

这样食品经济形成：

```text
Raw Food
├→ 直接消费 / 贸易
├→ Staple Food
└→ Prepared Food
```

其中 `Prepared Food` 原料更多、价值更高，可以主要由大型人口中心、富裕聚居地、乘员补给等消费。

➡️ **我推荐保留。**

因为它不是为了“多一级加工”，而是天然把三种农业 Raw Goods 汇聚起来，形成一个明显的跨地区供应链。

我暂时不建议再拆 Luxury Food、Canned Food、Beverage 等。

---

❓ **Q247** - **Produce 与 Animal Products 是否允许直接消费**：即使存在 Prepared Food，也没有必要规定所有食品必须加工后才能被人口使用。

➡️ **我推荐允许 Raw Food 本身就具有最终用途。**

例如：

```text
Grain
Produce
Animal Products
Staple Food
Prepared Food
```

全部可以进入人口消费需求，只是需求权重、价值和生产方式不同。

这样也延续 Q237 的原则：**“中间品”和“最终用途”不是互斥身份。**

---

❓ **Q248** - **纺织分支是否只增加 `Clothing` 一个民用成品**：

目前已经有：

```text
Natural Fiber
→ Textile
```

下一步最明显的是：

```text
Textile
→ Clothing
```

没有必要继续细分 Shirt、Shoes、Uniform 等。

➡️ **我推荐保留 `Clothing`。**

以后乘员装备如果需要防护服、战斗服等，那些属于具有具体 Gameplay 能力的装备，不与普通 `Clothing` 混为一谈。

---

❓ **Q249** - **是否需要一个宏观的 `Consumer Goods`**：除了吃穿，一个聚居地还应该消费大量家具、厨具、工具、日用品、小型器具等。

如果全部逐项建模显然不可接受。

一种很有效的宏观抽象是：

```text
Lumber
+ Polymer
+ Structural Metal
+ Glass
→ Consumer Goods
```

它代表广泛的普通生活用品。

➡️ **我推荐加入 `Consumer Goods`。**

它有很强的经济价值：能够给 Lumber、Polymer、Glass、Structural Metal 建立一个持续的民用需求出口，而不是让所有工业材料最终都只为了列车和武器服务。

---

❓ **Q250** - **是否现在加入 `Consumer Electronics`**：

既然我们已经可能有：

```text
Electronic Components
```

自然可以形成：

```text
Electronic Components
+ Polymer
+ Glass
→ Consumer Electronics
```

它代表家电、个人电子设备、通信终端等高价值民用品。

这会让高级电子工业不仅服务玩家装备，也进入普通市场。

➡️ **我推荐加入。**

于是会出现一个很好的产业纵深：

```text
Conductive Ore
+ Rare Mineral
        ↓
Electronic Components
        ↓
├→ 玩家高级设备
├→ Drone 等工业产品
└→ Consumer Electronics
```

高科技聚居地由此可以拥有真正不同于普通工业城的出口结构。

---

❓ **Q251** - **是否现在加入 Medical Supplies**：药品、医疗耗材显然可以成为商品，但当前我们还没有设计人口健康、疾病、乘员治疗等 Gameplay。

➡️ **我推荐暂时不加入。**

这正符合 Q202/Q216 的原则：

> 现实中重要 ≠ 当前就值得成为游戏商品。

等健康、事故、疾病或乘员医疗成为正式 Gameplay 后，再判断 `Medical Supplies` 是否值得拥有独立需求与生产链。

---

❓ **Q252** - **Composite 建造是否直接消耗这些材料和 Components，而不再增加“建筑部件”中间层**：例如建造工厂可以直接需要：

```text
Construction Material
Structural Metal
Mechanical Components
Electrical Components
```

而不是先生产：

```text
Wall Panel
Beam
Pipe
Construction Parts
Factory Parts
```

再制造建筑。

➡️ **我推荐直接消耗现有 Goods。**

Composite Construction 本来就是整个普通商品 DAG 的出口之一；没有必要为了建筑专门增加一套建筑 BOM 商品层。现有 Composite 仍然由 Voxel 构成，并通过显式 Construction 创建。fileciteturn0file5L11-L17 fileciteturn0file5L21-L29

具体某个建筑需要多少、哪些 Goods，以后由其制造定义决定。

---

如果全部采用，这一轮会新增 **3 个工业 Component + 5 个民用成品**：

```text
Components
├─ Mechanical Components
├─ Electrical Components
└─ Electronic Components

Civilian Goods
├─ Staple Food
├─ Prepared Food
├─ Clothing
├─ Consumer Goods
└─ Consumer Electronics
```

当前 DAG 的中段就开始具有真正的经济结构：

```text
Raw Goods
   ↓
Industrial Materials
   ↓
Components
   ↓
├─ Civilian Finished Goods
├─ Equipment
└─ Composite Construction
```

请继续回答 **Q239–Q252**。之后 frontier 就会推进到**工业设备、无人机、消耗品，以及列车/乘员顶层装备的分类边界**；那时我们会第一次真正接触你最初提出的“列车部件、列车武器、列车设备、乘员武器、乘员设备”。

---

## 17. User  (2026-09-10 11:16:17 UTC)

Q239 同意，各类零部件将主要用于制作武器，设备等，还有复合体（例如列车，建筑等）&#x20;

Q240 同意

Q241 同意拆分

Q242 同意暂时不添加

Q243 同意

Q244 同意

Q245 同意保留

Q246 同意

Q247 同意

Q248 保留

Q249 同意

Q250 加入

Q251 加入，乘员必然需要

Q252 同意直接消耗现有 Goods
一些额外对于复合体的说明：首先对于列车：列车会有手工设计的预置型号，同时允许玩家完全自主设计。即用户可以自由的使用各种体素块（可以用于建设列车的体素块，例如引擎，窗户，门等）来设计自己的列车，这进一步涉及到了我们后续要设计的列车系统。（聚居地系统也是类似的，但聚居地系统只涉及建筑物，装饰物的摆放，没有火车那么细的粒度）因此我认为对于复合体来说，其生成的实际消耗应当是构成其的所有体素所需的材料的总和，例如假设一个引擎由一些二级，三级产物组成，那么你必须有这些产物（你不能直接从原料拼接）

---

## 18. Assistant  (2026-09-10 11:16:31 UTC)

> [思考摘要] 澄清建造语义：我需要修正 Q252：不能只问原料总量。建造应校验设计展开后的具体体素商品库存；若引擎体素由二、三级产品组成，就必须持有这些成品，不能用更底层原料直接替代。 / 澄清了建造语义

你补充的 Composite 说明会修改我对 Q252 的表述，但不推翻此前结构。关键区别是：**Composite Construction 不是“拿若干泛化材料直接换一辆车/一栋楼”，而是必须尊重设计中实际 Voxel 的制造层级。** 例如 Engine Voxel 若本身需要 Mechanical Components、Electrical Components 等先制造出来，就不能拿 Ferrous Ore 直接在整车 Construction 时越级折算。当前文档本来也已经确定玩家可自由设计车头/车厢内部和 Voxel 布局，制造流程是 Blueprint → Validation → Instantiate。

因此下一轮要先把 **Voxel 商品制造 → Composite 装配** 的 BOM 语义钉死，再继续顶层装备分类；否则“引擎、门、窗、武器到底是库存货物还是施工配方中的虚拟部件”会影响后面所有设计。

> [思考摘要] 梳理当前前沿：我正在整理所有已具备前置条件的问题：民用物资新增医疗用品；接下来应优先确定复合体的体素库存校验、拆解回收、结构体素商品化，以及顶层产品分类。

你对 Q252 的补充很关键。我先把已经明确的一点修正为：

> **Composite 的制造成本不能绕过其内部 Voxel 的制造层级。**
>
> 设计里用了 Engine、Window、Door 等 Voxel，就必须尊重这些 Voxel 本身在生产 DAG 中的位置；不能因为最终目标是“一辆列车”，就把整个设计递归压平成若干 Raw Goods 后直接制造。

这和现有“玩家可以自由设计车辆 Voxel 布局、合法 Definition 才能制造”的规则完全兼容。fileciteturn0file5L39-L44

这一层有一个根决策会决定后面所有 Construction/BOM 问题，所以按照 design tree，本轮 frontier 很窄。

---

❓ **Q253** - **Composite Construction 到底消费“成品 Voxel”还是把它们的配方展开后消费材料**：

假设玩家设计了一辆车：

```text
Locomotive Blueprint
├─ 80 × Hull Voxel
├─ 12 × Window Voxel
├─ 4 × Door Voxel
├─ 2 × Engine Voxel
└─ 6 × Electrical Equipment Voxel
```

而：

```text
Engine
= Mechanical Components
+ Electrical Components
+ Structural Metal
```

这里存在两种本质不同的制造语义。

### A. 消费设计中实际需要的成品 Voxel

玩家必须先生产或购买：

```text
80 Hull
12 Window
4 Door
2 Engine
6 Electrical Equipment
```

然后执行：

```text
Goods Inventory
        ↓ Construction
CompositeInstance
```

Engine 的 Component 配方只在**制造 Engine**时发生。

因此不能拿：

```text
Mechanical Components
+ Electrical Components
+ Structural Metal
```

直接代替库存里缺失的 Engine。

### B. Composite Construction 自动展开 BOM

系统看到缺少 2 Engine 后，可以继续展开：

```text
2 Engine
→ X Mechanical Components
 + Y Electrical Components
 + Z Structural Metal
```

然后直接从这些材料制造整车。

➡️ **我推荐 A，而且建议把它作为硬规则。**

这最符合你刚才强调的：

> “你必须有这些产物，你不能直接从原料拼接。”

生产与装配因此拥有非常干净的边界：

```text
Production DAG

Raw Goods
   ↓
Materials
   ↓
Components
   ↓
Engine / Window / Door / Hull / Equipment
   ↓
库存中的标准化 Voxel Goods


Composite Construction

库存中的 Voxel Goods
   +
Blueprint / Design
   ↓
CompositeInstance
```

这也会产生更好的经济玩法，因为 **Engine 本身就是有价值的工业商品**。玩家可以选择：

```text
买 Engine
```

或者：

```text
买 Mechanical Components
→ 自己生产 Engine
```

或者更进一步：

```text
买矿
→ 冶炼
→ 零部件
→ Engine
```

每一级都对应不同资本投入、产业能力和贸易策略，而不是最终 Construction 自动替玩家把整条产业链折叠掉。

---

❓ **Q254** - **列车设计与聚居地设计是否采用“同一 Construction 规则、不同设计粒度”**：

你补充说两者类似，但聚居地不会像列车一样让玩家逐 Voxel 设计整个聚居地。

我理解为：

```text
列车
└─ 玩家可以编辑单个车辆 Composite
   └─ Voxel-level design
      Engine / Hull / Window / Door / Weapon / ...

聚居地
└─ 玩家编辑 Settlement Layout
   ├─ Building Composite A
   ├─ Building Composite B
   ├─ Decoration
   └─ ...
```

也就是说，聚居地层面的玩家操作主要是：

> **“把哪些建筑放在哪里”**

而不是：

> **“这栋工厂第 37 格墙体用什么 Voxel”**

但 Building Composite 本身在底层依旧是一组真实 Voxel，仍然遵守同一套制造成本和 Construction 规则。

➡️ **我推荐正式采用这个边界。**

因此不存在：

```text
Train Construction System
Building Construction System
```

两套不同的物质规则。

而是：

```text
               Composite Construction
                         │
             ┌───────────┴───────────┐
             │                       │
        Vehicle Composite       Building Composite
             │                       │
     玩家可细粒度设计          主要使用预置设计
```

建筑以后即使开放更细的自定义，也不需要修改资源系统，只是开放新的 Blueprint 编辑能力。

这也符合当前文档：建筑本身已经是 Composite，而聚居地不是 Composite，聚居地持有实际 Building Instance。fileciteturn0file5L27-L29 fileciteturn0file9L55-L66

---

❓ **Q255** - **医疗用品现在正式加入民用商品集合吗**：

你修正了 Q251：乘员必然需要，因此 `Medical Supplies` 已经有明确的未来 Gameplay 消费端，不再属于“没有需求系统支撑的现实主义噪声”。

➡️ **我推荐正式加入。**

当前民用/人员消费品因此从 5 种变成 **6 种**：

```text
Staple Food
Prepared Food
Clothing
Consumer Goods
Consumer Electronics
Medical Supplies
```

但本轮只决定节点存在；`Medical Supplies` 到底由哪些材料生产，需要等化工/装备分支继续向外展开时再决定，避免现在反推不存在的材料节点。

---

因此这一轮只需要回答 **Q253–Q255**。

其中真正关键的是 **Q253**。一旦确定，我下一轮才能继续问它的直接下游：**Composite BOM 如何从 Voxel Layout 派生、Construction 是否原子消费库存、拆下来的 Voxel 如何回库存，以及 Structural Metal / Hull / Window / Door 之间到底是什么关系。**

---

## 19. User  (2026-09-10 11:21:36 UTC)

Q253 A 消费成品

Q254 你的理解正确，采用

Q255 同意

---

## 20. Assistant  (2026-09-10 11:21:53 UTC)

> [思考摘要] 继续确定建造规则：我正在把剩余前置问题收拢：明确建筑布局如何生成物料清单、构件如何作为库存商品生产，以及船体、墙体、窗户和门之间的关系。 / 继续确定建造规则

Q253–Q255 把 **Production 与 Construction 的边界**彻底定下来了：生产负责制造可交易的成品 Voxel Goods；Construction 只消费这些已经存在的成品，按设计装配成 Composite，不递归展开上游配方。

下一层 frontier 因此集中在 **Composite BOM、装配事务，以及拆装后的物品身份**。这些决定会直接约束列车设计、建筑制造和之后的 Refit。

> [思考摘要] 确定体素商品化：我正在建议：布局中的每个实际体素都对应一种可生产、可交易、可库存的成品；建造前精确校验并一次性扣除，拆除后按耐久与运行状态返还库存。 / 确定了体素商品化方案

当前正式设计已经给我们两个直接约束：库存 Quantity 不等于空间中的 Voxel Instance；而 Refit 移动已有零件时必须保留真实 Voxel Identity 和 Runtime State。fileciteturn0file0L35-L38 fileciteturn0file5L46-L50 因此现在可以把“库存商品 ↔ Composite 内实际体素”的转换规则设计完整。

---

❓ **Q256** - **Composite BOM 是否完全从最终 Voxel Layout 派生**：假设一个设计实际包含：

```text
Locomotive
├─ Structural Block × 120
├─ Window × 16
├─ Door × 4
├─ Engine × 2
├─ Electrical Equipment × 8
└─ Weapon × 1
```

是否直接得到：

```text
BOM =
120 Structural Block
16 Window
4 Door
2 Engine
8 Electrical Equipment
1 Weapon
```

而不允许 CompositeDefinition 再单独维护：

```text
"construction_cost": {...}
```

这种第二份成本清单？

➡️ **我推荐严格从实际 Voxel Layout 派生 BOM。**

即：

> **Composite 中实际存在什么，就必须消费什么。**

Rail Contact、尺寸档位、Archetype、连接点等非 Voxel 元数据当然不产生物料成本。

这样不存在“模型改了但 construction_cost 忘记同步”的双重事实来源。

---

❓ **Q257** - **每一个可用于 Composite 的 Voxel 是否首先都是一个真正的成品货物**：你刚才举出了 Engine、Window、Door 等例子。这里建议正式建立一个非常强的原则：

```text
Production DAG
    ↓
Structural Block
Window
Door
Engine
Weapon
Equipment
...
    ↓
Inventory
    ↓
Composite Construction
```

也就是说，**玩家不能在设计器中放入一个只有建筑意义、但不存在于资源经济中的“虚拟部件”**。

➡️ **我推荐同意。**

凡是 Composite 中真实存在、可拆卸、可破坏的 Voxel，都对应正常 `VoxelDefinition`，原则上也可以制造、库存和交易。

这正好延续“一切皆体素”和统一 VoxelDefinition 的现有规则。fileciteturn0file2L26-L30

---

❓ **Q258** - **车体/建筑基础结构是否也需要经过“材料 → 成品结构体素”这一步**：

这一点是 Q253 的直接推论。

例如我们已经有：

```text
Structural Metal
Construction Material
Glass
```

但这些是工业材料，不应该直接等于列车中的车壳、建筑中的墙、窗户。

我建议存在类似：

```text
Structural Metal
    ↓
Structural Block

Construction Material
    ↓
Building Block

Glass + Structural Metal
    ↓
Window

Structural Metal + Mechanical Components
    ↓
Door
```

然后 Composite 才消费这些成品 Voxel。

否则会出现很奇怪的情况：

> Engine 必须先制造成 Engine，但一块车壳却可以直接拿 Structural Metal 塞进列车设计。

➡️ **我推荐明确要求所有实际结构 Voxel 都是成品货物。**

但粒度继续遵守 Gameplay Difference Test：不需要 `Left Wall Panel`、`Right Door Hinge` 等细分。

具体第一批结构 Voxel 有哪些，等这个原则确定后下一轮再列。

---

❓ **Q259** - **车辆与建筑是否尽量复用同一种结构部件，而不是人为分成 Vehicle/Building 两套材料**：

例如是否应该存在：

```text
Vehicle Window
Building Window

Vehicle Door
Building Door
```

还是只存在：

```text
Window
Door
```

只有当它们在耐久、Behavior、尺寸或 Gameplay 上确实不同才拆分？

➡️ **我推荐后一种。**

默认：

> **相同 Gameplay 功能 → 相同 VoxelDefinition。**

不要因为“这个放在火车上、那个放在建筑里”就自动拆成两种货物。

但像：

```text
Hull Block
vs
Building Block
```

如果它们真的具有明显不同的耐久、质量、车辆设计用途或生产成本，则可以通过 Gameplay Difference Test 合理拆开。

---

❓ **Q260** - **Construction 是否必须是原子事务**：例如造车需要 151 个货物，但玩家只缺一个 Window。

是否允许先扣掉其他 150 个，再告诉玩家失败？

➡️ **我推荐绝对不允许。**

流程应该是：

```text
Blueprint / Definition
        ↓
Construction Validation
        ↓
Derive exact BOM
        ↓
Check all required goods
        ↓
[全部满足]
Atomic Consume + Instantiate
```

任何失败：

```text
Inventory unchanged
World unchanged
No partial Composite
```

这与当前 Construction / Refit 等结构修改都要求 Transaction 原子结算的总原则一致。fileciteturn0file5L11-L17

---

❓ **Q261** - **新生产的 Voxel Good 在装配时如何变成 Voxel Instance**：

例如库存有：

```text
Engine × 5
```

这里并不存在五个空间 Engine Voxel。

当 Construction 消费一个 Engine 时才发生：

```text
Inventory Good ×1
        ↓ materialize
VoxelInstance
├─ DefinitionId = Engine
├─ CurrentDurability = MaxDurability
├─ initialized Behavior State
└─ RuntimeId（如果该类型需要）
```

➡️ **我推荐采用这个明确的 materialization 边界。**

也就是说：

> **制造 Engine 商品 ≠ 在世界中生成 Engine Voxel。**

只有 Placement / Construction 等显式行为才把库存单位实体化。

这与当前 Inventory Quantity 和空间 Instance 的区分完全一致。fileciteturn0file0L35-L38

---

❓ **Q262** - **拆下来的已使用零件如何进入库存**：

这是反方向最重要的问题。

假设列车上的 Engine：

```text
Durability = 72 / 100
Behavior State = ...
RuntimeId = #812
```

玩家把它拆下来。

如果直接变成：

```text
Engine Stack +1
```

其耐久和状态就消失了。

当前正式设计已经预留了这种情况：普通无特殊 Runtime State 的 Voxel 可以变回 Stack；复杂设备未来需要独立 Item Instance。fileciteturn0file0L35-L38

➡️ **我推荐正式采用两种库存形态：**

```text
Stack
VoxelDefinitionId + Quantity
```

用于普通、完全可互换的货物。

以及：

```text
ItemInstance
DefinitionId
Durability
Runtime State
Identity...
```

用于已经拥有个体状态的设备。

因此：

```text
全新的普通 Structural Block
→ Stack

使用过且受损的 Engine
→ ItemInstance
```

而不是为了库存方便清除状态。

---

❓ **Q263** - **Construction 是否允许安装二手/受损 ItemInstance**：

例如玩家购买或者拆下一台：

```text
Engine
Durability = 45%
```

能否把它装到另一辆车上？

➡️ **我推荐允许。**

Construction 只要求：

> 这是所需 Definition 的一个有效实体化单位。

它不应该自动把二手 Engine 修复成新品。

因此玩家可以：

```text
低价买二手引擎
→ 装入新车
→ 新车制造成功
→ 但 Operational Condition 可能较差
```

这和现有的 **Construction Validity / Operational Condition 分离**尤其契合：一个设计可以结构合法，但实际安装的部件状态让车辆处于 Degraded 状态。fileciteturn0file5L31-L37

这还会为以后非常自然地产生二手设备市场。

---

❓ **Q264** - **“正常拆解”和“Recovery”是否严格区分**：

现在已经有独立的 Recovery DAG，所以必须避免两个系统重叠。

我建议：

### Disassembly / Refit

完整拆下一个仍存在的 Voxel：

```text
Composite Engine
→ Engine ItemInstance
```

它还是原来的 Engine，没有发生资源转换。

### Recovery

主动报废或处理物品：

```text
Damaged Engine
    ↓ Recycling
Structural Metal
+ Mechanical Components
+ ...
```

这才进入 Recovery DAG，并且通常存在损耗。

➡️ **我推荐严格区分。**

这样：

> **拆卸是 Ownership / Inventory Transfer；回收才是生产意义上的物质转换。**

不会出现“从列车拆一个发动机就自动分解成钢和零件”的怪异规则。

---

❓ **Q265** - **预置型号与玩家自定义设计是否严格使用同一成本规则**：

例如官方预置：

```text
Core Freight Locomotive Mk.I
```

和玩家自己的：

```text
My Weird Train v37
```

是否完全一样：

```text
Layout
→ count actual Voxels
→ exact BOM
→ consume goods
→ instantiate
```

而官方预置绝不享受隐藏折扣或手写成本？

➡️ **我推荐严格统一。**

预置型号只是官方提供的 Blueprint / Definition；它不是特殊制造系统。

这样玩家可以拿官方型号修改几格之后，成本自然随实际 Voxel 变化，不需要维护另一套公式。

---

❓ **Q266** - **Construction 是否禁止使用“全局魔法库存”**：

这是资源系统与核心贸易玩法非常关键的一条。

假设玩家在 A 城拥有 Structural Block，在 500 公里外的 B 城准备造车。如果存在全局 Player Inventory，那么运输这些货物根本没有意义。

➡️ **我推荐明确禁止全局物质库存。**

货物必须存在于某个具体 Inventory / Storage Context，例如未来可能有：

```text
Settlement Storage
Train Cargo
Production Facility Storage
Construction Site Storage
```

Construction 只能消费**施工位置实际可访问的库存**。

首版具体怎样定义“可访问”——整个聚居地共享仓储，还是需要内部物流——可以以后单独设计；但现在应先冻结：

> **物质货物具有地点，不能跨世界瞬移消费。**

这条对你的贸易游戏尤其重要，因为否则玩家“低价收购原料自己建设”的物流部分会被直接绕过。

---

本轮 Frontier 为 **Q256–Q266**。

其中 Q258 很重要：如果你同意，那么我们下一轮就可以正式设计第一组 **Composite Construction Goods**，例如 `Structural Block / Building Block / Window / Door / Storage Module...`；而 Q262–Q264 会决定这些东西在列车改装、二手零件和回收经济中的完整生命周期。

---

## 21. User  (2026-09-10 11:41:59 UTC)

Q256 同意

Q257 是的，同意

Q258 是的

Q259 是的，应当复用

Q260 同意，应当是原子事务

Q261 同意，即将库存和在世界中存在相区分

Q262 同意

Q263 允许

Q264 同意，一个是拆卸，一个是分解回收

Q265 严格统一

Q266 同意，禁止全局魔法库存，玩家只能使用列车上和所处的自己聚居地的库存

---

## 22. Assistant  (2026-09-10 11:42:14 UTC)

> [思考摘要] 界定库存范围：我正在锁定一个关键边界：建造时，系统是把当前聚落库存视为共享仓，还是要求材料位于具体建筑、仓库或施工点？这会直接影响库存扣除、运输、权限与失败回滚规则。 / 界定了库存范围

Q256–Q266 已把 **Good → Inventory → Voxel Instance → Composite** 的生命周期闭合了。尤其 Q266 很重要：物质库存具有地点，不能瞬移；玩家只能动用**当前列车库存**与**当前所在、归玩家所有的聚居地库存**。

现在 frontier 有两支已经解锁：一支是“聚居地库存到底怎样抽象”，另一支是“第一批可用于实际搭建 Composite 的结构类 Voxel Goods 应该有哪些”。

> [思考摘要] 厘清库存语义：267. 聚落库存是否是共享池，还是必须存放在具体仓库或建筑中？

268. 建造时，停靠聚落的列车货物与聚落库存能否合并使用？

269. 聚落库存容量是否由仓储建筑提供，而列车容量是否由车厢模块提供？

270. 生产设施的输入输出是否统一读写所属聚落的共享库存？ / 厘清库存语义

Q256–Q266 之后，Construction 的基本语义已经很清楚：Blueprint 的实际 Voxel Layout 是 BOM 唯一事实来源；库存中的货物只有在装配时才实体化；拆卸与 Recovery 分离；预置与玩家设计完全使用同一规则。

这一轮 frontier 可以同时处理**聚居地库存的基本抽象**与**第一批结构/建造类成品 Voxel Goods**。

---

❓ **Q267** - **聚居地库存是统一库存池还是逐建筑库存**：Q266 已经确定货物不能跨地点瞬移，但“同一个聚居地内部”还存在两种粒度。

A：

```text
Settlement Inventory
├─ Structural Metal × 500
├─ Engine × 4
└─ Food × 2000
```

所有属于该聚居地的生产设施、Construction 和交易系统共享这一地点库存。

B：

```text
Warehouse A Inventory
Factory A Inventory
Station Inventory
Warehouse B Inventory
...
```

货物还必须在聚居地内部搬运。

➡️ **我推荐 A：聚居地拥有一个地点绑定的统一逻辑库存。**

本游戏核心是跨聚居地贸易，而不是厂内物流。要求玩家把 Metal 从聚居地东边仓库运到西边工厂，对当前玩法主要是噪声。

因此我们可以保持：

```text
World-level logistics:
Settlement A ↔ Train ↔ Settlement B
```

而不是继续展开：

```text
Warehouse → Truck → Factory → Warehouse → Station
```

实际建筑仍然是 Composite、生产能力仍然来自真实 Building Instance；只是聚居地内部物流被宏观化。现有设计已经确定聚居地记录实际 Building Instance，而不是保存另一套虚构建筑统计。fileciteturn0file9L55-L66

---

❓ **Q268** - **Composite BOM 是否要求精确的 VoxelDefinitionId，不允许隐式材料替代**：假设 Blueprint 指定：

```text
Engine × 2
Window × 12
Structural Block × 100
```

而库存没有 `Engine`，但拥有大量制造 Engine 所需的 Component，这已经由 Q253 明确不能自动展开。

类似地，如果以后有：

```text
Basic Engine
Advanced Engine
```

系统是否允许因为二者都带有 `engine` Tag，就自动拿 Advanced Engine 顶替 Basic Engine？

➡️ **我推荐严格要求 Blueprint 中指定的确切 VoxelDefinitionId。**

Tag 不承担隐式替代语义。

玩家想换另一种 Engine，就应该修改 Blueprint / Refit：

```text
Basic Engine
    ↓ explicit design change
Advanced Engine
```

这样最终 Composite 中“究竟安装了什么”永远明确，不出现系统私自替换零件。

---

❓ **Q269** - **基础结构 Voxel 是否应拆成 `Structural Block` 与 `Construction Block` 两类**：这两类虽然都叫“结构材料”，但我认为已经通过 Gameplay Difference Test。

建议：

```text
Structural Metal
    ↓
Structural Block
```

代表金属框架、车壳、机械结构、工业建筑钢结构等。

以及：

```text
Construction Material
    ↓
Construction Block
```

代表混凝土、砖石等重型静态建筑结构。

二者不是“Vehicle Block / Building Block”的人为领域分割，因为 Building 也可以使用 Structural Block；真正区别是材料性能：

```text
Structural Block
较轻 / 机械结构 / 车辆友好

Construction Block
较重 / 廉价 / 静态建筑友好
```

➡️ **我推荐保留这两个成品 Voxel Goods。**

这样建筑可以混用：

```text
Factory
├─ Construction Block
├─ Structural Block
├─ Window
└─ Door
```

而车辆大部分使用 Structural Block，并不会被人为禁止使用其他合法 Voxel。

---

❓ **Q270** - **`Window` 与 `Door` 是否正式成为通用可交易 Voxel Goods**：你已经多次以它们作为玩家设计列车的例子，它们也同样适用于建筑。

我建议：

```text
Glass + Structural Metal
→ Window
```

以及：

```text
Structural Metal
+ Mechanical Components
→ Door
```

具体配方以后再定。

关键是只存在通用：

```text
Window
Door
```

而不是：

```text
Train Window
Building Window
Train Door
Building Door
```

除非未来确实出现例如密封舱门、装甲门等具有明显不同 Gameplay 的特殊版本。

➡️ **我推荐正式加入 `Window` 和 `Door`。**

它们不仅是装饰，还具有空间设计、碰撞、可能的通行 Behavior 等明确玩法意义。

---

❓ **Q271** - **是否增加独立的 `Armor Block`**：如果只存在 Structural Block，那么玩家想强化列车防护，通常只能“堆更多结构体素”。

但列车武器和战斗既然已经是长期明确方向，一个专门的：

```text
Armor Block
```

可以形成很直观的设计取舍：

```text
更重
更贵
更高 Durability / 防护能力
```

并可能需要：

```text
Structural Metal
+ Specialty Material
→ Armor Block
```

➡️ **我推荐加入。**

它与 Structural Block 有明确 Gameplay Difference，不只是一个现实材料名称。

这样以后车辆设计天然具有：

```text
轻量结构
vs
重装防护
```

的取舍，而不必立即引入复杂 Material/Damage Type 系统。

---

❓ **Q272** - **纯装饰差异是否禁止膨胀成不同资源商品**：玩家会高度自定义列车和建筑外观，因此这个问题迟早会出现。

例如完全相同的结构块：

```text
Red Structural Block
Blue Structural Block
Black Structural Block
Industrial Structural Block
Luxury Structural Block
```

如果全部成为独立货物，经济 DAG 会被纯视觉变体污染。

➡️ **我推荐确立原则：纯表现差异本身不足以创建新的经济商品身份。**

即：

> 两个 Voxel 如果 Gameplay 属性、Behavior 和制造意义完全相同，仅颜色/纹理/外观不同，不应因为视觉差异自动成为两种资源商品。

具体将来如何支持玩家涂装、材质选择、装饰变体，属于 Presentation / Customization 系统，后续另行设计。

这也符合现有 Definition 体系已经将 Semantic Revision 与纯表现 Asset Revision 分离的思想。fileciteturn0file0L42-L55

---

❓ **Q273** - **Rail 是否正式进入生产 DAG，成为普通可交易成品 Voxel Good**：铁路本身已经是实际的 `core:rail` Voxel；当前铁路设计也明确 Rail Definition 描述轨道种类，而直线、曲线、坡道不是不同 Definition。fileciteturn0file6L23-L32

按照现在的资源规则，它显然满足：

- 物质性；
- 可库存；
- 可实体化；
- 可以合理交易；
- 玩家未来铺轨时需要真实消耗。

➡️ **我推荐正式将 `Rail` 纳入 Production DAG。**

例如概念上：

```text
Structural Metal
    ↓
Rail
```

未来如果真的加入 Heavy Rail、High-speed Rail，它们可以是不同成品；但：

```text
Straight Rail
Curve Rail
Slope Rail
```

绝不能成为不同商品，因为这些只是实例的空间拓扑。

---

❓ **Q274** - **Decoration Voxel 是否仍然必须是正常商品，但只设计有实际需求的少量类型**：现有“一切皆体素”意味着 Composite 中真正存在的装饰体素也不能是设计器里的虚构对象。fileciteturn0file2L26-L30

但如果我们为桌子、椅子、灯具、柜子、管道、按钮等各建一种资源商品，很快又会失控。

➡️ **我推荐两条规则同时成立：**

第一，真正占据 Voxel、参与 Composite 的 Decoration 依然必须对应正常 VoxelDefinition，并遵守 Q257 的库存/制造规则。

第二，**当前资源设计不预先枚举 Decoration Goods**。只有具体的列车内部设计、建筑设计或 Gameplay 需要某类装饰时才添加。

例如未来可以发现 `Light Fixture` 确实承担照明 Behavior，那么它值得成为 Goods；纯视觉的小摆件则未必需要成为独立 Gameplay Voxel。

---

❓ **Q275** - **第一批 Construction Goods 是否先冻结为 6 种**：如果采用上述建议，那么当前可以先得到：

```text
Composite Construction Goods
├─ Structural Block
├─ Construction Block
├─ Armor Block
├─ Window
├─ Door
└─ Rail
```

它们不是新的领域类型，而只是生产 DAG 中已经证明具有独立 Gameplay 意义的普通 `VoxelDefinition`。

之后：

```text
Engine
Weapon
Storage
Power Equipment
Sensor
...
```

会从“结构类”之外继续扩展成真正的功能设备 Voxel。

➡️ **我推荐先冻结这 6 种。**

不要现在把座椅、车灯、楼梯、管道等一口气全部加入；等列车系统和建筑系统真正需要时再扩。

---

请继续回答 **Q267–Q275**。

这一轮如果落定，下一轮会同时解锁两个重要方向：一是**本地库存容量与 Storage Capability**，二是**列车/建筑中的功能设备 Voxel**。后者会开始正式进入 `Engine、Storage、Power、Control、Sensor、Weapon` 等你最初所说的顶层产物体系。

---

## 23. User  (2026-09-10 11:50:40 UTC)

Q267 A

Q268 严格要求 Blueprint 中指定的确切 VoxelDefinitionId

Q269 同意

Q270 首版先不加入

Q271 首版先不加入，之后版本再按需增加变体

Q272 同意，颜色作为一种属性即可，玩家可以自行修改涂装颜色

Q273 同意

Q274 同意，如之后需要再按需添加

Q275 如之前的回答，只需三种Structural Block  Construction Block Rail

---

## 24. Assistant  (2026-09-10 11:51:08 UTC)

> [思考摘要] 确定容量模型：我正在判断货运容量应依据实际存储设施，并倾向于同时记录格数与质量上限：每件货物占用一个逻辑体积单位，但质量不同，因此列车既受空间也受载重约束。 / 确定了容量模型

这一轮的收缩是合理的：**首版 Construction Goods 只有 `Structural Block / Construction Block / Rail`**；Window、Door、Armor 都不预置，等列车/建筑实际设计证明有玩法需求再加。纯涂装也不产生新的商品身份。

接下来真正解锁的是两个直接分支：**库存容量从哪里来**，以及**哪些“功能性 Voxel 成品”值得进入生产 DAG**。

> [思考摘要] 梳理容量规则：我正在统一几个依赖：定居点容量来自实际储存设施，列车容量来自货运储存模块。库存按单位数量限制；货物质量另行累计，用于影响列车载重、动力和运输策略，而不直接替代格数容量。 / 梳理了容量规则

这一轮先不继续枚举 Engine/Weapon 等成品，因为它们会受到“功能到底来自设备 Voxel 还是 Building/Vehicle 类型本身”这个决定影响。先把当前 frontier 清掉。

---

❓ **Q276** - **涂装颜色属于实例表现状态，而不是商品身份吗**：Q272 你确认“颜色作为属性，玩家可以自行修改涂装颜色”。

为了不让 `Structural Block` 因颜色分裂成不同商品，我建议更精确地规定：

```text
VoxelDefinition = Structural Block

Voxel / Composite Presentation State
└─ PaintColor = ...
```

即同一块 Structural Block 可以在安装后被重新涂装；改变颜色不改变 `VoxelDefinitionId`、Value、生产配方或经济身份。

➡️ **推荐如此。** 颜色属于实例/Composite 的表现性自定义数据，而不是资源 DAG 中的属性。现有架构已经明确纯表现内容不属于 Definition 的玩法语义。fileciteturn0file0L42-L55

---

❓ **Q277** - **库存容量是否也必须来自实际存在的 Storage Capability**：Q267 已经确定一个聚居地内部使用统一逻辑库存池，但这不意味着库存必须无限。

可以定义：

```text
Settlement Inventory
Capacity =
Σ settlement-owned StorageCapability

Train Inventory
Capacity =
Σ train-owned StorageCapability
```

聚居地里的仓库 Building Composite、列车里的货舱/储存设备，本质上都通过其中实际存在的 Storage Voxel 提供容量。

这样：

```text
建造仓库 → 聚居地库存上限增加
货舱受损 → 可用储存能力下降
改装列车增加储存设备 → 运力增加
```

➡️ **我推荐采用统一的 StorageCapability 模型。**

不让 `Settlement` 或某个 Carriage Archetype 凭空拥有固定库存容量。现有架构已经把 `StorageBehavior → Cargo Capability` 作为 Behavior/Capability 模型的典型例子。fileciteturn0file5L97-L108

---

❓ **Q278** - **生产设施是否直接读写聚居地统一库存**：既然不做聚居地内部物流，那么一个属于该聚居地的工厂是否：

```text
Settlement Inventory
      │ inputs
      ▼
Production Facility
      │ outputs
      ▼
Settlement Inventory
```

例如：

```text
库存：
Ferrous Ore ×1000
Coal ×300

Smelter 运行
↓
库存：
Ferrous Ore 减少
Coal 减少
Structural Metal 增加
```

不存在 Factory Inventory、Output Buffer、Conveyor 等额外权威库存。

➡️ **我推荐同意。**

内部物流彻底宏观化；真正需要玩家规划的是**聚居地之间的物流**。

---

❓ **Q279** - **市场出售的货物是否就是聚居地实际库存中的货物**：现在生产已经会实际产生 Goods，因此后面的市场可以继续保留两套模型：

**A.**

```text
Settlement Inventory: Structural Metal ×500

Market Supply:
Structural Metal ×100
```

Market Supply 是另一套独立数量。

**B.**

市场只是对聚居地实际库存开放交易的一层经济规则：

```text
Settlement Inventory
        ↓ Market policy / availability
   Player can buy
```

买入后真实发生：

```text
Settlement Inventory -100
Train Inventory +100
```

➡️ **我推荐 B。**

否则生产系统刚刚认真制造出的实物，会立即在市场层重新变成另一套抽象“Supply Counter”。

可以以后设计“聚居地愿意拿多少库存出售”“战略储备”“价格”等市场规则，但**物质数量的事实来源只有实际库存**。

这会修改早期 MVP 中“Supply 独立恢复”的简化经济模型，但与你现在设计的完整生产经济明显更一致。

---

❓ **Q280** - **生产设施的能力是否由内部实际功能 Voxel 提供**：这是下一步功能商品设计最关键的问题。

例如一个工厂可以采用：

**A. Building 类型直接决定能力**

```text
Smelter Building
→ can_smelt = true
→ capacity = 100
```

里面实际摆什么 Voxel 与生产能力没有直接关系。

或者：

**B. Building Composite 本身只是结构，生产能力来自内部真实设备**

```text
Factory Composite
├─ Structural Block
├─ Construction Block
├─ Industrial Machinery
├─ Power Equipment
└─ ...

Industrial Machinery Behavior
        ↓
Production Capability
```

Composite 再从内部 Voxel 的 Behavior/Capability 推导自身当前生产能力。

➡️ **我推荐 B。**

这样资源、破坏、建设和经济真正连接起来：

```text
生产 Industrial Machinery
→ 建造工厂
→ 获得生产能力

机械受损
→ Production Capability 下降

替换 / 增加机械
→ Capacity 改变
```

而不是建筑名字叫“工厂”就凭空拥有产能。

这也最符合现有规则：Composite 的 Operational Condition 和 Capability 从实际 Runtime Voxel / Behavior 推导，而不是维护另一份平行事实。fileciteturn0file5L31-L37 fileciteturn0file5L97-L108

---

❓ **Q281** - **资源系统对 Engine、Weapon、Medical Equipment 等功能成品的职责边界**：这些顶层商品横跨多个未来系统。如果我们现在把发动机功率、枪械伤害、医疗效果等全部一起设计，资源设计会迅速侵入列车、战斗和乘员系统。

我建议资源系统只负责：

```text
Finished Voxel Good
├─ 商品身份
├─ 生产 DAG / Process
├─ 基础 Voxel 属性
└─ 它组合哪些 Behavior 类型的设计接口
```

而：

```text
Engine 的动力规则      → Train / propulsion design
Weapon 的战斗规则      → Combat design
Medical Equipment 效果 → Crew / health design
```

在对应系统 grilling 时详细确定。

➡️ **我推荐严格采用这个边界。**

资源系统需要回答“**造什么、用什么造、能否库存交易**”，但不提前回答所有“**装上以后具体怎么玩**”。

---

请回答 **Q276–Q281**。

如果 Q280 采用推荐方案，下一轮会自然解锁一组非常重要的新商品：我们不再笼统地问“工厂需要什么材料”，而会开始判断 **Industrial Machinery、Storage Equipment、Engine、Power Equipment、Control Equipment** 等哪些功能 Voxel 值得成为正式可交易成品；同时 Q277 落定后才能继续确定库存容量究竟按**单位数、质量还是两者共同约束**。

---

## 25. User  (2026-09-10 12:01:49 UTC)

Q276 是的

Q277 列车的库存容量取决于其安装的存储设备，存储设备提供容量，聚居地则取决于仓库建筑

Q278 是的，不做内部物流

Q279 同意

Q280 同意

Q281 同意

---

## 26. Assistant  (2026-09-10 12:02:02 UTC)

> [思考摘要] 推进系统澄清：我正在继续处理下一轮互不依赖的问题：仓储容量是否由仓库建筑类型直接决定，还是由内部存储体素累计；同时确认功能体素商品如何登记，以及仓储单位如何进入物流与市场统计。 / 推进了系统澄清

Q276–Q281 已经把功能设备与经济系统的边界定清楚了：列车容量由实际 Storage 设备提供，聚居地容量由仓库建筑提供；生产设施直接读写聚居地统一库存；市场交易也直接改变这份真实库存；而设备装上以后“具体怎么工作”留给列车、战斗、乘员等系统去定义。

现在 frontier 有两组可以并行处理：**库存容量到底按什么约束**，以及**第一批功能性成品 Voxel Goods 应该有哪些**。

> [思考摘要] 细化设备分类：我正在确认功能体素商品的抽象粒度：是采用通用工业设备，还是区分冶炼、化工、制造、农业等专用设备；同时需要决定储存设备、电力设备与动力设备是否独立成类，以免商品树过度膨胀。 / 细化设备分类

❓ **Q282** - **Storage Capacity 应按“货物单位数”还是质量计算**：现在每个库存货物都是整数 Quantity，每个可实体化 Voxel 又统一占 1 Cell；同时 `VoxelDefinition.Mass` 已经独立存在。fileciteturn0file2L11-L16 fileciteturn0file0L21-L25

有三个选择：

**A. 只按货物单位数：**

```text
Storage Capacity = 1000 units

Stone ×1       → 1 unit
Electronics ×1 → 1 unit
```

货物质量另外影响列车总质量、动力学等。

**B. 只按总质量。**

**C. 同时限制数量/体积和质量。**

➡️ **我推荐 A。**

因为每个标准货物单位最终都可以实体化成一个 1×1×1 Voxel，所以 `Quantity` 天然可以理解为抽象的标准货运体积。`Mass` 已经承担重量意义，不需要再建立第二个 Cargo Weight Capacity 系统。

于是：

```text
Storage Equipment
→ +N inventory units

Cargo
→ occupies Quantity capacity
→ contributes Mass × Quantity to train mass
```

重货的代价体现在车辆运行，而不是额外再做一套载重限制。以后 Playtest 真证明需要双约束，再加重量上限。

---

❓ **Q283** - **仓库建筑的容量最终是否也由实际 Storage Voxel 派生**：你已经明确“聚居地取决于仓库建筑”。

这里还可以进一步统一：

```text
Warehouse Composite
├─ Structural Block
├─ Construction Block
└─ Storage Equipment × N
           ↓
      StorageCapability
           ↓
Settlement Inventory Capacity
```

所以玩家表面上是：

> 建一个仓库 → 增加聚居地容量。

底层事实仍然是：

> 仓库内部实际存在的 Storage Equipment 提供容量。

➡️ **我推荐采用这一方案。**

这样列车和聚居地实际上共享同一个 Storage 机制：

```text
Train Storage Equipment
        ↓
Train Capacity

Warehouse Storage Equipment
        ↓
Settlement Capacity
```

仓库只是一个预置的 Building Composite。它被破坏到 Storage Equipment 损坏时，实际库存能力也会发生变化。

这符合现有 Behavior → Capability 结构。fileciteturn0file5L97-L108

---

❓ **Q284** - **所有功能设备仍严格是单 Cell Voxel，并通过数量扩展能力吗**：例如 Engine、Industrial Machinery、Storage Equipment 都不能成为一个“3×2×2 的大型 Voxel”，因为现有硬不变量是一个 Voxel 永远占一个 Cell。fileciteturn0file2L9-L16

因此大型动力系统应该表达为：

```text
Engine ×1  → propulsion 10

Engine ×6  → propulsion 60
```

而不是：

```text
Huge Engine
size = 3×2×1
```

如果以后确实需要不同性能档，可以存在不同 VoxelDefinition：

```text
Basic Engine
Advanced Engine
```

但它们仍然各自占一个 Cell。

➡️ **我推荐严格采用。**

这会让玩家的列车内部空间真正成为设计资源：想获得更高动力、储存、火力，就要付出更多 Voxel 空间或使用更昂贵的高性能设备。

---

❓ **Q285** - **生产设备首版是否只需要一个宏观 `Industrial Machinery` Good**：Q280 已确定真实生产能力来自实际功能 Voxel，但这不意味着必须立刻设计：

```text
Smelting Machinery
Textile Machinery
Chemical Machinery
Electronics Machinery
Food Machinery
...
```

一种更克制的结构是：

```text
Industrial Machinery
    ↓ ProductionCapacity
```

而“这栋设施允许运行哪些 Process”由 Facility / Process Compatibility 决定。

例如：

```text
Smelter Building
+ Industrial Machinery ×10
→ 有 10 单位生产能力
→ 只允许兼容的冶炼 Process

Textile Factory
+ Industrial Machinery ×10
→ 同样有 10 单位生产能力
→ 但运行纺织 Process
```

这样**建筑/设施决定生产类型边界，实际 Machinery 数量决定产能**；建筑名字本身不会凭空产生 Capacity。

➡️ **我推荐首版只加入一个 `Industrial Machinery`。**

如果以后“运输化工设备”和“运输重型机械”本身真的值得成为不同贸易品，再通过 Gameplay Difference Test 拆分。

---

❓ **Q286** - **`Storage Equipment` 是否正式成为第一批功能性成品 Good**：

它已经有非常明确的 Gameplay：

```text
Storage Equipment
→ StorageBehavior
→ StorageCapability
```

并且直接作用于列车和仓库。

➡️ **我推荐正式加入。**

它应该是可制造、可库存、可交易、可安装、可拆卸的普通 Voxel Good。

概念生产路线可以是：

```text
Structural Metal
+ Mechanical Components
→ Storage Equipment
```

但具体配方比例后面统一确定。

---

❓ **Q287** - **`Power Equipment` 是否现在正式加入**：Q181 已经确定电力/能量不是货物，而是 Production Factor。但如果生产能力来自真实世界设施，那么能源也最好最终由真实设备提供，而不是 Settlement 上凭空出现：

```text
Power = 1000
```

因此可以加入：

```text
Power Equipment
→ PowerBehavior
→ Power Capability
```

燃料、电力网络、发电效率等具体规则以后再设计。

➡️ **我推荐现在加入 `Power Equipment`。**

理由不是为了模拟电网，而是它已经有明显的跨系统用途：

- 工业生产；
- 建筑；
- 列车设备；
- 未来武器与电子设备。

这样 `Fuel` 也自然拥有设备消费端。

---

❓ **Q288** - **`Control Equipment` 是否作为独立成品加入，而不直接使用 Electronic Components**：

`Electronic Components` 是零部件商品；真正安装在列车/建筑里的控制系统最好不要直接等同于一箱电子元件。

可以有：

```text
Electronic Components
+ Conductive Metal
+ Polymer
→ Control Equipment
```

它代表控制器、自动化系统、计算单元、通信控制等宏观设备。

➡️ **我推荐加入 `Control Equipment`。**

它与 Electronic Components 的区别类似：

```text
Mechanical Components ≠ Engine
Electronic Components ≠ Control Equipment
```

前者是制造输入，后者才是真正可安装并提供 Behavior 的完成设备。

暂时不继续拆 Computer、Radio、PLC、Fire-Control Computer 等。

---

❓ **Q289** - **`Engine` 是否现在就作为一个具体 Good 冻结，还是只冻结“Propulsion Equipment 产品族”**：

这一点我建议谨慎。

列车系统以后很可能会产生真正有 Gameplay 差异的动力方案：

```text
燃料类型
功率
效率
质量
热量
空间效率
...
```

如果现在直接把所有东西压成唯一一个：

```text
Engine
```

以后很可能不得不拆。

➡️ **我推荐目前只冻结一个设计上的 `Propulsion Equipment` 产品族，不冻结唯一的 `core:engine` 商品。**

等列车动力系统 grilling 时，再决定实际有哪些 VoxelDefinition，例如可能是：

```text
某种 Engine A
某种 Engine B
...
```

资源系统现在只确定：

> 它们都是 Production DAG 顶部的可交易成品 Voxel，由 Mechanical / Electrical / Control 等工业输入制造。

这样既不提前侵入列车设计，也不会把未来路线封死。

---

❓ **Q290** - **原先五类“顶层产物”是否都只是产品族，而不是单个商品或新的领域类型**：

你最初提出：

```text
列车部件
列车武器
乘员武器
列车设备
乘员设备
```

我建议现在正式定义它们为**内容设计分类 / 产品族**。

例如：

```text
Train Equipment
├─ actual VoxelDefinition A
├─ actual VoxelDefinition B
└─ actual VoxelDefinition C
```

而不是存在一个真正的：

```text
core:train-equipment
```

万能货物。

同样，也不建立：

```text
TrainEquipmentDefinition
CrewWeaponDefinition
```

新的基础领域类型。

➡️ **我推荐如此。**

所有具体产品继续只是 `VoxelDefinition + Behavior`。具体产品清单随着列车、战斗、乘员系统设计逐步增加。

---

❓ **Q291** - **Medical Supplies 明确作为“消耗性货物”，而不是功能设备吗**：Q255 已经加入 `Medical Supplies`。

我建议其语义是：

```text
Medical Supplies
= stackable consumable Good
```

用于未来：

```text
Crew treatment
Settlement population needs
Emergency events
```

而不是：

```text
Medical Supplies
→ MedicalBehavior
→ 永久安装设备
```

如果以后需要医疗设施或医疗设备，则另外设计对应功能 Voxel。

➡️ **我推荐如此。**

这样 `Medical Supplies` 与 Food、Clothing 更接近，是一个持续需求和消耗端，而不是列车内部模块。

---

如果全部采用，这一轮只会立即增加三个明确的功能性 Goods：

```text
Industrial Machinery
Storage Equipment
Power Equipment
Control Equipment
```

其中后三个是直接安装型设备；`Industrial Machinery` 主要支撑生产设施。

至于：

```text
Engine / Propulsion
Train Weapons
Crew Weapons
Train Equipment
Crew Equipment
```

则先冻结为**产品族和生产网络出口方向**，具体 VoxelDefinition 等对应 Gameplay 系统设计时再展开。

请继续回答 **Q282–Q291**。

---

## 27. User  (2026-09-10 12:08:59 UTC)

Q282 按A计算，并且只是抽象的存放而不是实例化的存放，例如存放1000个物品在仓库里并不需要把这1000物品实例化为体素拜访在里面

Q283 同意

Q284 同意

Q285 同意

Q286 同意

Q287 同意

Q288 同意

Q289 同意，后续根据玩法再拆

Q290 没错，我认为可以先建立一个占位符，之后版本再细化拆分

Q291 同意

---

## 28. Assistant  (2026-09-10 12:09:40 UTC)

> [思考摘要] 梳理生产系统：我正在把已确定的生产框架正式化：统一处理配方、设备、燃料、电力与劳动力。电力可能采用聚落共享池；燃料消耗、人口劳动力约束，以及离散批次或连续进度，仍需要明确规则。 / 梳理了生产系统

> [思考摘要] 明确系统边界：我正在优先确认相互独立的规则：电力按聚落共享还是建筑分开计算，劳动力是否统一汇总，仓储超容量时是否仅禁止新增，以及占位符是否只存在于设计文档而非实际物品定义。 / 明确了系统边界

这一轮把库存模型也收紧了：**Storage Capacity 是抽象库存容量，不是仓库内部实际摆放空间。** 聚居地库存 `1000` 个 Goods 并不意味着世界中生成 1000 个 Voxel；只有放置、Construction 等显式行为才实体化。这与现有 `Inventory Quantity ≠ Voxel Instance` 的不变量完全一致。fileciteturn0file0L35-L38

另外，Q290 的“占位符”有一个需要明确的语义。随后我们就可以把 `ProductionProcessDefinition` 的运行规则补齐。

---

❓ **Q292** - **顶层产品族的“占位符”是否只是设计占位，而不是真实商品**：你在 Q290 提到可以先建立占位符，之后再细化拆分。这里最好明确到底是什么。

我建议目前记录：

```text
Future Product Families
├─ Propulsion Equipment
├─ Train Weapons
├─ Crew Weapons
├─ Train Equipment
└─ Crew Equipment
```

但**不因此创建**：

```text
core:propulsion-equipment
core:train-weapon
core:crew-weapon
...
```

这些万能 VoxelDefinition。

等对应 Gameplay 设计完成，再真正增加：

```text
Actual VoxelDefinition A
Actual VoxelDefinition B
...
```

➡️ **我推荐占位符只存在于设计文档/内容规划层，不进入运行时 Catalog 和 Production DAG。**

否则以后拆分时会遇到旧万能货物到底如何迁移、库存如何转换等没有价值的问题。

---

❓ **Q293** - **所有普通 Goods 是否统一占用 1 点 Storage Capacity**：既然一个标准库存单位对应一个可实体化 Voxel 单位，那么可以定义：

```text
Inventory usage =
Σ Quantity
```

无论：

```text
Stone ×1
Electronic Components ×1
Engine ×1
```

都消耗 `1` 点容量。

它们的重量差异继续通过：

```text
Mass × Quantity
```

影响列车总质量，而不影响库存槽位。

➡️ **我推荐如此，并且首版不设计 Bulk / Liquid / Refrigerated / Hazardous 等特殊 Cargo Capacity。**

例如 Crude Oil 也只是普通库存单位，不要求 Tank Capacity。只有以后发现“专用罐车”等确实能增加贸易策略深度，再引入 Storage 类型约束。

---

❓ **Q294** - **Storage Capacity 降低到当前库存以下时是否禁止自动销毁货物**：例如仓库受损：

```text
Before:
Capacity = 1000
Inventory = 900

After damage:
Capacity = 600
Inventory = 900
```

如果强制立即删除 300 单位货物，既突兀又需要决定删什么。

➡️ **我推荐允许临时 Overcapacity：**

```text
Used = 900
Capacity = 600
Overcapacity = 300
```

此时：

- 已有 Goods 保留；
- 可以取出、出售、运输；
- 不能再增加库存；
- 修复仓库或减少库存后恢复正常。

不自动随机丢弃货物。

以后事故系统如果要设计“货舱被炸毁导致货物散落”，那属于显式 Damage Gameplay，而不是 Storage Capacity 的通用规则。

---

❓ **Q295** - **Production Process 是否严格采用整数 Batch Recipe**：我们已经确定所有 Goods Quantity 为整数，因此我建议每个 Process 的物质关系也完全采用整数：

```text
Process: Structural Metal Refining

Inputs
Ferrous Ore × 4
Coal × 1

Outputs
Structural Metal × 3
```

每次完成的是一个完整 Batch。

不允许：

```text
Ferrous Ore × 0.37
```

也不产生 0.42 个产品。

➡️ **我推荐所有 Good Input / Output 数量严格为正整数。**

生产速度可以连续，但**物质转换永远在完整 Batch 上原子发生**。这样库存、贸易和确定性模拟都更干净。

---

❓ **Q296** - **Process 的生产速度是否采用 `WorkRequired` 模型，而不是配方自带固定每秒产量**：生产设施拥有不同数量的 Industrial Machinery，因此如果 Process 固定：

> 每 10 秒生产 1 Batch

那么增加机器到底如何加速就会产生额外规则。

我建议：

```text
ProductionProcessDefinition
├─ Inputs
├─ Outputs
├─ WorkRequired
└─ Requirements
```

设施提供：

```text
ProductionCapability = X work / time
```

于是：

```text
Batch completion time
= WorkRequired / allocated ProductionCapability
```

例如：

```text
Process WorkRequired = 100

Factory A capacity = 10
→ 10 time units

Factory B capacity = 25
→ 4 time units
```

➡️ **我推荐采用 Work 模型。**

`Work` 是抽象的生产工作量，不是 Goods，也不是物理资源。这样增加 Industrial Machinery 自然增加产能，而不需要为每个配方重新定义不同规模工厂的速度。

---

❓ **Q297** - **生产设施的“允许生产什么”与“能生产多快”是否严格分离**：Q227、Q280、Q285 实际已经指向这个结构，我建议正式固定下来：

```text
Facility / Building Definition
→ Permitted Process Families

实际 Industrial Machinery
→ Production Capacity
```

例如：

```text
Smelter
Permits: metallurgy

Industrial Machinery ×10
→ Production Capacity = 10

因此可以运行：
Ferrous Refining
Conductive Refining
...
```

另一个：

```text
Assembly Factory
Permits: assembly

Industrial Machinery ×20
→ Production Capacity = 20
```

同一种 `Industrial Machinery` 可以被两栋建筑使用，但它们不能因此运行相同 Process。

➡️ **我推荐明确分离：**

> **Facility configuration 决定 Process Compatibility；实际功能 Voxel 决定 Capacity。**

这样不会为了每一种工业建立一种 Machinery 商品，同时建筑本身又不能脱离内部设备凭空生产。

---

❓ **Q298** - **聚居地内部的 Power 是否也采用统一 Pool，而不模拟电网**：既然内部货物流已经宏观化，我认为电力也应该一致处理。

例如聚居地所有有效 Power Equipment 汇总：

```text
Settlement Power Capacity
= Σ active PowerCapability
```

所有生产设施共同申请这份 Power。

不模拟：

```text
Power Plant
→ Cable
→ Substation
→ Factory
```

也不要求建筑物空间邻接发电设施。

➡️ **我推荐 Settlement-wide Power Pool。**

Power 仍然是真实设备提供的 Gameplay Capability，只是**内部输配电被抽象掉**。

列车自身以后可以拥有独立的 Train Power Pool；这是列车系统分支，不需要与聚居地电力连成一个世界级电网。

---

❓ **Q299** - **Labor 是否采用类似 Power 的聚居地级 Production Factor**：Q181 已经确定 Labor 不是 Goods，而现在人口又必然会成为聚居地经济的重要组成部分。

我建议：

```text
Settlement Population
        ↓
Available Labor Capacity
```

Process 可以要求：

```text
Work
Power
Labor
```

但玩家不需要把具体 NPC 一个个分配进机器，也不存在 `Worker × 17` 这种 Inventory。

➡️ **我推荐采用 Settlement-wide Labor Pool。**

即：

> 人口提供可用劳动力；不同生产活动竞争劳动力；具体人口增长、职业、工资等以后在经济/人口系统中细化。

这样能让高度工业化聚居地受到人口规模约束，却不会退化成人员排班模拟。

现有聚居地已经有常住人口这一领域事实，因此以后可以自然接入。fileciteturn0file9L55-L59

---

❓ **Q300** - **列车库存与聚居地库存之间是否必须发生显式 Transfer，生产设施不能直接读取列车货舱**：例如列车抵达自己的聚居地：

```text
Train:
Ferrous Ore ×500

Settlement:
Ferrous Ore ×0
```

工厂是否可以直接把列车里的 Ore 当输入？

➡️ **我推荐不可以。**

应明确执行：

```text
Train Inventory
    ↓ Transfer
Settlement Inventory
    ↓
Production
```

反方向同理：

```text
Settlement Inventory
    ↓ Transfer
Train Inventory
```

Transfer 可以由玩家命令或以后自动化规则完成，但它必须是一个真实库存移动行为。

这样“列车库存”和“聚居地库存”始终是两个明确的地点库存，不会因为停站而临时合并成一个魔法库存池。

---

这一轮是 **Q292–Q300**。

如果这些确定，生产系统的骨架会达到：

```text
Goods Inventory
      │
      ▼
ProductionProcess
├─ integer Inputs / Outputs
├─ WorkRequired
├─ Power Requirement
├─ Labor Requirement
└─ Facility Compatibility
      │
      ▼
Goods Inventory
```

下一轮就可以继续决定**生产运行的事务语义**：输入什么时候扣除、多个工厂争抢同一库存怎样 Resolve、产物遇到满仓怎么办、停电/缺工时如何暂停，以及 Power Equipment 是否消耗 Fuel。现有 Simulation 本身已经有 Request → Resolve → Commit 的确定性竞争处理框架，可以直接承接这一层。fileciteturn0file11L44-L59

---

## 29. User  (2026-09-10 12:17:45 UTC)

Q292 没错，同意

Q293 同意

Q294 同意

Q295 没错，严格整数

Q296 同意

Q297 同意

Q298 统一pool，不模拟电网

Q299 没错，同意

Q300 同意

---

## 30. Assistant  (2026-09-10 12:17:59 UTC)

> [思考摘要] 推进生产规则：我会在已确定的配方、设施、劳动力和功率约束上继续追问：批次何时占用投入、是否允许并行、暂停如何处理、产能与库存竞争如何裁决，以及失败或中断时物资如何回滚。 / 推进生产规则制定

Q292–Q300 已把“一个生产批次需要什么”确定下来：整数配方、抽象 Work、设施兼容性、聚居地级 Power/Labor Pool，以及明确的地点库存与 Transfer。现在 frontier 正好落到**批次在运行时如何占用输入、产能和输出空间，以及多个生产者如何竞争共享资源**。

> [思考摘要] 界定生产运行：我会先确认几个根决策：设施是否只能执行一个作业、生产进度是否显式保存、工作量如何分配，以及生产计划由谁设定。投入品消耗、暂停和回滚规则将留到这些选择确定后再追问。 / 界定了生产运行

Q292–Q300 后，生产系统已经可以进入真正的 **Runtime Execution**。现有 Simulation 本身已经确定 `Evaluate → Resolve → Commit`，并要求共享资源竞争不能依赖执行顺序，因此生产系统应直接建立在这套仲裁框架上，而不是另造实时更新逻辑。fileciteturn0file11L44-L59

这一轮只处理当前已经解锁、且彼此不依赖的几个根决策。输入扣除、输出满仓、生产中断等问题都依赖 Q301，因此留到下一轮。

---

❓ **Q301** - **正在生产的 Batch 是否是一等 Runtime State**：假设一个 Process：

```text
Mechanical Components
WorkRequired = 100
```

工厂已经完成了 63 Work。这个“63/100”是否应该作为明确的运行状态存在？

也就是：

```text
ProductionJob / ActiveBatch
├─ ProcessDefinitionId
├─ ProgressWork
└─ 之后需要的 reservation / state
```

而不是每个 Tick 独立计算一点产物，或者只在达到某个时间点时突然执行 Recipe。

➡️ **我推荐明确建立 Active Batch / Production Job 运行状态。**

Goods 仍然只按整数 Batch 转换，但 Work 可以连续积累：

```text
0 → 17 → 43 → 63 → 100
                    ↓
               Batch complete
```

这样停电、缺工、设施受损、Save/Load 等都可以自然暂停并恢复同一个批次，而不会产生半个货物。

---

❓ **Q302** - **生产系统是否只负责“执行生产计划”，而不自己决定生产什么**：生产设施可以运行多个兼容 Process，但“现在应该生产 Structural Metal 还是 Conductive Metal”本质是经营决策。

一种做法是 Production System 自己看价格、库存后自动选择最赚钱的配方；另一种是由更高层系统提供 Production Plan / Order。

➡️ **我推荐严格采用后者。**

边界应当是：

```text
Player / Settlement AI / Economy Policy
              ↓
       Production Plan
              ↓
       Production System
              ↓
        execute faithfully
```

资源/生产系统不内置“利润最大化 AI”。

这样以后：

- 玩家聚居地可以自己配置生产；
- AI 聚居地可以由经济 AI 决策；
- 不同 AI 可以采取不同产业策略；

但底层生产物理规则始终完全相同，符合 Q182。

---

❓ **Q303** - **Power 与 Labor 是否都是不可库存的即时 Capacity**：我们已经确定它们不是 Goods，也都是聚居地级 Pool。

现在需要进一步明确：

```text
Power = 当前这一时刻可提供多少
Labor = 当前这一时刻可投入多少
```

而不是：

```text
Power Inventory = 5000
Labor Inventory = 300
```

➡️ **我推荐明确规定二者都不能库存。**

其中：

- Power 是即时产生、即时分配的流；
- Labor 是人口当前能够提供的工作能力；
- 未使用的 Power/Labor 不会积累到未来；
- Battery、蓄能等如果未来有实际玩法，再单独设计。

这样不会把 Production Factor 偷偷变回另一种 Inventory Good。

---

❓ **Q304** - **Labor 是否只是被占用，而不是被“消耗”**：如果一个 Process 需要 Labor，它显然不应该：

```text
生产一次 Structural Metal
→ 人口 -3
```

我建议 Labor 的语义是：

```text
Available Labor = 100

Factory A allocated 30
Factory B allocated 50

Remaining = 20
```

停止生产后，被占用的 Labor 立即重新可用。

➡️ **我推荐如此。**

人口死亡、迁移、雇佣等才改变人口本身；正常生产只是竞争同一个 Labor Capacity。

具体“100 人口产生多少 Labor”以及工资、职业等留给人口/经济系统。

---

❓ **Q305** - **Power Equipment 是否通过消耗 Fuel 来产生 Power，而不是免费发电**：目前 `Fuel` 已经是正式工业商品，`Power Equipment` 又已经确定为实际功能 Voxel。如果 Power Equipment 无条件提供 Power，那么聚居地工业实际上没有持续能源物流需求。

我建议首版基本关系是：

```text
Settlement Inventory
    Fuel
      ↓
Power Equipment
      ↓
Settlement Power Pool
      ↓
Production Facilities
```

Power Equipment 没有 Fuel 时就不能输出对应 Power。

这不是 Production DAG，因为输出的 Power 不是 Good，而是运行时 Capability / Factor：

```text
Fuel Good
→ PowerBehavior
→ Power Capacity
```

➡️ **我推荐采用这一规则。**

具体每单位 Fuel 能产生多少 Power、设备效率等以后调参；未来太阳能、核能等若真的值得加入，可以用不同设备 Behavior 扩展，不需要推翻当前模型。

---

❓ **Q306** - **Power/Labor 消耗强度是否与实际执行的 Work 成比例**：Process 已经有 `WorkRequired`。对于生产 Factor，我认为最干净的模型不是：

```text
这个 Batch 永远需要 5 Power 和 3 Labor
```

而是定义生产强度：

```text
PowerPerWork
LaborPerWork
```

例如某工厂本 Tick 实际执行 20 Work：

```text
Power demand = 20 × PowerPerWork
Labor demand = 20 × LaborPerWork
```

如果只能获得一半 Power/Labor，则这一 Tick 只能完成相应比例的 Work。

➡️ **我推荐采用“Factor intensity × actual Work”的模型。**

因此生产速度最终受到三个瓶颈共同约束：

```text
Machinery Work Capacity
Power available
Labor available
        ↓
actual executable Work
```

不会出现“工厂虽然只有一半电，但依然全速生产，只是事后扣一个抽象电力值”的问题。

---

❓ **Q307** - **Industrial Machinery 的 Production Capacity 是否线性叠加**：既然每个功能 Voxel 都只能占一个 Cell，那么最简单的统一模型是：

```text
Industrial Machinery ×1
→ WorkCapacity = X

×10
→ 10X
```

设备受损或失效后，只计算当前有效 Machinery 的 Capability。

➡️ **我推荐首版严格线性叠加。**

暂时不引入：

- 规模经济；
- 机器之间的邻接 Bonus；
- 最佳布局；
- 工厂效率曲线；
- 边际递减。

如果未来 Gameplay 证明这些值得存在，再通过 Behavior / Facility 规则加入。

这样玩家增加工厂产能的基本逻辑非常透明：

```text
更多 Machinery
→ 占更多 Composite 空间
→ 消耗更多制造资源
→ 需要更多 Power / Labor
→ 获得更多 Work Capacity
```

这和当前“能力从实际 Voxel/Behavior 推导”的原则一致。fileciteturn0file5L97-L108

---

请回答 **Q301–Q307**。

这一轮完成后，Q301 会解锁生产批次最关键的一组事务问题：**什么时候锁定/扣除 Inputs、Work 中断是否保留、多个工厂抢同一批原料如何 Resolve、Output 空间是否预留、满仓时能否启动，以及建筑在半成品状态下被摧毁时怎么办。**

---

## 31. User  (2026-09-10 12:27:42 UTC)

Q301 同意

Q302 同意，严格采用后者

Q303 同意

Q304 是的

Q305 同意

Q306 同意

Q307 同意

---

## 32. Assistant  (2026-09-10 12:28:21 UTC)

> [思考摘要] 梳理下一轮问题：我正在围绕批次事务语义继续提问：输入何时扣除、输出容量如何预留、多个设施如何竞争资源，以及中断时状态如何处理。我会先确认这些基础规则，再展开设施损毁等后续情形。 / 梳理了下一轮问题

Q301–Q307 已经确定 Active Batch、Work、Power/Labor、Fuel 与 Capacity 的基本模型。现在真正进入 **Batch Transaction**：什么时候消耗输入、何时占用输出空间、共享库存如何仲裁。

> [思考摘要] 推进批次语义：我正在确认四个基础规则：输入在何时扣除、产出空间是否提前预留、资源竞争如何裁决，以及中断后进度是否保留。先固定这些，再处理取消、损毁和恢复等后续情形。 / 推进批次语义

❓ **Q308** - **Batch 的 Inputs 在什么时候真正从库存中扣除**：

假设：

```text
Ferrous Ore ×4
Coal ×1
→ Structural Metal ×3
WorkRequired = 100
```

有三种语义：

**A. Batch 启动时原子扣除全部 Inputs。**  
这些原料此后已经进入“在制品”状态，不再属于库存。

**B. 启动时只 Reservation，完成时才扣除。**

**C. 完成时才检查和扣除。**

➡️ **我推荐 A。**

```text
Start Batch
├─ 检查完整 Inputs
├─ 原子扣除 Inputs
└─ 创建 ActiveBatch

ActiveBatch.ProgressWork
0 → ... → 100
```

这是最干净的物质语义：生产开始以后，原料已经被投入生产，不应继续显示在 Settlement Inventory 中。也避免额外建立 `ReservedGoods` 这种半库存状态。

---

❓ **Q309** - **Batch 启动时是否同时 Reservation 最终 Output Capacity**：

如果启动时只扣 Inputs，却不考虑最终产物空间，就可能出现：

```text
Batch Progress = 100%
Settlement Storage = full
```

然后我们还需要发明“完成但拿不出来的商品”。

➡️ **我推荐启动 Batch 时同时原子预留所需 Output Capacity。**

例如：

```text
Inputs = 5 units
Outputs = 3 units
```

启动事务：

```text
Check Inputs
Check output capacity
↓
Consume Inputs
Reserve 3 Storage Capacity
Create ActiveBatch
```

完成时：

```text
Reserved Capacity
→ actual Output Goods
```

Reservation 只是抽象容量，不实例化任何 Voxel。

如果生产期间仓库受损导致 Capacity 降低，沿用 Q294：允许临时 Overcapacity，但已经获得的 Reservation 不失效；只阻止新的库存增加/Batch 启动。

---

❓ **Q310** - **Batch 启动必须是严格的 All-or-Nothing 事务吗**：

例如配方需要：

```text
Mechanical Components ×4
Electrical Components ×2
```

库存只有：

```text
Mechanical Components ×4
Electrical Components ×1
```

是否允许先扣掉 Mechanical Components，等待另一个 Input？

➡️ **我推荐绝对不允许。**

一个 Batch 的启动条件应该整体 Resolve：

```text
Required Inputs ✓
Output Reservation ✓
Facility valid ✓
↓
全部成功 → Start
任何失败 → Nothing changes
```

不存在“半装料 Batch”。

这继续遵守已经确定的结构修改原子性原则，也与 Simulation 的 Transaction 设计一致。fileciteturn0file11L61-L64

---

❓ **Q311** - **多个设施争抢同一批库存时如何决定谁启动**：

例如同一个 Settlement Inventory 只有：

```text
Structural Metal ×10
```

Factory A 和 Factory B 同一个 Simulation Tick 都请求 10。

不能采用：

> 哪个 System / Collection 先遍历到，哪个拿走。

现有 Simulation 已经明确要求共享资源竞争经过 Resolver，并禁止注册顺序和容器枚举顺序决定 Gameplay。fileciteturn0file11L27-L30 fileciteturn0file11L46-L59

➡️ **我推荐生产计划提供显式 `Production Priority`，之后再使用稳定的确定性 fallback。**

例如：

```text
1. Player / AI Production Priority
2. Stable Facility / Job Identity
```

所以：

```text
Requests
↓
Production Resolver
↓
grant complete Batch starts
↓
Commit
```

不允许“先执行先得”。

---

❓ **Q312** - **Power 与 Labor 的竞争是否使用同一种 Priority，但允许部分分配**：

Inputs 不允许部分分配，因为缺一个原料整个 Batch 都不能启动。

但 Power/Labor 不同。Q306 已经确定实际 Work 可以因为 Factor 不足而降低。

例如：

```text
Factory A requests Power 80
Factory B requests Power 80

Available Power = 100
```

➡️ **我推荐同样经过 Resolver，但允许分配部分 Capacity。**

结构：

```text
Active Production Jobs
       ↓ Requests
Power / Labor Resolver
       ↓
Allocated Factors
       ↓
Actual Work
```

优先级仍采用：

```text
Explicit Production Priority
→ deterministic fallback
```

因此高优先级生产可以先获得 Factor；剩余 Capacity 再分给后续任务。

不采用隐式平均分配，因为“平均公平”本身也是一种 Gameplay 策略，不应该偷偷写死在底层系统中。

---

❓ **Q313** - **一个生产设施同时只运行一个 Production Job，还是允许并行多条生产线**：

Q227 已经确定一个 Facility 可以兼容多个 Process，但还没有确定是否可以**同时**运行。

**A. 一个 Facility 同时一个 Active Production Job。**

```text
Factory
→ 当前生产 Mechanical Components
→ 全部 Machinery Capacity 投入该 Job
```

想生产其他东西，要切换 Process 或再建一座设施。

**B. 一个 Facility 可以同时分配 Capacity 给多个 Job。**

```text
60% Mechanical Components
40% Electrical Components
```

➡️ **我推荐首版采用 A。**

原因是我们设计的是宏观贸易经济，而不是生产排程优化器。

Facility 的：

```text
Machinery count
Power
Labor
```

共同决定当前 Process 的速度。

如果玩家希望并行生产，两座工厂就是一个非常直观的解决方案。以后如果大型工厂需要多生产线，再扩展为多个 Production Slot，不需要改变 ProcessDefinition 本身。

---

❓ **Q314** - **生产中途 Power/Labor 不足时是否只 Pause，不损失 Progress**：

例如：

```text
ProgressWork = 73 / 100
```

突然停电。

可以：

**A. 保留 73，等待恢复。**

**B. Progress 慢慢衰减。**

**C. Batch 失败并损失 Inputs。**

➡️ **我推荐 A。**

```text
Insufficient Power / Labor
→ ActualWork = 0
→ ActiveBatch stays at 73
```

没有人为惩罚、没有半成品腐坏。

以后如果某些特殊 Process 确实需要连续运行，比如高炉停机会造成损失，可以通过专门 Behavior/Process 规则增加，而不是让所有生产默认复杂化。

---

❓ **Q315** - **整数 Fuel 如何驱动连续 Power 输出**：

Goods 必须严格是整数 Quantity，但 Power 是连续 Capacity，因此不能每 Tick 消耗：

```text
0.013 Fuel
```

我建议每个整数 Fuel 单位拥有一个固定的抽象能量预算。例如：

```text
Fuel ×1
→ FuelEnergy = 1000
```

Power Equipment 从 Settlement Inventory **一次取出一个完整 Fuel 单位**，然后内部保存：

```text
CurrentFuelEnergyRemaining
```

实际发电按负载持续扣减这个值：

```text
1000 → 934.2 → 810.7 → ... → 0
```

归零后再申请下一单位 Fuel。

➡️ **我推荐这种“整数 Fuel + 连续内部燃料剩余量”模型。**

这里存储的是**当前正在燃烧的一单位 Fuel 的剩余化学能状态**，不是可库存的 Power，因此不违反 Q303。

这也避免发电结果依赖 Simulation Tick 大小，适合现有连续量使用 `double` 的设计。fileciteturn0file11L32-L42

---

本轮是 **Q308–Q315**。

这些落定之后，下一轮才能继续处理直接依赖它们的异常路径：**玩家切换生产时能否取消 Active Batch、取消是否返还材料、设施被摧毁时在制品怎么处理、Reservation 怎么释放，以及 Active Batch 如何进入 Save State。**

---

## 33. User  (2026-09-10 12:36:56 UTC)

Q308 同意

Q309 不同意，对于output我认为可以建立一个单独的缓冲区，若仓库有空间则自动存放，但若不满足则尽可能存放，多出的物品暂存在缓冲区，同时当缓冲区有物品时所有生产暂停，不再继续。

Q310 不允许

Q311 同意

Q312 同意

Q313 同意

Q314 同意

Q315 同意

---

## 34. Assistant  (2026-09-10 12:37:13 UTC)

> [思考摘要] 规划下一轮追问：我会围绕生产输出缓冲区继续追问：明确缓冲区归属、容量、暂停范围，以及取消、拆除、保存和读档时的处理，避免把关键状态留给实现猜测。 / 规划下一轮追问

Q309 改变了输出侧的事务模型：**输入在 Batch 启动时已被消耗，但输出完成后不要求仓库预留空间，而是先进入生产侧缓冲，再尽可能自动转入聚居地库存。** 这意味着我们必须先把 Buffer 本身的所有权、容量和持久性边界定清楚，之后才能讨论“缓冲区堵塞时暂停谁、设施被毁时货物怎么办”。

❓ **Q316** - **Output Buffer 属于单个生产设施，还是属于整个聚居地**：你提出“多出的物品暂存在缓冲区”，这里最关键的是缓冲区在哪里。

**A. Facility-local Buffer**

```text
Factory A
├─ ActiveBatch
└─ OutputBuffer

Factory B
├─ ActiveBatch
└─ OutputBuffer
```

**B. Settlement-wide Production Buffer**

```text
Settlement
├─ Inventory
└─ ProductionBuffer
```

➡️ **我推荐 A：每个生产设施拥有自己的 Output Buffer。**

生产完成的货物首先属于产生它的设施；聚居地 Inventory 只是自动接收目标。这样设施受损、停产、拆除时状态归属非常明确，也不会形成一个绕过仓库容量的第二个聚居地级无限库存。

---

❓ **Q317** - **Output Buffer 是否只需要容纳“当前完成的一个 Batch”**：既然规则是“Buffer 非空时不再继续生产”，那么实际上无需设计一个很大的缓存仓库。

例如：

```text
Batch output:
Structural Metal ×3

Warehouse 只剩 1 capacity

→ Inventory +1
→ OutputBuffer +2
→ production blocked
```

设施直到这 2 个货物全部移走后才能开始下一 Batch。

➡️ **我推荐 Buffer 没有独立的 Gameplay 容量属性，而是只保存当前未能转移完的 Batch 输出。**

也就是说：

> 一个 Facility 最多存在一组未清空的 completed output。

不设计：

```text
BufferCapacity = 100
```

这种第二种仓储能力。否则玩家会开始把工厂 Buffer 当免费仓库使用。

---

❓ **Q318** - **完成 Batch 时是否“尽可能立即转移”输出**：例如：

```text
Output = 10
Settlement free capacity = 6
```

是否应原子地产生：

```text
Settlement Inventory +6
Facility OutputBuffer +4
```

而不是全部 10 先进入 Buffer，再等下一个 Tick 搬运？

➡️ **我推荐尽可能立即转移。**

逻辑上：

```text
Batch completes
→ produce integer outputs
→ move as much as possible to Settlement Inventory
→ remainder enters OutputBuffer
```

之后每个 Simulation Tick 或相关库存变化后，系统都可以再次尝试：

```text
OutputBuffer → Settlement Inventory
```

这只是同一聚居地内部的自动抽象物流，不需要玩家手工搬运。

---

❓ **Q319** - **Active Batch 是否允许玩家主动取消**：由于 Q308 已确定 Inputs 在启动时已经消耗，取消时必须决定这些物质怎么办。

有三个常见方向：

**A. 不允许取消。** 开始后只能暂停，最终必须完成。

**B. 允许取消并全额返还 Inputs。**

**C. 允许取消，但已投入 Inputs 不返还。**

➡️ **我推荐首版采用 A：Active Batch 不允许主动取消，只能暂停。**

这样最简单，也避免制造“加工到 99% → Cancel → 原料完整返还”的物质复制语义。

玩家想切换生产 Process：

```text
先完成当前 Batch
→ 清空 OutputBuffer
→ 再切换
```

以后如果确实需要取消生产，可以专门设计 Scrap / Recovery 规则，而不是默认退款。

---

❓ **Q320** - **Active Batch 是否独占该 Facility 的 Process 选择**：这是 Q313“一个设施同时只运行一个 Job”的进一步边界。

我建议一旦：

```text
ActiveBatch(Process A)
```

存在，就不能把 Facility 配置切换到 Process B。

只有：

```text
ActiveBatch = none
OutputBuffer = empty
```

时，才能改变当前生产计划。

➡️ **我推荐如此。**

这样 Facility 的状态机非常简单：

```text
Idle
  ↓ start
Processing
  ↓ complete
BlockedOnOutput   （如果存在剩余 Buffer）
  ↓ buffer emptied
Idle
```

不会出现“Process A 的半成品还在机器里，但工厂已经切到 Process B”的混合状态。

---

❓ **Q321** - **Output Buffer 中的 Goods 是否属于真实库存货物，但不占 Settlement Storage Capacity**：虽然 Buffer 不是普通 Inventory，但里面已经存在完成的整数商品。

例如：

```text
OutputBuffer:
Electronic Components ×7
```

这些东西已经不是半成品，不能因为 Save/Load 或暂停而消失。

➡️ **我推荐将其视为真实 Goods 数量，只是 Location = Facility Output Buffer。**

因此：

- 它们不占 Settlement Inventory Capacity；
- 不能参与普通市场销售；
- 不能直接被其他生产设施消费；
- 只有转入 Settlement Inventory 后才成为聚居地可用库存。

这样不会让 Buffer 变成绕过仓库容量的隐形共享库存。

---

这一轮 Frontier 是 **Q316–Q321**。

这些决定完成后，下一轮才能继续处理它们的直接下游：**Buffer 非空究竟暂停该设施还是整个聚居地、设施被摧毁时 Active Batch 与 Buffer 如何处理、保存哪些生产运行状态，以及仓库重新腾出空间时 Buffer 的自动清空顺序。**

---

## 35. User  (2026-09-10 12:42:00 UTC)

Q316 同意

Q317 同意

Q318 同意

Q319 A

Q320 同意

Q321 同意

---

## 36. Assistant  (2026-09-10 12:42:16 UTC)

> [思考摘要] 推进后续边界：我正在继续确认缓冲、转运和设施状态的边界，优先询问不依赖未决规则的问题：缓冲满时暂停范围、多个缓冲竞争容量时的顺序，以及多产物批次的部分转移处理方式。 / 推进后续边界工作

Q316–Q321 把输出堵塞状态定义完整了：Buffer 是**设施局部、最多承载一个已完成 Batch 的剩余产物**；其中货物已经真实存在，但还不能进入市场或被其他工厂消费。

现在 frontier 正好落在异常与仲裁路径：**Buffer 堵塞影响谁、多个 Buffer 如何抢有限仓容、设施被毁时在制品和已完成产物怎么处理、这些状态如何存档。**

> [思考摘要] 厘清设施后果：我正在确认设施缓冲与生命周期的具体行为：暂停是否仅影响单个设施、多个缓冲如何竞争转运、多产物如何部分转移，以及禁用或摧毁设施时正在生产和已缓存物资如何处理。 / 明确了设施后果

❓ **Q322** - **Output Buffer 非空时暂停范围**：Q309 你说“缓冲区有物品时所有生产暂停”，现在 Buffer 已确定属于单个 Facility，因此需要明确这里的“所有”指什么。

**A. 只阻塞该 Facility。**

```text
Factory A Buffer 非空 → Factory A Blocked
Factory B 正常生产
Factory C 正常生产
```

**B. 一个 Buffer 堵塞就暂停整个 Settlement 的所有生产。**

➡️ **我推荐 A。** 一个工厂因为仓储问题堵塞，不应冻结整座工业城市。每座 Facility 独立经历：

```text
Idle → Processing → BlockedOnOutput → Idle
```

聚居地级 Power、Labor 和 Inventory 仍是共享资源，但 Production Job 生命周期属于各自设施。

---

❓ **Q323** - **仓库重新出现空间时，Output Buffer 是否拥有优先入库权**：假设仓库刚空出 10 Capacity，同时发生：

```text
Factory A Buffer → 请求 6
Factory B Buffer → 请求 8
Train unloading → 请求 10
```

这些请求不能靠执行顺序争抢容量。

➡️ **我推荐建立统一的 Storage Capacity Resolver，并让已完成的 Output Buffer Transfer 高于新的外部入库。**

例如：

```text
1. Existing Output Buffer
2. Other inventory inflows
3. deterministic fallback within same priority
```

多个 Buffer 之间：

```text
Production Priority
→ stable Facility InstanceId
```

决定顺序。

理由是 Buffer 中的商品已经完成生产，而且正在阻塞设施；应优先清掉既有债务，而不是一边让工厂堵死，一边继续往仓库卸新货。

这也符合现有 Simulation 对共享资源竞争必须显式 Resolve 的要求。fileciteturn0file11L46-L59

---

❓ **Q324** - **多输出 Batch 在只能部分入库时如何确定商品顺序**：例如：

```text
Refining Output
Fuel ×4
Chemical Feedstock ×6

Free Capacity = 5
```

Q318 已经确定要“尽可能立即转移”，所以必须决定是哪 5 个先进入 Settlement Inventory。

➡️ **我推荐采用规范化的 `VoxelDefinitionId` Ordinal 顺序作为确定性 fallback。**

不增加“副产品优先级”这种字段，也不依赖 JSON 中偶然的书写顺序。

例如算法始终：

```text
sort outputs by VoxelDefinitionId
→ fill available capacity
→ remainder stays in the same OutputBuffer
```

它主要是一条确定性规则，不代表商品的经济优先级。以后如果 Gameplay 真需要玩家控制输出分流，再单独设计。

---

❓ **Q325** - **Facility 被摧毁时 Active Batch 怎么处理**：Inputs 在 Batch 启动时已经从库存扣除，因此：

```text
Inputs consumed
Progress = 67 / 100
Facility destroyed
```

是否返还原料？

➡️ **我推荐 Active Batch 直接销毁，Inputs 不返还。**

它们已经进入在制品状态；设施被摧毁导致生产损失是合理结果：

```text
Facility destroyed
→ ActiveBatch removed
→ unfinished material lost
```

以后如果 Recovery 系统需要从工业事故中回收部分材料，可以作为明确的 Salvage/Recovery Gameplay，而不是默认退款。

---

❓ **Q326** - **Facility 被摧毁时 Output Buffer 中已经完成的 Goods 怎么处理**：这与 Q325 不同，因为 Buffer 中已经是完整货物。

例如：

```text
Factory destroyed
OutputBuffer:
Mechanical Components ×3
```

选择可以是：

**A. 直接损失。**

**B. 强行转入 Settlement Inventory，即使形成 Overcapacity。**

**C. 在世界中实体化为散落 Voxel Goods。**

➡️ **我推荐首版 A。**

不要为了这个边缘情况立即引入“确定性寻找附近空格并散落大量货物”的系统，也不要借 Overcapacity 绕过仓库限制。

明确事件即可：

```text
Facility destroyed
→ buffered goods destroyed
```

以后事故与 Salvage 系统成熟后，可以把这里扩展成部分散落/回收。

---

❓ **Q327** - **Facility 只是失效而未被摧毁时是否完整保留生产状态**：例如 Industrial Machinery 受损后，建筑仍保持身份，但当前无法生产。

➡️ **我推荐完整保留：**

```text
ActiveBatch.ProgressWork 保留
OutputBuffer 保留
Production Plan 保留
```

生产能力不足时 Active Batch 停住，修复后继续。

对于已经完成的 Output Buffer，我建议即使生产设备失效，仍然允许它继续自动转入 Settlement Inventory，因为这是我们已经抽象掉的**聚居地内部物流**，不是机器继续加工。

只有整个 Facility Composite 真正被摧毁，才触发 Q325/Q326 的损失规则。

---

❓ **Q328** - **生产运行状态是否全部进入 Save State**：既然 Active Batch 可以跨很长时间运行，存档至少需要保存：

```text
Facility
├─ selected Production Plan / Process
├─ Production Priority
├─ ActiveBatch
│  ├─ ProcessDefinitionId
│  └─ ProgressWork
└─ OutputBuffer
   └─ Goods quantities
```

➡️ **我推荐全部保存。**

读档不能把 `87/100 Work` 重置成零，也不能丢掉堵在 Buffer 中的已经完成商品。

这些都是影响后续 Simulation 的权威 Runtime State，而不是派生缓存；现有存档原则本来也要求保存实际 Runtime State，并保证 Save → Load → Continue 与不中断运行一致。fileciteturn0file1L18-L34

---

❓ **Q329** - **Power Equipment 中已经投入的一单位 Fuel 是否也属于设备 Runtime State**：Q315 已确定：

```text
Fuel ×1
→ CurrentFuelEnergyRemaining
→ 连续发电
```

那么一台设备运行到：

```text
CurrentFuelEnergyRemaining = 427.3 / 1000
```

被暂停、存档或从 Composite 拆下后怎么办？

➡️ **我推荐严格保留这个状态。**

也就是说它和 Engine Durability 一样属于具体设备实例：

```text
Power Equipment ItemInstance
├─ Durability
├─ Behavior Runtime State
└─ CurrentFuelEnergyRemaining
```

因此：

- Pause/断电不清空；
- Save/Load 必须恢复；
- 拆卸后进入 `ItemInstance` 并保留；
- 安装到另一 Composite 后仍然是原来的剩余燃料；
- 设备被真正摧毁时剩余 Fuel Energy 一并损失。

这正好复用此前已经确定的“有复杂 Runtime State 的设备进入 ItemInstance，而不是压回 Stack”的规则。fileciteturn0file0L35-L38

---

本轮 Frontier 是 **Q322–Q329**。

这一轮结束后，生产运行模型基本只剩最后几层：**Buffer 清空与下一 Batch 的 Tick 顺序、生产计划是一次 Batch 还是持续目标、Fuel/Power 在共享库存中的仲裁，以及 Recovery DAG 的实际执行语义**。

---

## 37. User  (2026-09-10 12:50:50 UTC)

Q322 A
Q323 同意
Q324 同意
Q325 同意
Q326 A
Q327 同意
Q328 全部保存
Q329 同意

---

## 38. Assistant  (2026-09-10 12:51:11 UTC)

> [思考摘要] 澄清生产计划：我正在确认生产计划的最小定义：它是持续重复的工艺选择，还是包含批次数量与库存目标的一次性指令？我倾向于让低层持续运行，由上层控制启停和目标。 / 澄清了生产计划

Q322–Q329 之后，生产运行的异常路径基本闭合：设施各自堵塞、已完成 Buffer 有真实货物语义、在制品与缓冲状态都持久化，复杂设备的燃料余量也跟随实例保存。

下一层真正的 frontier 只剩三类根决策：**Tick 内状态推进顺序、Production Plan 的最低层语义、以及 Recovery 是否复用同一套 Batch 执行框架。** 这些决定之后，才适合继续问“缺料自动恢复、回收产率、燃料争抢”等更下游的问题。

> [思考摘要] 整理设计问题：我正在继续拆分关键决策：生产计划是否持续循环、恢复流程如何处理不同物品、回收产出是否受物品状态影响，以及供能设备采用需求驱动还是持续满负荷运行。 / 整理了设计问题

❓ **Q330** - **生产状态是否严格遵守 Tick Barrier，不允许同 Tick 连锁推进**：现有 Simulation 已确定 Behavior Evaluation 只读取 committed state，然后统一 Resolve / Commit。fileciteturn0file11L44-L55

例如某 Tick 开始时：

```text
Factory
OutputBuffer = 3
ActiveBatch = none

Settlement free capacity = 3
```

本 Tick 可以把 Buffer 清空。那么它是否还能在**同一个 Tick**立即检查原料并启动下一 Batch？

➡️ **我推荐不能。**

采用严格状态屏障：

```text
Tick N committed state:
BlockedOnOutput

Tick N:
Buffer transfer succeeds

Tick N+1 committed state:
Idle

Tick N+1:
may request new Batch
```

同理，一个 Batch 在 Tick N 完成，不会在 Tick N 内再完成“输出 → 清仓 → 新 Batch 启动”的连锁。这样每一步都只根据 Tick 开始时的权威状态决策，避免同 Tick 内提交顺序产生 Gameplay 差异。

---

❓ **Q331** - **最低层 Production Plan 是持续生产，还是离散生产订单**：

目前更高层 Player / AI 决定“生产什么”，底层只执行。但最低层计划还可以有两种形态。

**A. 持续选择：**

```text
Facility Production Plan
├─ ProcessId
├─ Enabled
└─ Priority
```

只要 Enabled，就不断尝试：

```text
Start Batch
→ Finish
→ Start next Batch
→ ...
```

库存不足等情况只是等待。

**B. 离散订单：**

```text
Produce Mechanical Components × 37 batches
```

执行完就自动停。

甚至进一步加入目标库存：

```text
Produce until stock = 1000
```

➡️ **我推荐 A 作为底层唯一语义。**

“生产 37 批”“保持库存 1000”“根据价格切换产品”都属于更高层 Production Policy / AI，可以通过修改 `Enabled / ProcessId` 实现。

这样资源生产底层保持简单：

> **设施当前被配置为持续执行某个 Process，直到上层改变配置。**

---

❓ **Q332** - **Recovery 是否复用与 Production 完全相同的 Batch Runtime 框架**：

我们已经确定 Forward Production DAG 与 Recovery DAG 是两个独立无环图，但这不代表需要两套执行引擎。

我建议：

```text
ProductionProcessDefinition ─┐
                             ├→ Batch Execution
RecoveryProcessDefinition ───┘
```

共同拥有：

```text
integer Inputs / Outputs
WorkRequired
Power intensity
Labor intensity
Facility compatibility
ActiveBatch
OutputBuffer
Priority
```

区别只在**领域语义和图约束**：

- Production Process 属于 Forward DAG；
- Recovery Process 属于 Recovery DAG；
- 两张图分别验证无环。

➡️ **我推荐如此。**

即“两个 Process 类型，一套 Runtime 执行机制”，避免出现 RecoverySystem 与 ProductionSystem 两套几乎相同的代码和规则。

---

❓ **Q333** - **Recovery 是否同时接受 Stack Good 与具体 ItemInstance**：

例如普通 Structural Block 可以：

```text
Structural Block ×10
→ Recovery
→ Structural Metal ×...
```

而受损 Engine 则是：

```text
Engine ItemInstance #812
Durability = 43/100
→ Recovery
→ ...
```

➡️ **我推荐两者都允许，但输入语义明确区分。**

```text
Commodity Input
DefinitionId + integer Quantity

Item Input
specific ItemInstance identity
```

Recovery 一个具体二手设备时，必须真正消费那个 ItemInstance；不能只从同 Definition 的 Stack 里随便扣一个单位代替它。

---

❓ **Q334** - **受损 ItemInstance 的回收产量是否应受其剩余状态影响**：

如果一台新品 Engine 和一台只剩 1% Durability 的 Engine 都能得到完全相同的：

```text
Structural Metal ×5
Mechanical Components ×3
```

那么损坏几乎不会降低其物质价值，玩家甚至可以把设备用到报废前再全额回收。

➡️ **我推荐回收产量与剩余完整度相关，而且完全确定。**

最简单的首版基准可以是：

```text
ConditionRatio =
CurrentDurability / MaxDurability
```

然后由 Recovery Process 根据该比例计算整数输出。

但我**不建议现在冻结具体取整公式和回收率**；这里只先确定硬语义：

> **ItemInstance 的 Runtime Condition 可以影响 Recovery Yield；Recovery 不保证返还新品的完整物料价值。**

以后可以让特殊 Behavior State 也参与，但首版首先使用 Durability 即可。

普通未个体化 Stack Good 则按其标准完整状态处理。

---

❓ **Q335** - **Power Equipment 是按实际需求燃烧 Fuel，还是只要启动就按额定功率持续燃烧**：

假设整个聚居地目前只需要 20 Power，但 Power Equipment 最大可以提供 100。

**A. Demand-driven**

```text
Demand = 20
→ generate 20
→ consume FuelEnergy proportional to 20
```

**B. Fixed-output**

```text
Equipment enabled
→ generate 100
→ consume Fuel as if generating 100
→ unused 80 simply wasted
```

➡️ **我推荐 A：按实际被 Resolver 分配的 Power 输出消耗 FuelEnergy。**

因为我们不模拟电网、蓄电与发电调度，固定满功率燃烧只会产生没有对应控制系统的燃料浪费。

因此：

```text
Power demand
→ Resolver
→ actual delivered Power
→ consume corresponding FuelEnergy
```

闲置设备不烧 Fuel；低负荷时按低负荷消耗。

---

❓ **Q336** - **Composite Construction 本身是瞬时原子行为，还是需要时间与生产能力的 Construction Job**：

我们此前只确定了：

```text
Exact finished Voxel Goods
→ Atomic Consume
→ CompositeInstance
```

但还没有确定装配是否耗时。

对于一整列车或大型建筑，如果库存一齐全就瞬间出现，制造产业可能缺少一个重要约束。

两个方向：

**A. 瞬时 Construction**

只要 BOM、位置和合法性满足：

```text
consume goods
→ instantiate immediately
```

**B. Construction Job**

```text
Finished Voxel Goods
+ Construction Capability
+ Labor
+ Time / Work
(+ Power when appropriate)
        ↓
CompositeInstance
```

生产 Engine 和制造 Engine 本身仍属于 Production DAG；这里的 Work 只是**把已有成品 Voxel 装配成 Composite**。

➡️ **我推荐 B。**

尤其是玩家自己的列车与大型建筑，建造时间和施工能力可以成为很自然的经济约束，同时不会要求逐 Voxel 播放施工过程。

不过我建议 **Construction Job 仍是独立于 ProductionProcess 的领域行为**：它输入的是 Blueprint/BOM，输出的是具有身份和空间布局的 Composite，而不是普通 Good。

如果你选择 B，下一轮我们再设计 Construction Job 在什么时候消费 BOM、需要什么设施、失败/取消如何处理；本轮不提前假设这些细节。

---

本轮 Frontier 是 **Q330–Q336**。其中 Q331 会决定后续缺料后的自动恢复语义；Q336 则会打开资源系统最后一条较大的分支——**从库存中的完成品，到真实列车/建筑的施工制造流程**。

---

## 39. User  (2026-09-10 12:59:07 UTC)

Q330 同意
Q331 同意
Q332 同意
Q333 同意
Q334 同意
Q335 A
Q336 B

---

## 40. Assistant  (2026-09-10 12:59:21 UTC)

> [思考摘要] 继续梳理建造规则：我正在沿着已确定的建造任务继续追问：材料何时扣除、取消如何处理、施工中断后能否恢复，以及设施损坏或地点变化时怎样结算。 / 继续梳理建造规则

Q330–Q336 把三条运行路径都接上了：持续生产、Recovery 共用 Batch 执行框架，以及 **Composite Construction 采用独立的、耗时的 Construction Job**。现在最大的未决分支就是施工过程本身；同时，持续生产在缺料时的行为和 Recovery 的首版产率规则也已经没有前置依赖，可以一并处理。

> [思考摘要] 推进建造细节：我正在继续确认建造任务的边界：材料是否一次性扣除、库存来源能否合并、施工位置是否提前锁定，以及建造完成前是否只保留施工状态而不生成实体。 / 推进了建造细节

Q330–Q336 之后，Production 与 Recovery 已经接近闭合。现在把 **Construction Job 的根语义**定下来，同时补完两个已经解锁的 Production/Recovery 分支。现有契约要求 Composite Creation 必须来自显式 Gameplay 行为，因此 Construction Job 很适合作为这一显式创建行为。fileciteturn0file5L11-L17

---

❓ **Q337** - **Construction Job 运行期间是否存在“半成品 Composite”**：假设制造一辆机车需要 10,000 Work，目前进度是：

```text
ConstructionJob
Progress = 6300 / 10000
```

是否应该已经在世界中出现一辆只完成 63% 的机车？

**A. 不存在半成品 Composite。**

Construction Job 是独立 Runtime State；直到完成时才原子创建完整 Composite。

**B. 边施工边实体化 Voxel。**

随着 Progress 推进逐步出现车体。

➡️ **我推荐 A。**

```text
Inventory Goods
      ↓
ConstructionJob
0 ─────────────→ WorkRequired
                  ↓
           Atomic Instantiate
                  ↓
          CompositeInstance
```

这样不会引入“半辆车是否满足 Construction Validity”“施工到一半被打断怎么 Fragment”等大量额外状态。

视觉层以后完全可以显示脚手架、施工动画或半成品模型，但这些不成为权威 Composite。

---

❓ **Q338** - **Construction BOM 是否在 Job 启动时一次性消耗**：

与 Production Batch 类似，有三个方向：

**A. 启动时原子消耗完整 BOM。**

**B. 启动时 Reservation，完成时消费。**

**C. 随施工进度逐步消费。**

➡️ **我推荐 A。**

例如：

```text
Start Construction
├─ validate Blueprint
├─ validate location
├─ validate complete BOM
├─ consume complete BOM atomically
└─ create ConstructionJob
```

货物投入施工后就不再属于库存。

这与 Q253 的原则完全一致：施工消费的是已经生产完成的实际 Voxel Goods，而不是递归展开它们的原料。

---

❓ **Q339** - **同一次 Construction 是否可以组合使用当前位置的列车库存和玩家聚居地库存**：

你在 Q266 已明确玩家只能使用：

> 列车库存 + 当前所处的自己聚居地库存。

例如：

```text
Settlement Inventory:
Structural Block ×80

Train Inventory:
Structural Block ×20

Blueprint needs:
Structural Block ×100
```

是否允许直接满足 BOM？

➡️ **我推荐允许。**

它们都是当前施工地点实际可访问的物质库存，并没有跨地点瞬移。

但启动 Transaction 必须明确记录每种 Good 从哪个 Inventory 扣除，并使用稳定的消费顺序。例如默认：

```text
1. Settlement Inventory
2. Current Train Inventory
```

或者以后允许玩家指定来源。

首版不需要为此增加“先搬入统一施工仓库”的微操。

---

❓ **Q340** - **Construction Job 是否必须预留最终 Composite 的空间**：

如果施工期间世界里没有半成品 Composite，那么另一个玩家操作或 AI 有可能在目标位置建别的东西。

因此需要决定是否存在：

```text
Construction Spatial Reservation
```

它不是 Voxel，也不是 Collision，但阻止冲突的 Construction / Placement 使用该区域。

➡️ **我推荐必须预留。**

流程：

```text
Validate final layout occupancy
        ↓
Create Construction Reservation
        ↓
Perform Work
        ↓
Complete
        ↓
Reservation → Composite occupancy atomically
```

Reservation 不违反“一个 Cell 最多一个 Voxel”，因为它不是 Voxel occupant，而是待施工空间约束。

如果没有这一层，就只能在 Job 完成时重新检查位置，并可能出现“耗时造完了但因为别人放了一块砖而失败”的糟糕结果。

---

❓ **Q341** - **Construction Capacity 是否必须来自实际功能 Voxel**：

Production 已经确定：

```text
Industrial Machinery
→ Production Capability
```

那么 Construction 也不应因为建筑名字叫“车辆工厂”就凭空获得施工能力。

我认为这里已经出现足够明确的 Gameplay Difference，可以正式加入一个新成品：

```text
Construction Equipment
→ ConstructionBehavior
→ Construction Capability
```

它代表装配设备、工业施工设备、吊装/制造工装等宏观集合。

➡️ **我推荐正式加入 `Construction Equipment`。**

于是：

```text
Train Factory / Construction Yard Composite
├─ Structural / Construction Blocks
├─ Construction Equipment ×N
├─ Power Equipment ...
└─ ...
        ↓
Construction Capability
```

不复用 `Industrial Machinery`，因为：

```text
Production Capacity
≠
Composite Construction Capacity
```

二者是明显不同的 Gameplay 能力。

---

❓ **Q342** - **Construction Work 首版是否只按 Composite 的 Voxel 数量计算**：

对于玩家完全自由设计的车辆，我们必须有一种通用算法计算施工时间。

复杂方案是给每种 VoxelDefinition 增加：

```text
AssemblyWork = ...
```

然后：

```text
ConstructionWork =
Σ voxel AssemblyWork
```

但这意味着所有 VoxelDefinition 多一个主要只为施工使用的参数。

简单方案则是：

```text
ConstructionWork =
VoxelCount × BaseWorkPerVoxel
```

所以 500 格车辆比 100 格车辆耗时约 5 倍。

➡️ **我推荐首版采用简单方案。**

即所有 Voxel 在装配 Work 上暂时等权：

```text
1 installed Voxel = N Construction Work
```

Engine 之所以昂贵和复杂，已经体现在它自身的 Production DAG 中；Composite Construction 阶段只模拟“把完成的部件安装起来”。

以后如果 Playtest 明确证明“安装引擎应该显著比安装结构块慢”具有 Gameplay 意义，再增加 `AssemblyWork`。

---

❓ **Q343** - **持续 Production Plan 缺料时是否保持 Enabled，并自动等待恢复**：

Q331 已确定底层计划是持续生产：

```text
ProcessId
Enabled
Priority
```

如果当前缺少 Input，我建议：

```text
Enabled = true
ActiveBatch = none
State = WaitingForInputs
```

而不是自动关闭生产计划。

以后新货物运到：

```text
Train unloads Inputs
↓
next eligible Tick
↓
facility automatically requests new Batch
```

➡️ **我推荐如此。**

同样：

- Power 不足 → Active Batch Pause；
- Labor 不足 → Active Batch Pause；
- 缺 Inputs → 尚未启动的新 Batch 等待；
- Output Buffer 非空 → 该 Facility Blocked。

玩家不需要每次补货后重新打开工厂。

---

❓ **Q344** - **受损 Item 的 Recovery Yield 首版是否采用线性比例 + 向下取整**：

Q334 已确定 Durability 会影响回收结果，现在需要首版明确算法。

假设新品 Recovery Recipe：

```text
Engine ×1
→ Structural Metal ×8
→ Mechanical Components ×6
```

当前：

```text
CurrentDurability / MaxDurability
= 0.5
```

则：

```text
Structural Metal =
floor(8 × 0.5) = 4

Mechanical Components =
floor(6 × 0.5) = 3
```

➡️ **我推荐采用这个确定性公式：**

```text
Recovered_i =
floor(BaseRecoveryOutput_i
      × CurrentDurability
      / MaxDurability)
```

每种 Output 独立计算。

结果可以为 0，不建立小数残余、不随机补偿。

这意味着设计 Recovery Recipe 时应该避免“新品只能回收 1 个某材料”却又希望破损品有平滑回收收益的配方；这是内容设计问题，不需要额外 Runtime State。

---

❓ **Q345** - **Recovery Recipe 是否必须显式设计，而绝不自动反转 Production Recipe**：

例如：

```text
Production:
Mechanical Components ×4
Structural Metal ×3
→ Engine
```

不能因此自动推导：

```text
Engine
→ Mechanical Components ×4
 + Structural Metal ×3
```

否则制造和回收很容易形成无损循环。

➡️ **我推荐严格要求显式 `RecoveryProcessDefinition`。**

例如设计者可以明确规定：

```text
Engine
→ Mechanical Components ×2
 + Structural Metal ×1
```

再结合 Q344 的 Durability 比例进一步产生损耗。

因此：

> Production Recipe 描述怎么制造；Recovery Recipe 描述能回收什么。二者没有自动可逆关系。

这也是我们把 Production DAG 与 Recovery DAG 分开的真正意义之一。

---

❓ **Q346** - **Recovery 是否必须通过真实生产设施执行，而不是库存菜单里的瞬时按钮**：

既然 Recovery 已经复用 Batch Runtime，我建议：

```text
Settlement Inventory
      ↓
Compatible Recycling Facility
      ↓
Recovery Process
├─ Work
├─ Power
├─ Labor
└─ Output Buffer
      ↓
Settlement Inventory
```

而不是右键 Engine → “分解” → 瞬间获得资源。

➡️ **我推荐必须通过兼容 Facility 执行。**

首版仍然可以复用：

```text
Industrial Machinery
→ Production Capacity
```

来提供实际 Work Capacity，只是对应 Building / Facility 被配置为允许 Recovery Process。

因此暂时**不需要再创建 `Recycling Machinery` 商品**。等回收产业真正需要独立设备差异时再拆。

---

本轮 Frontier 是 **Q337–Q346**。

如果这些确定，Construction Job 的下一层才会被解锁：**Power/Labor 如何约束施工、暂停与取消、Construction Facility 被毁怎么办、Spatial Reservation 如何释放、Job 完成时如何从保存的设计生成正确 Voxel Instance，以及 Refit 是否也采用类似的耗时 Job。**

---

## 41. User  (2026-09-12 03:52:48 UTC)

Q337 A 不存在半成品
Q338 A
Q339 允许
Q340 需要预留空间
Q341 同意
Q342 采用简单方案
Q343 同意
Q344 使用该公式
Q345 同意，应当避免无损循环
Q346 同意

---

## 42. Assistant  (2026-09-12 03:52:51 UTC)

Q337–Q346 已经把 Construction 与 Recovery 的核心事务语义补齐：施工无半成品、完整 BOM 启动即消耗、空间预留、Construction Capability 来自真实设备、Recovery 显式且有损。下一层 frontier 只剩 **Construction Job 的运行时状态与失败路径**，以及 **Refit 是否进入同一耗时施工模型**。

❓ **Q347** - **Construction Job 是否也采用 Work + Power + Labor 的统一执行模型**：现在 Construction 已经有 `Construction Equipment → Construction Capability`，但还没明确运行约束。

我建议：

```text
ConstructionJob
├─ WorkRequired
├─ PowerPerWork
├─ LaborPerWork
└─ ProgressWork
```

实际推进速度由：

```text
Construction Capability
Power allocation
Labor allocation
```

共同决定，和 Production 的 Work 模型保持一致，只是 Capability 类型不同。

➡️ **我推荐同意。** 这样 Production 与 Construction 在资源竞争层面统一，但领域对象仍然分开。

---

❓ **Q348** - **Construction Job 中途 Power/Labor 不足时是否只暂停并保留 Progress**：

例如：

```text
ProgressWork = 6200 / 10000
```

停电或劳动力不足。

➡️ **我推荐和 Production 一致：保留 Progress，不损失 BOM，不回退。**

```text
ActualWork = 0
Job stays active
```

恢复条件后继续。

---

❓ **Q349** - **玩家是否允许主动取消 Construction Job**：由于 Q338 已确定完整 BOM 在启动时已经消耗，这里存在和 Production 类似的问题。

**A. 不允许取消，只能暂停。**

**B. 可以取消，并原样返还所有 Voxel Goods。**

**C. 可以取消，但材料损失。**

➡️ **我推荐首版 A。**

理由和 Active Batch 一样：既然材料已经投入施工，不应该在施工 99% 时点击取消就全部恢复。

以后如果需要拆除未完成工程，可以专门设计 Salvage，不在首版增加复杂度。

---

❓ **Q350** - **Construction Facility 被摧毁时 Job 如何处理**：

如果施工设备所在 Composite 被毁，但目标 Composite 尚未生成：

```text
BOM consumed
Progress = 70%
Spatial Reservation exists
```

➡️ **我推荐：Job 失败，已投入 Goods 损失，Reservation 释放。**

不自动返还 BOM。

如果只是部分受损但仍保留有效 Construction Capability，则 Job 暂停或降速；只有负责该 Job 的 Facility 真正失去执行资格/身份时才失败。

以后 Recovery/Salvage 可以扩展事故后的材料回收。

---

❓ **Q351** - **Spatial Reservation 是否属于 Construction Job 的权威 Runtime State 并进入存档**：

我建议必须保存：

```text
ConstructionJob
├─ Blueprint / Definition reference
├─ target Transform / layout placement
├─ reserved cells
├─ ProgressWork
├─ resource provenance if needed
└─ owning / executing facility
```

读档后必须恢复同一施工位置，不能重新找空地。

➡️ **我推荐同意。**

Reservation 不是渲染缓存，而是阻止其他 Construction/Placement 冲突的 Gameplay 状态，因此必须持久化。

---

❓ **Q352** - **Job 完成时是否使用启动时冻结的设计快照，而不是当前 Blueprint 内容**：

假设玩家启动施工后又编辑了同名 Blueprint。

如果完成时重新读取“当前 Blueprint”，就可能出现：

```text
启动时消耗的是旧 BOM
完成时生成的是新 Layout
```

这是严重不一致。

➡️ **我推荐 Job 启动时冻结实际 Construction Definition / Layout Snapshot。**

完成时严格根据该快照 Instantiate。

Blueprint 后续修改只影响新的 Construction Job。

这也符合现有“Definition 不保存实例状态、已存在设计布局不被后续模板修改覆盖”的整体原则。fileciteturn0file0L42-L45

---

❓ **Q353** - **Refit 是否也改成耗时的 Refit Job，而不再永远瞬时原子完成**：

当前正式文档为了首版测试把 Refit 定为 Atomic Transaction，强调的是**结构修改的提交必须原子**，并不一定意味着未来正式 Gameplay 必须零耗时。fileciteturn0file5L46-L59

在完整游戏里，玩家给列车：

```text
移除 Engine
安装新 Engine
增加 Storage Equipment
移动结构块
```

如果瞬间完成，会绕过我们刚建立的 Construction 能力。

我建议区分：

```text
Refit Planning
→ validate final layout
→ derive delta BOM
→ consume required added Goods
→ Refit Job performs Work
→ final structural change commits atomically
```

也就是说：

- 施工过程耗时；
- 中间不暴露半改装车辆；
- 完成时一次性提交最终 Layout；
- 被移动/保留的旧 Voxel Identity 仍按现有规则保留。

➡️ **我推荐正式玩法采用耗时 Refit Job；当前开发测试场景的瞬时 Atomic Refit 只是测试入口/简化操作。**

这是一个比较重要的方向调整，需要你确认。

---

❓ **Q354** - **Refit Job 的成本是否只消费“新增或替换进去”的 Voxel Goods**：

例如：

```text
旧车：
100 Structural Block
2 Engine

新设计：
105 Structural Block
2 Engine
1 Storage Equipment
```

其中两个 Engine 原样保留。

我建议：

```text
Need:
Structural Block ×5
Storage Equipment ×1
```

而不是重新消费整车 BOM。

移动已有 Engine：

```text
old cell → new cell
```

不消费新 Engine，并保留其 Runtime Identity。

替换 Engine：

```text
old Engine removed
new Engine installed
```

才需要一个新 Engine Good；旧 Engine 则进入 ItemInstance 库存。

➡️ **我推荐如此。**

这正好延续当前已经确定的真实 Voxel Identity 规则。fileciteturn0file5L46-L59

---

❓ **Q355** - **Refit Work 是否按“实际修改的 Voxel 数量”计算，而不是整车大小**：

Construction：

```text
Work ∝ total Voxel count
```

Refit 更合理的是：

```text
Work ∝ changed Voxel count
```

例如仅换一个 Engine，不应该因为列车有 1000 个体素而支付整车施工时间。

➡️ **我推荐：**

```text
RefitWork =
BaseWorkPerChangedVoxel
× changed voxel count
```

其中：

- Add 算 1；
- Remove 算 1；
- Move 算 1；
- Rotate 算 1；
- Replace 算 1 个结构修改单位；

首版不进一步给不同操作设不同系数。

---

❓ **Q356** - **正在 Refit 的 Composite 是否仍然可正常运行**：

有两个方向。

**A. Refit Job 期间目标 Composite 被锁定，不能参与正常运营。**

例如列车必须停在自己的聚居地/施工设施中，暂停贸易运行。

**B. 施工后台进行，列车还能继续跑。**

➡️ **我推荐 A。**

施工过程虽然不暴露半成品 Layout，但目标 Composite 应进入明确的：

```text
UnderRefit
```

状态，不能移动、运行生产设备、参与正常运营。

否则会出现“火车一边跑一边在后台换发动机”的语义问题。

完成时才原子切换到新布局。

---

本轮 Frontier 是 **Q347–Q356**。

如果这一轮落定，Construction / Refit 的大框架基本就完整了。之后我们可以转向目前还没真正展开的最后一大片：**市场与生产经济如何从这些真实库存和生产能力中形成 Supply、Demand、Price，以及 AI 聚居地如何决定生产计划**。

---

## 43. User  (2026-09-14 08:19:01 UTC)

Q347 使用统一模型
Q348 保持一致
Q349 同意
Q350 同意
Q351 同意
Q352 同意
Q353 同意
Q354 同意
Q355 同意
Q356 A

---

## 44. Assistant  (2026-09-14 08:19:16 UTC)

Q347–Q356 之后，**Construction / Refit** 这一整条分支已经闭合：两者都采用耗时 Work 模型，施工期间不暴露半成品，完整 BOM 启动即消耗；Refit 只计算实际变更量，并在完成时原子提交最终布局。

现在设计树的 frontier 正式进入资源系统的最终核心：**真实库存、生产与市场如何连接成经济系统**。这里需要先确定“市场是什么、Supply/Demand 从哪里来、Price 怎么形成”；AI 聚居地如何选择生产计划要依赖这些答案，放到后续轮次。

> [思考摘要] 明确市场库存：我正在把市场从抽象供需数值改成真实库存与结算。接下来需要确认：玩家自有聚落间调运是否不经过现金交易；市场价格是否只影响与外部聚落交易；库存是否按聚落统一统计，而非按车站分开维护。 / 明确了市场库存

> [思考摘要] 构建库存供需：我正在考虑用库存差额替代抽象需求：供给等于可出售库存，需求等于目标库存与现有库存的缺口。还需要确认价格是否据此自动生成，以及交易资金是否必须由各方真实持有、不会凭空增加。 / 构建了库存供需模型

现有正式契约已经给市场留下了正确边界：`Value` 是 VoxelDefinition 上稳定的基础价值，真正的 `Price` 必须属于 `Market Context`；而长期核心循环仍然以贸易方案和策略迭代为中心。fileciteturn0file0L21-L27 fileciteturn0file4L34-L39

---

❓ **Q357** - **市场应该属于 Settlement，还是 Station**：列车最终是在站点交易，但一个聚居地未来可能有多个站点/接口。如果每个 Station 各自维护一套库存、价格和经济，就会把同一个聚居地经济人为切开。

➡️ **我推荐一个 Settlement 拥有一个统一 `MarketState`，Station 只是列车访问该市场的交易接口。**

```text
Settlement
├─ Inventory
├─ Production
├─ Population
├─ Treasury
└─ MarketState
      ↑
    Station
      ↑
     Train
```

因此：

- Settlement 没有可访问 Station 时，列车不能与之交易；
- Station 不拥有另一份商品库存；
- 未来即使一个 Settlement 有多个 Station，它们看到的也是同一个市场。

这也符合现有“Settlement ≠ Station ≠ Rail Connection”的既定区分。fileciteturn0file9L9-L15

---

❓ **Q358** - **玩家自己的列车与自己的聚居地之间是否属于 Transfer，而不是 Trade**：

例如玩家列车把 500 Ferrous Ore 运回自己的聚居地。

两个方向：

**A. 仍然按照市场价格“卖给自己”。**

**B. 只是所有权内部的库存 Transfer，不产生买卖和货币变化。**

➡️ **我推荐 B。**

```text
Player Train Inventory
        ↓ Transfer
Player Settlement Inventory
```

没有：

```text
Sale
Price
Profit
Tax
```

真正的 Trade 是**不同经济所有者之间的货物所有权交换**。

这样能清楚区分：

> “我把自己的原料运回基地”

和

> “我把原料卖给另一个城镇”。

---

❓ **Q359** - **Market Supply 是否严格从实际库存派生，而不再拥有独立 Supply Counter**：

Q279 已经确定市场卖出的就是实际库存，因此建议彻底放弃 MVP 那种：

```text
Supply = 100
↓ market tick
Supply magically restores to 150
```

的模型。

但聚居地也不应该把最后一份食物或生产原料全部卖光，所以需要一个**出售策略**。

例如：

```text
Inventory(Fuel) = 1000
ReserveTarget = 400

Sellable Supply = 600
```

➡️ **我推荐：**

> `Supply` 只是 `Inventory + Market Policy` 的派生结果，不是独立 Runtime Quantity。

概念上：

```text
SellableSupply(good)
=
max(0, physical inventory - protected reserve)
```

具体 reserve 如何由 AI/玩家策略产生以后再设计。

---

❓ **Q360** - **Market Demand 是否也不再拥有“自动恢复”的独立 Counter，而从真实经济需求派生**：

同样，我建议不再存在：

```text
Demand = 100
↓ market tick
Demand magically restores
```

而是：

```text
Desired Stock / Need
        -
Current Inventory
        ↓
Buy Demand
```

需求来源可以包括：

```text
人口消费
生产计划所需 Inputs
Construction / Refit 计划
战略储备
其他未来 Gameplay
```

例如：

```text
Settlement wants Fuel stock = 800
Current Fuel = 300
→ potential demand = 500
```

➡️ **我推荐 Demand 也严格成为派生事实。**

这会形成非常强的一条经济不变量：

> **Supply 和 Demand 都不能凭空恢复；它们必须由真实库存、真实生产和真实消费产生。**

---

❓ **Q361** - **聚居地人口是否必须真实消费 Goods，从而形成持续民用需求**：

我们已经设计了：

```text
Staple Food
Prepared Food
Clothing
Consumer Goods
Consumer Electronics
Medical Supplies
```

如果人口只提供 Labor，却从来不消耗东西，这些商品就没有真实经济出口。

➡️ **我推荐人口以宏观方式持续消费实际库存商品。**

例如：

```text
Population
    ↓
Consumption Needs
    ↓
Settlement Inventory
    ↓ remove Goods
```

但仍然保持宏观抽象：

- 不模拟每个居民；
- 不模拟“John 今天吃了两片面包”；
- 每类商品按 Population × Consumption Rate 产生总体需求。

不同 Goods 可以具有不同消费速率，具体数值以后平衡。

这样民用商品会真正形成长期 sink，而不是卖到城市之后永远堆在仓库。

---

❓ **Q362** - **生产和 Construction 的原料需求是否也应该进入市场需求**：

假设一个工业聚居地的持续 Production Plan 每天需要大量：

```text
Ferrous Ore
Coal
```

本地库存不足。

市场应该自然表现为：

```text
高 Ferrous Ore Demand
高 Coal Demand
```

而不是只有居民消费才算 Demand。

➡️ **我推荐所有已确认的经济意图都可以贡献 Demand。**

因此 Market Demand 大致来自：

```text
Demand
├─ Consumption Demand
├─ Production Input Demand
├─ Construction Demand
├─ Refit Demand
└─ Strategic Reserve Demand
```

但注意这只是**需求意图**，不是提前凭空把 Goods 加入库存。

这样生产链和市场才真正是一套系统：工厂开得越多，对上游资源的需求就越强。

---

❓ **Q363** - **价格是否以 `Value` 为长期锚点，以本地供需状态产生动态偏移**：

这里我建议不要让：

```text
Structural Metal.Value = 100
```

意味着永远卖 100。

也不要让 Price 完全脱离 Value，以至于极端状态下随机漂到毫无尺度感的数字。

推荐：

```text
Local Price
=
Definition.Value
× Local Scarcity / Demand Factor
```

其中实际 Factor 由：

```text
当前库存
目标库存 / Demand
可出售 Supply
生产与消费压力
```

等 Market State 推导。

➡️ **我推荐 `Value` 是长期名义锚点，Price 是 Settlement-local Runtime Quote。**

这完全延续现有 Definition / Market Context 分离。fileciteturn0file0L21-L27

具体曲线、上下限和参数暂时不冻结，后续通过经济模拟与 Playtest 调整。

---

❓ **Q364** - **同一 Settlement 是否使用一个统一 Market Price，而不是固定 Buy Price / Sell Price 两套价格**：

常见游戏会人为设计：

```text
Buy = 120
Sell = 80
```

强制加入 spread。

但如果我们的价格本身来自真实库存和供需，那么这层固定 spread 未必必要。

➡️ **我推荐首版使用一个统一的当前 Market Price。**

即某一瞬间：

```text
Fuel Price = 83
```

市场按这个经济状态报价。

买入和卖出本身会改变真实库存，因此后续 Quote 可以变化。

暂时不设计固定：

```text
BuyMultiplier = 1.2
SellMultiplier = 0.8
```

这种纯游戏化价差。

以后如果需要交易费、税收、手续费，可以作为独立经济机制，而不是偷偷埋在两套价格里。

---

❓ **Q365** - **Settlement 是否拥有真实 Treasury，市场不能无限印钱购买货物**：

如果一个贫穷小镇无论如何都能从玩家手里买下：

```text
10,000 Consumer Electronics
```

那么 Demand 和 Price 虽然存在，货币约束却是假的。

我建议每个 Settlement 有真实的：

```text
Treasury / Currency Balance
```

外部交易：

```text
Buyer:
Currency -
Goods +

Seller:
Currency +
Goods -
```

必须同时原子发生。

➡️ **我推荐真实货币余额。**

也就是：

> **Market 不是一个拥有无限资金的自动售货机。**

这会让出口型聚居地通过卖货获得进口能力，也让长期经济形成真实资金循环。

至于货币初始分布、税收、人口是否产生财政收入以及防止经济死锁的机制，都是这个决定的下游问题，下一轮再问。

---

❓ **Q366** - **是否正式废弃旧的“Market Tick 恢复 Supply / Demand”模型**：

现有术语仍然保留了早期 MVP 的“市场刻”和 Supply/Demand 恢复概念，但现在我们已经设计出完全不同、更物理一致的经济系统：

```text
World Resources
→ Production
→ Physical Inventory
→ Consumption / Construction
→ Supply & Demand
→ Price
→ Trade
```

如果还保留“每个 Market Tick 自动恢复供需”，就会出现两套事实来源。

➡️ **我推荐正式废弃恢复式 Market Tick。**

市场状态改为由实际世界状态持续派生/更新。

仍然可以存在某种较低频率的：

```text
Economic Evaluation Interval
```

用于：

- 人口消费结算；
- AI 重新规划生产；
- 价格重新计算；
- 市场策略更新。

但它只是模拟调度频率，不会凭空恢复任何 Goods、Supply 或 Demand。

这意味着后续正式文档需要更新目前术语表中的旧 Supply/Demand/Market Tick 语义，而不是保留兼容层。

---

本轮 Frontier 是 **Q357–Q366**。

如果这些落定，下一轮会真正进入市场模型的下一层：**需求目标如何计算、库存低到什么程度开始买/高到什么程度开始卖、批量交易如何改变价格、Treasury 不足怎么办，以及人口消费与市场短缺会带来什么后果。**

---

## 45. User  (2026-09-14 08:23:51 UTC)

经济和市场模型是很大的一块，不再本次设计范围内，本次设计注意就是资源和生产。经济模型需要之后和人口，聚居地模拟一同设计，现在设计为时尚早

---

## 46. Assistant  (2026-09-14 08:24:07 UTC)

> [思考摘要] 厘清范围边界：我会撤回市场、经济与需求问题，不把它们当作未决事项。当前只继续追问资源与生产：资源采集是否需要专用工具或设施，以及采集过程如何进入生产链。 / 厘清范围边界

对，Q357–Q366 这整条 **Market / Population / Settlement Economy** 分支应当从本次 design tree 中剪掉，全部视为**未讨论、未决定**，以后单独 grilling。当前只保留一个接口边界：资源与生产系统产出/消耗真实 Goods，未来经济系统基于这些事实工作，但本次不定义 Supply、Demand、Price、Treasury 或人口消费。

> [思考摘要] 继续追问采集系统：我正在把资源采集接入现有生产框架。接下来需要确认：采集是否依赖专用设施、如何绑定资源源、采集工作如何消耗劳动力与能源，以及产出是否直接进入统一库存。 / 继续追问采集系统

对，本次边界就收在 **资源定义、获取、生产、回收、库存、Construction/Refit**。经济、人口、市场、聚居地宏观模拟全部留到下一次独立设计。

当前还没闭合的最大一支其实不是市场，而是我们一直只定义了 Source，却还没有定义清楚**“谁在开采 Source、如何把 Extraction 接入已经建立的 Work/Facility 模型”**。这一轮先解决它，同时补上 Process 与 Facility 的最后几个结构问题。

---

❓ **Q367** - **自然资源开采能力是否也必须来自实际的设施 Composite**：现在 Resource Source 是世界 Voxel，不能让 Settlement 仅凭一个抽象数值：

```text
MiningCapacity = 100
```

就隔空开采周围矿物。

➡️ **我推荐和 Production 完全一致：实际 Extraction 必须由真实的生产/采集设施提供。**

例如：

```text
Mining Facility Composite
        ↓
Extraction Capability
        ↓
Resource Source Voxel
        ↓
Raw Goods
```

林业也同理。

这样玩家建设资源产业的链条就是：

```text
制造采集设备
→ 建造采集设施
→ 获得 Extraction Capacity
→ 开采真实 Source Voxels
```

而不是 SettlementArchetype 自带资源产量。

---

❓ **Q368** - **是否增加通用的 `Extraction Equipment` 成品 Good**：

我们目前已有：

```text
Industrial Machinery
Storage Equipment
Power Equipment
Control Equipment
Construction Equipment
```

Extraction 现在也已经证明拥有独立 Gameplay Capability。

可以：

**A.** 复用 Industrial Machinery。

**B.** 增加：

```text
Extraction Equipment
→ ExtractionBehavior
→ Extraction Capability
```

然后矿场、伐木设施等都通过它获得采集能力。

➡️ **我推荐 B。**

因为“制造产品”和“从世界资源体素中采集原料”是两种明显不同的能力。

首版仍然只设计一个通用 `Extraction Equipment`，不马上拆：

```text
Mining Drill
Logging Machinery
Oil Pump
...
```

如果以后不同资源采集方式能产生足够 Gameplay Difference，再拆成具体产品。

---

❓ **Q369** - **Extraction 是否采用“连续 Work → 离散整数 Damage”的执行模型**：

Source 的 `CurrentDurability` 是整数；每个 Effective Damage 又决定确定性的 Raw Good Yield。

因此我建议不要每 Tick 直接造成小数 Damage，而是：

```text
ExtractionJob
├─ Target Source Voxel
├─ ProgressWork
└─ WorkPerDamage
```

设备持续提供 Work：

```text
0 → ... → WorkPerDamage
          ↓
      commit 1 Extraction Damage
          ↓
Source CurrentDurability - 1
          ↓
calculate deterministic Yield
```

然后 Work 继续积累。

➡️ **我推荐这种模型。**

这样我们同时得到：

- Work / Power / Labor 可以连续分配；
- Source Durability 永远保持整数；
- Yield 规则继续使用 Q206 已确定的整数比例；
- 开采最终仍通过正式 Damage Transaction；
- 普通 Weapon Damage 仍然不会产生 Raw Good。

---

❓ **Q370** - **Extraction Job 是否一次明确指向一个具体 Source Voxel**：

底层可以有两种方式：

**A.**

```text
ExtractionJob
Target = exact WorldVoxelCoord
```

该格耗尽后 Job 结束；更高层 Extraction Plan / AI 再选择下一格。

**B.**

底层 Job 直接指向“一整片矿区”，自己不断寻找下一个资源格。

➡️ **我推荐 A。**

和 Production System 不替玩家决定生产什么一样，Extraction Runtime 也不应该偷偷承担资源规划 AI：

```text
Player / Settlement AI
       ↓
Extraction Plan
       ↓
choose exact Source
       ↓
Extraction Job
```

以后上层可以很方便地设置：

> 自动开采这片区域中的 Ferrous Ore。

但底层每次正在挖哪一个 Voxel 始终明确且确定。

---

❓ **Q371** - **Extraction 的 Raw Goods 是否复用 Facility Output Buffer**：

假设采矿获得：

```text
Ferrous Ore ×3
```

但 Settlement Inventory 已满。

➡️ **我推荐完全复用已经确定的 Output Buffer 规则。**

```text
Extraction
    ↓
Facility-local OutputBuffer
    ↓
尽可能自动进入 Settlement Inventory
```

Buffer 非空：

```text
该 Extraction Facility 暂停
```

而不是继续挖矿并创造无限缓冲。

这样 Production、Recovery、Extraction 三种物质转换行为都拥有相同的输出堵塞语义。

---

❓ **Q372** - **Primary Production 是否不模拟“每块农田/每棵作物”，仍然只是设施级宏观生产**：

我们已经确定：

```text
Grain Farming
Produce Farming
Livestock Production
Fiber Farming
Forestry
```

都是普通 Production Process。

现在要明确空间粒度。

一种方向是实际生成农作物 Voxel、每格田地成长、逐格收获。

另一种则是：

```text
Farm Building Composite
+ suitable Site
+ Industrial/Production capability
+ Labor
+ Power（若该 Process 需要）
        ↓
Grain
```

➡️ **我推荐后者。**

首版不模拟每棵小麦、每头牲畜。

农场、林场、畜牧设施都是实际 Building Composite；它们受到 Site / Environmental Requirements 约束，但产出通过宏观 Production Process 发生。

这符合你希望生产系统“精致但服务经济”，而不是把游戏转成农业模拟器。

---

❓ **Q373** - **Production Process 的 Goods 输入是否永远要求确切 `VoxelDefinitionId`**：

例如 Engine 的 Process 需要：

```text
Mechanical Components
Electrical Components
Control Equipment
```

是否允许写：

```text
any tag: mechanical_component
```

然后自动接受各种替代品？

➡️ **我推荐严格使用确切 DefinitionId。**

如果以后存在两种合法生产路线：

```text
Process A:
Material X → Product

Process B:
Material Y → Product
```

就显式建立两个 `ProductionProcessDefinition`。

这正好延续 Q192：

> 替代路线是明确设计的 Process，而不是 Tag 驱动的自动配方替换。

---

❓ **Q374** - **Facility 与 Process 的兼容性是否采用数据驱动 Tag，而不是硬编码 Process 列表**：

Q297 已确定：

> Facility configuration 决定可以运行什么，Industrial Machinery 决定速度。

现在有两种实现语义：

**A. Facility 显式列出所有 ProcessId**

```text
allowed_processes:
- core:ferrous-refining
- core:conductive-refining
...
```

**B. Process 和 Facility 使用生产领域 Tag 匹配**

例如：

```text
Process:
required_facility_tags = [metallurgy]

Smelter:
facility_tags = [metallurgy]
```

➡️ **我推荐 B。**

这样新增一个新的冶炼 Process 不需要修改所有 Smelter Definition。

但这些 Tag **只负责兼容性分类**：

```text
metallurgy
assembly
textile
food_processing
agriculture
recovery
...
```

不提供产能。

真实产能仍然来自实际 Machinery Voxel，保持 Q280/Q297 的边界。

---

❓ **Q375** - **长期不可再生资源补充是否采用“新资源区出现”，而不是原矿重新长回来**：

你之前提出：

> 某处资源彻底耗尽后，在世界某处尚未开发区域刷新新的资源，等待玩家或 AI 探勘。

我认为这个方向可以正式冻结，但需要避免把它误解成单个 Voxel Respawn。

➡️ **我推荐定义为 `Resource Renewal Event`：**

```text
Existing resource area depleted
          ↓
Resource Renewal Event
          ↓
select eligible undeveloped region
          ↓
generate a new Resource Source cluster
```

明确禁止：

```text
挖掉 Ferrous Source
↓
过一会原地重新出现
```

同时，新资源区的选择必须继续服从整个项目的确定性原则：由 `WorldSeed + stable event identity` 派生，而不是依赖调用顺序。现有模拟要求所有 Gameplay Random 最终源于 WorldSeed，并采用显式稳定的随机键。fileciteturn0file11L66-L72

**具体触发阈值、间隔、新矿区大小和“undeveloped”的精确定义暂不冻结**，作为后续 World Generation / Exploration 与资源系统结合时的调参和细化问题。

---

本轮是 **Q367–Q375**。

如果这一轮落定，资源获取侧基本闭合。之后剩下的主要工作会变成**内容 DAG 本身的补全**：给 `Industrial Machinery / Extraction Equipment / Power Equipment / Storage Equipment / Construction Equipment / Control Equipment / Medical Supplies` 等已经确定存在的 Goods 建立合理的上游依赖，并检查整张 Production DAG 和 Recovery DAG 是否完整、无环、没有无意义节点。

---

## 47. User  (2026-09-14 08:29:22 UTC)

Q367Q367 同意
Q368 同意
Q369 同意
Q370 同意
Q371 同意复用
Q372 同意
Q373 同意
Q374 同意
Q375 同意

---

## 48. Assistant  (2026-09-14 08:29:36 UTC)

> [思考摘要] 梳理依赖前沿：我正在检查内容DAG与恢复图的剩余分歧。基础商品、材料、组件和成品已基本确定；下一步需要确认哪些中间品必须保留，以及医疗、能源、建造链的依赖边界。、】【 / 梳理了依赖前沿信息

Q367–Q375 之后，资源系统的**获取、生产执行、回收、库存、Construction/Refit**机制已经基本闭合。现在 design tree 的 frontier 正式回到内容层：把我们已经确认存在的 Goods 接成一张真正完整、无环、不过度细分的 **Production DAG 骨架**。这一轮只确定“依赖关系”，不碰具体整数配方比例。

> [思考摘要] 确定生产边界：我正在锁定基础配方：冶金是否消耗煤，炼油是否强制联产燃料与化工原料，食品、聚合物、组件、医疗用品和功能设备分别需要哪些必需投入。 / 确定了生产边界

❓ **Q376** - **基础材料加工路线是否先冻结为这一组 canonical processes**：

我建议第一版内置内容采用：

```text
Ferrous Ore + Coal
→ Structural Metal

Conductive Ore
→ Conductive Metal

Rare Mineral
→ Specialty Material

Stone
→ Construction Material

Silica
→ Glass

Timber
→ Lumber

Natural Fiber
→ Textile
```

这里最值得确认的是 `Coal`：我建议让基础 `Structural Metal` 冶炼明确消耗 Coal，使煤不仅是发电 Fuel 的上游，还成为重工业物流的重要资源。

以后完全可以增加不使用 Coal 的替代冶金 Process，但首个标准路线先保持这一条。

➡️ **推荐采用以上骨架。**

---

❓ **Q377** - **Crude Oil Refining 是否成为第一个真正有意义的多输出 Process**：

我们已经保留：

```text
Crude Oil
Fuel
Chemical Feedstock
Polymer
```

现在可以选择：

**A. 一个 Refining Process 同时输出：**

```text
Crude Oil
→ Fuel
+ Chemical Feedstock
```

然后：

```text
Chemical Feedstock
→ Polymer
```

**B. 两个独立 Process：**

```text
Crude Oil → Fuel
Crude Oil → Chemical Feedstock
```

➡️ **我推荐 A。**

这是一个多输出真正能创造决策而不是制造噪声的场景：燃料产业和化工产业被炼化能力联系起来。如果某一侧积压，也会成为实际生产规划问题。

---

❓ **Q378** - **食品 DAG 是否采用这一最小骨架**：

```text
Grain
→ Staple Food
```

以及：

```text
Grain
+ Produce
+ Animal Products
→ Prepared Food
```

`Prepared Food` 因此成为三条农业供应链的汇聚点，而不是简单的 Grain 二次加工品。

➡️ **我推荐采用。**

暂时不设计更多食品 Process；以后人口/聚居地经济设计时，如果消费结构证明需要，再扩充。

---

❓ **Q379** - **三个 Component 是否采用“逐渐增加工业复杂度”的依赖关系**：

我建议：

```text
Structural Metal
+ Polymer
→ Mechanical Components
```

```text
Conductive Metal
+ Structural Metal
→ Electrical Components
```

```text
Electrical Components
+ Specialty Material
+ Glass
+ Polymer
→ Electronic Components
```

这样 Electronic Components 并不是直接由几个矿石跳跃制造，而需要已有电气工业作为上游。

➡️ **我推荐采用。**

这会形成比较清晰的工业纵深：

```text
basic metallurgy
    ↓
mechanical / electrical industry
    ↓
advanced electronics
```

但它依旧只是 DAG，没有 Tier 规则。

---

❓ **Q380** - **民用成品是否采用下面这组依赖骨架**：

除了 Q378 的食品，我建议：

```text
Textile
→ Clothing
```

```text
Lumber
+ Polymer
+ Structural Metal
+ Glass
→ Consumer Goods
```

```text
Electronic Components
+ Polymer
+ Glass
→ Consumer Electronics
```

```text
Chemical Feedstock
+ Textile
+ Polymer
→ Medical Supplies
```

这里 `Medical Supplies` 不再要求增加 Pharmaceutical Chemical 等中间节点；当前化工抽象已经足够。

➡️ **我推荐采用。**

---

❓ **Q381** - **三种首版 Construction Goods 是否采用最直接的一步加工**：

我们已经冻结首版只有：

```text
Structural Block
Construction Block
Rail
```

我建议：

```text
Structural Metal
→ Structural Block

Construction Material
→ Construction Block

Structural Metal
→ Rail
```

也就是说它们虽然生产链简单，但依然必须先被生产为真正的成品 Voxel Good，不能在 Construction 时直接拿材料代替。

➡️ **我推荐采用。**

Rail 的曲线、坡道等仍然只是实例拓扑，不改变商品 Definition，这与铁路现有设计一致。fileciteturn0file6L23-L32

---

❓ **Q382** - **第一批通用功能设备是否采用标准化工业 BOM，而不为每一种设备发明独有材料链**：

目前已经确定的实际 Goods 有：

```text
Industrial Machinery
Storage Equipment
Power Equipment
Control Equipment
Extraction Equipment
Construction Equipment
```

我建议它们先采用高度复用的上游：

```text
Industrial Machinery
= Structural Metal
+ Mechanical Components
+ Electrical Components

Storage Equipment
= Structural Metal
+ Mechanical Components

Power Equipment
= Structural Metal
+ Mechanical Components
+ Electrical Components

Extraction Equipment
= Structural Metal
+ Mechanical Components
+ Electrical Components

Construction Equipment
= Structural Metal
+ Mechanical Components
+ Electrical Components

Control Equipment
= Electrical Components
+ Electronic Components
+ Polymer
```

➡️ **我推荐如此。**

`Industrial / Extraction / Construction / Power` 即使上游相似，也因为实际 Behavior/Capability 完全不同而有充分理由成为不同成品商品。

现在没有必要为了让它们“配方看起来不同”而强行加入无意义材料。

---

❓ **Q383** - **Animal Products 的 Primary Production 是否明确消费 Grain**：

其它 Primary Production 可以主要依靠 Site + Labor + Work：

```text
Grain Farming → Grain
Produce Farming → Produce
Fiber Farming → Natural Fiber
Forestry → Timber
```

但畜牧可以设计为：

```text
Grain
+ Livestock Production
→ Animal Products
```

这里不增加 `Feed` 这种中间品。

➡️ **我推荐让 Animal Products 消耗 Grain。**

这会形成非常自然的农业内部贸易：

```text
粮食产区
→ 畜牧区
→ Animal Products
```

同时也解释了为什么畜牧业不是一个完全无物质输入的无限 Raw Good Source。

---

❓ **Q384** - **Primary Production 的其他四种 Process 是否允许完全没有 Goods Input**：

也就是：

```text
[Site + Labor + Work]
→ Grain

[Site + Labor + Work]
→ Produce

[Site + Labor + Work]
→ Natural Fiber

[Suitable forestry site + Labor + Work]
→ Timber
```

其“投入”主要来自环境、时间和生产 Factors，而不是为了形式完整强行加入：

```text
Seed
Water
Fertilizer
Sapling
```

等商品。

➡️ **我推荐同意。**

这与之前 Q225 的原则一致：没有独立 Gameplay 价值的现实投入不应该为了真实性进入 Goods DAG。

---

❓ **Q385** - **内置内容是否遵循“每个商品默认只有一条 canonical Process，替代路线必须证明 Gameplay 价值”的规则**：

Q192 已经允许同一商品存在多种路线，但“允许”不代表应该普遍这么做。

我建议：

> 默认一个 Product 对应一个清晰的标准生产路线。只有替代路线真正创造资源地域、设施需求、成本结构或策略上的差异时，才增加第二条 Process。

例如未来可以出现：

```text
Biological feedstock
→ alternative Polymer
```

但只有当这条路线真的值得玩家选择时才加入。

➡️ **我推荐把这条作为内容设计原则。**

这样 DAG 会保持可理解，而不会迅速变成几十条几乎等价的配方网络。

---

❓ **Q386** - **顶层未来产品族是否只要求“最终从当前 Component / Material 层接出”，本次不再提前指定具体 BOM**：

例如以后某种 Propulsion Equipment 很可能需要：

```text
Mechanical Components
Electrical Components
Control Equipment
Structural Metal
...
```

但具体哪一种 Engine、需要什么，属于列车系统设计。

➡️ **我推荐本次只冻结接口：**

```text
Materials / Components / Functional Goods
                 ↓
Future concrete equipment VoxelDefinitions
```

而不为 `Propulsion Equipment / Train Weapon / Crew Weapon / Train Equipment / Crew Equipment` 这些设计占位符提前写虚构 Recipe。

这样资源系统提供了完整的工业基础，但不会提前决定尚未设计的 Gameplay 产品。

---

本轮 Frontier 是 **Q376–Q386**。

如果这一轮确定，**Forward Production DAG 的首版骨架实际上就基本完整了**。之后 frontier 会转向两件事：一是检查这张 DAG 有没有重复、断链或不必要节点；二是为确实值得回收的 Goods 设计一张克制的 **Recovery DAG**，而不是机械地让所有商品都可逆。

---

## 49. User  (2026-09-14 08:33:48 UTC)

Q376 采用该骨架
Q377 A
Q378 采用
Q379 采用
Q380 采用
Q381 采用
Q382 同意
Q383 同意
Q384 同意
Q385 同意
Q386 同意

---

## 50. Assistant  (2026-09-14 08:34:08 UTC)

> [思考摘要] 梳理生产与回收：我正在明确资源来源的多产出机制、生产过程定义，以及设施和兼容性分类。回收流程需要显式建模、保持各自无环，并通过损耗与产出约束避免循环产生额外资源。

Q376–Q386 已经把 **Forward Production DAG** 的首版骨架基本补齐。下一轮不再新增正向商品，而是做两件事：先检查“哪些东西值得进入回收体系”，再给 **Recovery DAG** 设最小且安全的规则，避免把所有配方机械反转成噪声或无损循环。

> [思考摘要] 明确回收边界：我正在收敛第一版回收系统：只让耐用工业品显式声明可回收产物，允许设备拆成组件或材料，再由独立流程继续处理；原料、燃料、食物和多数消耗品暂不纳入。 / 明确了回收边界

在检查刚刚确定的 Forward DAG 时，有一个需要先修正的接口问题：**Coal 原本被设计为能源资源，但 Q305 后我们把 Power Equipment 的燃料输入统一成了 `Fuel`。** 如果不补一条路线，Coal 在当前 DAG 中实际上只剩冶金用途。因此先做一次一致性检查，再进入 Recovery 的根规则。

---

❓ **Q387** - **Coal 是否增加一条明确的 `Coal → Fuel` 替代路线**：当前已经有：

```text
Crude Oil
→ Fuel + Chemical Feedstock
```

而 Power Equipment 实际消耗的是 `Fuel`。

我建议再允许：

```text
Coal
→ Fuel
```

这不是为了模拟现实中的具体煤化工技术，而是为了给 Coal 恢复我们最初设计的第二个核心用途：**能源**。

这样 Fuel 存在两条真正有 Gameplay 差异的路线：

```text
Crude Oil
→ Fuel + Chemical Feedstock
  高度绑定化工产业

Coal
→ Fuel
  单纯能源路线
```

同时 Coal 还被 Structural Metal 冶炼消耗，于是煤炭产区会面对：

> 用于重工业，还是用于能源？

➡️ **我推荐加入。** 这是 Q192 所允许的“有明确 Gameplay 价值的替代生产路线”，而不是无意义的配方重复。

---

❓ **Q388** - **Recovery 是否严格采用显式 Opt-in，而不是默认“所有 Goods 都可回收”**：

即只有存在明确：

```text
RecoveryProcessDefinition
```

的 Goods 才可以被回收。

没有 Recovery Process：

```text
→ 不能回收
```

而不是系统根据 Production Recipe 自动猜。

➡️ **我推荐严格 Opt-in。**

这样“可回收”本身就是内容设计决定。我们只为值得产生玩法的商品增加 Recovery，继续遵守“不要为了真实性制造噪声”的原则。

---

❓ **Q389** - **首版 Recovery 是否只覆盖耐用工业品，不覆盖普通消耗品**：

我建议首批重点覆盖：

```text
Construction Goods
├─ Structural Block
├─ Construction Block
└─ Rail

Components
├─ Mechanical Components
├─ Electrical Components
└─ Electronic Components

Functional Equipment
├─ Industrial Machinery
├─ Storage Equipment
├─ Power Equipment
├─ Control Equipment
├─ Extraction Equipment
└─ Construction Equipment
```

而默认不回收：

```text
Raw Goods
Basic Materials
Food
Fuel
Chemical Feedstock
Clothing
Medical Supplies
普通民用消费品
```

原因不是这些现实中绝对不可回收，而是目前没有足够 Gameplay 意义。

➡️ **我推荐首版把 Recovery 聚焦于“耐用工业品与设备”。**

以后 Consumer Electronics 等如果真的需要形成废旧电子产业，再显式加入。

---

❓ **Q390** - **Recovery 是否完全复用现有 Goods，而不增加万能 `Scrap` 商品**：

一种常见做法是：

```text
Engine → Scrap
Scrap → Metal
```

但这样会增加一个几乎所有东西都经过的中间节点。

我建议直接：

```text
Equipment
→ Mechanical Components
+ Structural Metal
+ ...
```

具体回收什么由每条显式 Recovery Process 决定。

➡️ **我推荐首版不增加 `Scrap` / `Waste` 万能商品。**

只有以后“废料本身的运输和集中处理”被证明是值得玩的物流问题时，才增加具体废料 Good。

---

❓ **Q391** - **Recovery DAG 是否允许多阶段回收**：

例如：

```text
Power Equipment
↓ Recovery

Mechanical Components
Electrical Components
Structural Metal
```

玩家之后还可以继续：

```text
Mechanical Components
↓ Recovery

Structural Metal
Polymer
```

也就是回收并不要求一次直接还原到最基础材料。

➡️ **我推荐允许，而且优先如此设计。**

这样 Recovery DAG 与生产工业层次保持对应：

```text
Equipment
→ Components / Materials
→ Materials
```

但每一步都有明确损耗。

这会让玩家选择：

> 直接把回收到的 Component 再用于制造，

还是：

> 继续拆成更基础材料。

---

❓ **Q392** - **Recovery 输出是否只能是 Production DAG 中已经存在的 Goods**：

例如不能为了某个回收配方临时创造：

```text
Damaged Metal
Broken Electronics
Recycled Plastic
```

除非这些东西本身拥有独立 Gameplay。

➡️ **我推荐首版要求 Recovery 输出复用已有 Goods。**

例如：

```text
Electronic Components
→ Conductive Metal
+ Specialty Material
+ Polymer
```

具体比例以后确定。

这能防止 Recovery DAG 自己又长出另一棵“废品资源树”。

---

❓ **Q393** - **回收具体 ItemInstance 时，除了 Durability 影响 Yield，其他 Runtime State 默认全部销毁吗**：

例如一个 Power Equipment：

```text
Durability = 70%
FuelEnergyRemaining = 327.4
```

被送入 Recovery 后。

Q344 已经确定 Durability 按比例影响回收产量；但内部还剩的一部分 FuelEnergy 已经不是完整的库存 `Fuel` 单位。

➡️ **我推荐：Recovery 消费 ItemInstance 后，除明确参与 Recovery 计算的状态外，其余 Runtime State 全部销毁。**

因此：

```text
Durability
→ 影响 Recovery Yield

CurrentFuelEnergyRemaining
→ 不返还为 Fuel

其他 Behavior Runtime State
→ 销毁
```

如果玩家不想浪费剩余资源，就应该先正常使用或执行未来显式支持的卸载行为，而不是让 Recovery 自动逆推出库存商品。

---

❓ **Q394** - **Composite 是否禁止直接进入 Recovery Process**：

例如一整辆列车不能：

```text
Locomotive Composite
→ Recovery
→ 500 Structural Metal
```

因为 Composite 不是普通 Goods，而且内部还包含真实 Voxel Identity、Durability 与 Runtime State。

➡️ **我推荐禁止。**

Recovery 的输入仍然是：

```text
Stack Good
或
ItemInstance
```

Composite 如果玩家想拆掉，应先经过一个**显式 Deconstruction / Dismantling 行为**，把仍存在的实际 Voxel 拆回对应 Goods / ItemInstances；之后玩家再决定哪些零件继续使用，哪些送去 Recovery。

这也保持：

```text
Disassembly ≠ Recovery
```

我们在 Q264 已经确立的边界。

---

本轮 Frontier 是 **Q387–Q394**。

如果这些确定，下一轮就会自然分成两支：一支给首批工业品画出具体的 **Recovery DAG 依赖骨架**；另一支处理 Q394 解锁的 **Composite Deconstruction Job**——它与 Construction/Refit 类似，但输出的是原有部件，而不是把它们自动变成原料。

---

## 51. User  (2026-09-14 08:39:11 UTC)

Q387 同意
Q388 同意
Q389 同意
Q390 同意
Q391 同意
Q392 同意
Q393 同意
Q394 禁止

---

## 52. Assistant  (2026-09-14 08:39:27 UTC)

> [思考摘要] 继续锁定回收规则：我正在把回收过程压缩成可仿真的有向无环图，并处理复合拆解只返还通用材料的边界。现在需要用户确认回收节点、损耗口径和工业遗产的封存方式。

❓Q395. 若工业遗产进入回收，是否只允许列入“经历保守拆解、不得保留完整功能”的回收边界？

❓Q396. 是否允许回收图中出现“结构块 → 结构金属”“机电部件 → 结构金属 + 导电金属 + 聚合物”等多产出节点？

❓Q397. 复合拆解是否统一写为：`复合部件 → 若干通用材料`，不进入专业部件库存？

❓Q398. 回收损耗是否按节点设置固定比例，还是按材料族设置统一回收率？

❓Q399. 结构金属回收率是否高于复合电子与聚合物材料？

❓Q400. 禁止回收的对象是否包括未授权封存设备、仍在线任务设备和部分动件生产线？

❓Q401. 回收产生的材料是否必须先进入材料库存，下一时刻才能重新投入生产？

❓Q402. 回收图是否必须保持单向：退役物品 → 通用材料，不允许直接回到同型号成品？

❓Q403. 俘获结束后的工业遗产是否优先转为“遗产封存存量”，而不是自动进入回收队列？

❓Q404. 是否规定遗产封存会占用静态库存容量，但不占用生产维护劳力？

❓Q405. 回收阶段是否只处理已登记的退役民用、工业、建筑和交通资产？

❓Q406. 是否需要保留一条特殊的“原址保留”路径，用于承担星系中心与文化地标的遗产角色？

❓Q407. 回收后的材料是否允许跨材料族降级，例如高纯电子材料只能回收为通用金属，而不能回到电子材料？

❓Q408. 复合拆解作业是否消耗机械维护与自动化容量，而非任务劳力？ / 继续锁定回收规则

Q387–Q394 之后，Recovery 的边界已经很干净：**显式 opt-in、有损、多阶段、只输出既有 Goods，且 Composite 本身绝不直接进入 Recovery。** 现在 frontier 正好分成两支：一支补齐首批 **Recovery DAG**；另一支设计 **Composite Deconstruction**，把真实部件拆回库存而不是回收到原料。

❓ **Q395** - **首批结构类 Goods 的 Recovery DAG 是否采用最直接的一步回收**：

对于已经冻结的三个 Construction Goods，我建议：

```text
Structural Block
→ Structural Metal

Construction Block
→ Construction Material

Rail
→ Structural Metal
```

实际返还数量显著低于制造投入，由 Recovery Recipe 明确决定，再叠加 Q344 的 Durability 比例。

➡️ **我推荐采用。** 这三种结构件没有必要再经过额外“废料”节点。

---

❓ **Q396** - **三个 Component 是否采用下面的多阶段 Recovery 骨架**：

```text
Mechanical Components
→ Structural Metal
 + Polymer
```

```text
Electrical Components
→ Conductive Metal
 + Structural Metal
```

```text
Electronic Components
→ Electrical Components
 + Specialty Material
 + Polymer
```

这里特意让 `Electronic Components` 可以回收到 `Electrical Components`，而不是一步全部拆回基础材料，因此还能继续进入下一层 Recovery：

```text
Electronic Components
→ Electrical Components
→ Conductive Metal / Structural Metal
```

➡️ **我推荐采用。**

`Glass` 虽然参与 Electronic Components 的制造，但没有必要要求所有上游投入都能被回收；Recovery 本来就不是 Production 的逆函数。

---

❓ **Q397** - **六种通用功能设备是否采用下面的 Recovery 骨架**：

```text
Industrial Machinery
→ Mechanical Components
 + Electrical Components
 + Structural Metal

Storage Equipment
→ Mechanical Components
 + Structural Metal

Power Equipment
→ Mechanical Components
 + Electrical Components
 + Structural Metal

Control Equipment
→ Electronic Components
 + Electrical Components
 + Polymer

Extraction Equipment
→ Mechanical Components
 + Electrical Components
 + Structural Metal

Construction Equipment
→ Mechanical Components
 + Electrical Components
 + Structural Metal
```

具体数量以后平衡，本次只确定**能回收到哪些商品种类**。

➡️ **我推荐采用。**

这样 Equipment → Components → Materials 构成非常清楚的有损 Recovery DAG。

---

❓ **Q398** - **Composite Deconstruction 是否也采用耗时 Job，而不是瞬时拆除**：

既然 Construction 和 Refit 都已经需要 Construction Capability，那么整辆列车或整栋建筑的拆解也不应成为一个瞬时菜单按钮。

我建议：

```text
Composite
   ↓
Deconstruction Job
├─ Construction Capability
├─ Work
├─ Power
└─ Labor
   ↓
原子完成
   ↓
原 Composite 消失
+ 原有 Voxels 返回库存
```

➡️ **我推荐采用。**

并且继续复用 `Construction Equipment → Construction Capability`，不新增 `Deconstruction Equipment`。

---

❓ **Q399** - **Deconstruction 是否返回“原来的部件”，而不是生成新的同类 Goods**：

例如列车里有：

```text
Engine #812
Durability = 63/100
Behavior State = ...
```

Deconstruction 后应该仍然是：

```text
Engine #812
Durability = 63/100
Behavior State = ...
```

而不是销毁 #812，再生成一个全新 Engine。

➡️ **我推荐严格保留真实 Voxel Identity 与 Runtime State。**

更一般地：

```text
pristine + 无特殊 Runtime State
→ 可以重新并入 Stack

damaged / 有 Runtime State / 有长期 Identity
→ ItemInstance
```

因此一块受损 Structural Block 即使没有复杂 Behavior，也不能因为拆进库存就自动恢复耐久。

这延续现有“RuntimeId 是身份、移动零件保持状态”的规则。fileciteturn0file5L46-L50

---

❓ **Q400** - **Refit / Deconstruction 在启动前是否必须保证返回部件有足够库存空间**：

这是与普通 Production 不同的场景。

Production 输出未知何时遇到满仓，因此我们用了 Output Buffer。

但 Refit / Deconstruction 的返回件在 Job 启动前就完全已知。如果允许没有空间也启动，就需要为整列列车建立可能非常巨大的临时 Buffer。

➡️ **我推荐在启动 Job 前检查并预留所需的 Storage Capacity。**

例如拆车预计返回 450 个库存单位：

```text
accessible inventory free capacity >= 450
```

才允许开始。

Job 启动后这部分容量成为 Reservation；其他入库不能占用。

这样：

- 不需要巨大 Deconstruction Buffer；
- 不会完成后无处放零件；
- 不会利用拆车流程绕过仓储容量。

这里是对 Q309 的有意识例外，因为 **Deconstruction 的输出在开始前就是精确已知的现有部件**。

---

❓ **Q401** - **Deconstruction 期间 Composite 是否保持完整存在并继续占据原空间**：

Q337 已确定 Construction 不存在半成品；反方向我建议同样保持原子性：

```text
Deconstruction Progress 0–99%
→ 原 Composite 完整存在
→ 原 Occupancy 不变
→ 不逐 Voxel 消失

100%
→ Atomic Commit
→ Composite removed
→ Voxels transferred to inventory
```

➡️ **我推荐如此。**

因此 Deconstruction 不需要额外 Spatial Reservation：目标 Composite 自己正在占据这些 Cell。

---

❓ **Q402** - **正在 Deconstruction 的 Composite 是否进入锁定状态**：

和 Refit 一样，我建议：

```text
UnderDeconstruction
```

期间：

- 列车不能运行；
- 建筑不能执行生产；
- 不能同时 Refit；
- 不能同时启动第二个 Deconstruction；
- 仍然是世界中的真实 Composite。

➡️ **我推荐采用。**

否则会出现一辆车一边运行、一边被后台拆解的问题。

---

❓ **Q403** - **Deconstruction Job 是否仍然不允许主动取消**：

虽然原 Composite 在 100% 前没有变化，但如果允许 99% 时无代价 Cancel，就会让施工 Work 本身变得没有承诺性。

➡️ **我推荐首版继续统一规则：Active Deconstruction Job 不允许取消，只允许暂停。**

Construction、Refit、Deconstruction 因此都使用一致的正式 Gameplay 语义。

---

❓ **Q404** - **Deconstruction Work 是否简单按 Composite 总 Voxel 数计算**：

与 Construction 对称，我建议：

```text
DeconstructionWork
=
VoxelCount × BaseDeconstructionWorkPerVoxel
```

不按 Engine、Structural Block 等分别设置拆卸难度。

`BaseDeconstructionWorkPerVoxel` 可以与 Construction 的参数不同，但都是统一调参值。

➡️ **我推荐采用。**

仍然坚持“先用最简单模型，Playtest 证明不足后再细化”。

---

❓ **Q405** - **Deconstruction 过程中如果目标 Composite 结构发生外部变化，Job 是否立即失败**：

目标在施工期间仍是真实世界对象，因此未来仍可能受到 Damage。

例如：

```text
开始拆解时：500 Voxels
Progress = 60%

随后事故摧毁了其中 30 Voxels
```

启动时冻结的“最终返回哪些零件”已经不再成立。

➡️ **我推荐：任何改变目标 Voxel Layout / Identity 集合的外部结构修改都会使 Deconstruction Job 失效。**

处理方式：

```text
structural change
→ Deconstruction Job fails
→ Progress discarded
→ Storage reservation released
→ Composite 保持事件发生后的真实状态
```

不会按照旧快照返还已经不存在的部件。

普通 Durability 变化如果没有导致 Voxel 消失，则 Job 可以继续；完成时返回其**最新实际 Durability/Runtime State**。

---

本轮 Frontier 是 **Q395–Q405**。

如果这一轮落定，**Recovery 与 Composite 拆解生命周期基本就闭合了**。之后我会做一次完整的资源/生产 design-tree audit：把目前所有确定的 Source、Raw Goods、Materials、Components、Finished Goods、功能设备、Production Processes、Recovery Processes 和 Job 类型整理成一张总图，并只针对真正还存在的结构性缺口继续提问，而不再为了增加内容而增加内容。

---

## 53. User  (2026-09-14 08:44:05 UTC)

Q395 采用
Q396 采用
Q397 采用
Q398 采用
Q399 同意
Q400 同意
Q401 同意
Q402 同意
Q403 同意
Q404 采用
Q405 同意

---

## 54. Assistant  (2026-09-14 08:44:20 UTC)

> [思考摘要] 继续审查缺口：我正在基于现有资源、生产链和采掘规则，继续检查真正尚未定案的结构问题。下一步重点会放在工厂建造、设备依赖、库存流转，以及生产失败或中断时的处理。 / 继续审查缺口

> [思考摘要] 厘清空间与归属：我还在检查几个可能影响系统闭环的空白：采掘设施如何连接资源源，远程采掘是否允许，生产设施归属哪些实体，以及燃料不足时多个设备如何分配可用能源。 / 厘清空间与归属

这一轮我不继续增加商品节点，而是先做一次 **design-tree audit**。当前 Source、Raw Goods、Forward DAG、Recovery DAG、Inventory、Production、Construction、Refit、Deconstruction 的主干都已经闭合；剩下的真正结构性缺口主要集中在**生产/采集设施与世界空间、Owner Context 的关系**，而不是配方本身。

审计后，Forward/Recovery 两张 DAG 本身目前没有发现断链或环。真正还未冻结的是**空间上的采集资格、Facility 分类的权威来源，以及运行中的设施能否直接进入 Refit/Deconstruction**。这些都属于资源/生产范围。

---

❓ **Q406** - **Extraction Facility 能否隔空开采任意 Source**：Q370 已经确定底层 `ExtractionJob` 指向一个确切 Source Voxel，但还没有规定什么 Source 才是合法目标。

显然不能：

```text
Mining Facility in Settlement A
        ↓
extract
Source Voxel 20 km away
```

否则资源的空间分布就失去意义。

➡️ **我推荐每个 Extraction Facility 拥有明确的空间 Extraction Area / Range。只有位于范围内、类型兼容的 Source Voxel 才能成为 Job Target。**

```text
Extraction Facility
├─ world position
├─ extraction range / area
└─ compatible source family
        ↓
Eligible Source Voxels
```

具体半径、形状暂不冻结，可以在实现和 Playtest 时调整。

---

❓ **Q407** - **“矿床 / 森林资源区”是否只是空间上的 Source Voxel Cluster，而不是新的 Runtime Entity**：

Q222 已经确定一个大型矿床由大量 Source Voxels 组成：

```text
■■■■■
■■■■■■
 ■■■■
```

现在 Resource Renewal Event 也会“生成一个新的资源区”。

我们可以因此引入：

```text
DepositInstance
├─ Reserve
├─ Type
└─ Voxels
```

但这会重新产生第二份资源事实。

➡️ **我推荐不要建立 `DepositInstance`。**

正式 Runtime Truth 仍然只是：

```text
World Source Voxels
```

“矿床”“森林区”“资源 Cluster”只是：

- World Generation 的生成概念；
- 地图/UI 的空间查询结果；
- AI 探勘时的聚类结果。

其总储量始终从实际 Source Voxel 的 `CurrentDurability + Yield` 派生。

这样不会把我们 Q196 刚刚删掉的 Reserve 又在更高层重新发明一次。

---

❓ **Q408** - **Facility Compatibility Tags 应该存在哪里**：

Q374 已确定使用：

```text
metallurgy
assembly
textile
food_processing
...
```

这类 Tag 匹配 Facility 与 Process。

但它们究竟属于：

**A. Building Composite Definition 的配置。**

```text
Smelter Blueprint
facility_tags = [metallurgy]
```

**B. 某个 Machinery Voxel。**

➡️ **我推荐 A。**

Facility Tag 回答的是：

> “这个建筑被设计成做什么？”

而内部实际设备回答：

> “它现在有多少能力？”

因此：

```text
Building Composite Definition
→ Facility Tags / Process Compatibility

Runtime Voxel Behaviors
→ Production / Extraction / Construction Capability
```

例如一栋 Smelter 即使所有 Industrial Machinery 都坏了，仍然是 `metallurgy` Facility，只是：

```text
ProductionCapacity = 0
```

这也避免让一个通用 `Industrial Machinery` 同时承担“机器是什么类型”和“机器提供多少产能”两个职责。

---

❓ **Q409** - **Facility Tag 是否只是被动兼容性元数据，而不是 Capability**：

当前架构已经明确：Capability 不能由 JSON 随意伪造，必须由实际 Behavior 推导。fileciteturn0file5L97-L108

所以我建议明确：

```text
facility_tag: metallurgy
```

只表示：

> 允许该 Building 运行要求 `metallurgy` 的 Process。

真正启动还必须同时存在：

```text
Industrial Machinery
→ ProductionCapability > 0
```

以及 Power、Labor 等条件。

➡️ **我推荐严格区分。**

这样 Mod 写：

```text
facility_tags = [metallurgy]
```

不会凭空让一座空房子开始炼钢。

---

❓ **Q410** - **Extraction 是否也使用 Facility Compatibility Tag，而不是给通用 Extraction Equipment 拆型号**：

Q368 选择了一个统一：

```text
Extraction Equipment
```

因此资源类型差异需要落在别处。

我建议：

```text
Mining Facility
facility_tags = [mining]

Forestry Facility
facility_tags = [forestry]

Oil Extraction Facility
facility_tags = [oil_extraction]
```

Source / Extraction 定义则要求相应 Tag：

```text
Ore Deposit
requires mining

Forest
requires forestry

Oil Deposit
requires oil_extraction
```

实际速度仍由同一种 `Extraction Equipment` 提供的 Extraction Capability 决定。

➡️ **我推荐采用这一结构。**

这样不需要立即制造：

```text
Mining Drill
Logging Machine
Oil Pump
```

三种商品，但空间产业仍然有明确区别。

---

❓ **Q411** - **Primary Production 的 Site Requirements 是否在运行期间持续有效，而不是只在建造时检查一次**：

例如一个农场建设时位于适合农业的 Site，后来地形或环境条件发生重大变化。

有两个方向：

**A. 建好以后永远能生产。**

**B. Process 每次运行都要求当前 Site 条件仍满足。**

➡️ **我推荐 B。**

即：

```text
ProductionProcess
required_environment = [...]

Facility site
        ↓ current evaluation
eligible / ineligible
```

但不要求每 Tick 昂贵地全地图扫描。实际实现可以在：

- Facility 建造；
- Site 状态变化；
- Process 启动；

等事件发生时重算并缓存结果。

语义上仍然是**当前环境决定当前是否能生产**。

---

❓ **Q412** - **Primary Production 的环境要求是否属于 Process，而不是硬编码在 Building 类型中**：

例如同一 Agricultural Facility 可以兼容：

```text
Grain Farming
Produce Farming
```

但二者未来可能有不同环境条件。

➡️ **我推荐 Site / Environmental Requirements 属于 `ProductionProcessDefinition`。**

于是：

```text
Facility tags
→ 这个建筑类别原则上允许什么 Process

Process environment requirements
→ 这个地点实际上能不能运行它

Industrial Machinery
→ 能运行多快
```

三者职责非常清楚。

---

❓ **Q413** - **有 Active Job / Output Buffer 的 Facility 是否禁止直接开始 Refit 或 Deconstruction**：

这是当前生命周期里最后一个明显冲突。

例如：

```text
Factory
├─ ActiveBatch = 71%
└─ OutputBuffer = empty
```

玩家此时要求 Refit。

或者：

```text
Factory
└─ OutputBuffer = Electronic Components ×4
```

玩家直接开始 Deconstruction。

我们虽然已经定义这些 Job 在施工期间会锁定目标，但还没定义**能否带着未完成的生产状态进入施工**。

➡️ **我推荐必须先达到生产静止状态：**

```text
ActiveBatch = none
OutputBuffer = empty
ExtractionJob = none
RecoveryJob = none
```

才能开始该 Facility 的 Refit / Deconstruction。

对于持续 Production Plan，可以先：

```text
Enabled = false
→ 当前 Batch 自然完成
→ Buffer 清空
→ Facility Idle
→ 开始 Refit / Deconstruction
```

不允许施工系统隐式取消生产 Batch，也不允许把 Facility-level ActiveBatch 塞进拆下来的 Voxel ItemInstance。

---

本轮 Frontier 是 **Q406–Q413**。

如果这些落定，我预计资源/生产系统剩下的就主要是**审计型问题**而不是新系统：例如库存中 Painted/受损 Voxel 的堆叠边界、Job/Process 的 Definition 身份与内容更新规则，以及最终总图是否还有静默假设。

---

## 55. User  (2026-09-14 08:50:03 UTC)

Q406 同意
Q407 同意
Q408 A
Q409 同意
Q410 同意
Q411 B
Q412 同意
Q413 同意

---

## 56. Assistant  (2026-09-14 08:50:21 UTC)

> [思考摘要] 厘清涂装库存：我在继续检查库存与物品身份：如果涂装后的建筑块被拆除、储存或重新加工，颜色信息是否必须保留？这会影响堆叠规则、实例化、回收和重建成本。 / 厘清涂装库存

Q406–Q413 之后，资源与生产的主机制已经闭合。我现在进入最后一轮 **audit frontier**：不再新增系统，只检查几个会导致实现歧义的边界——主要是 **Stack / ItemInstance 的判定、涂装状态如何存储、Process/Job 的身份与热重载、以及内容更新时正在运行的 Job 如何解释**。

> [思考摘要] 冻结执行契约：我在检查内容更新与运行中任务的边界：生产或回收批次开始后，是否冻结当时的输入、输出、工时和消耗规则？否则配方更新可能让已消耗资源被重新解释，造成不一致或漏洞。 / 冻结了执行契约

审计里发现一个需要优先修正的真实冲突：我们在 Q206 用 `MaxDurability - CurrentDurability` 推导累计开采量，但现有内容更新规则允许 `MaxDurability` 改变，同时保留已有实例的 `CurrentDurability` 绝对值。这样热重载后会凭空改变“已开采量”。这必须在最终定稿前修掉。

❓ **Q414** - **Extraction Yield 公式是否改为只依赖一次开采前后的 CurrentDurability**：

Q206 原方案是：

```text
TotalExtractedDamage
= MaxDurability - CurrentDurability
```

但现有内容更新规则明确规定：MaxDurability 提高时 CurrentDurability 保持原绝对值，提高上限不会自动修复。fileciteturn0file0L105-L109

例如：

```text
旧：
Max = 500
Current = 300
→ 已开采 200

内容更新：
Max = 1000
Current 仍 = 300

按旧公式：
→ 突然变成“已开采 700”
```

这显然错误。

➡️ **我推荐把 Q206 的实现公式修正为基于 Damage 区间：**

对于：

```text
N Damage → M Goods
```

一次 Extraction 从：

```text
oldDurability → newDurability
```

产生：

```text
Yield =
floor(oldDurability × M / N)
-
floor(newDurability × M / N)
```

例如 `5 Damage → 1 Good`：

```text
300 → 297
floor(300/5) - floor(297/5)
= 60 - 59
= 1
```

这个方案有几个优点：

- 不需要 Reserve；
- 不需要 Yield remainder 状态；
- 仍然严格按 Effective Damage；
- 分多次还是一次造成同样总 Damage，结果一致；
- 修改 MaxDurability 不会篡改过去已经发生的 Yield；
- 完全符合你希望复用 Durability 的方向。

也就是说，**保留 Q206 的设计意图，但替换具体计算方式。**

---

❓ **Q415** - **涂装颜色是否属于 Composite / Placement 的表现覆盖，而不是库存 ItemInstance 状态**：

Q276 已确定颜色不改变商品身份，但如果：

```text
Structural Block
Paint = Black
```

然后拆下来进入库存，我们会遇到一个问题：

普通 Stack 只有：

```text
VoxelDefinitionId + Quantity
```

没有颜色。

如果为了保留颜色，把每一个涂过色的 Structural Block 都变成 ItemInstance，会让大量纯装饰状态破坏 Stack 的意义。

➡️ **我推荐把涂装定义成空间实例上的 Presentation Override，而不是可携带的商品状态。**

例如车辆保存：

```text
Voxel Runtime / Composite Presentation
LocalCoord → PaintColor
```

拆下该 Voxel 后，颜色覆盖随安装关系消失；重新库存化后仍然是普通：

```text
Structural Block ×1
```

玩家以后安装后可以重新涂装。

这样颜色真正做到：

> 有视觉意义，但没有资源、库存和经济语义。

---

❓ **Q416** - **是否正式建立统一 `Stackability` 判定规则**：

目前已经零散确定：

- 普通新品可以 Stack；
- 受损 Engine 要 ItemInstance；
- 受损 Structural Block 不能因为收纳就自动修复；
- 有 Behavior Runtime State 的设备必须保存状态。

我建议正式统一成：

> 一个 Voxel 只有在所有需要保存的个体状态都处于 canonical/default 状态，并且不需要保留长期身份时，才允许折叠进 Stack。

典型：

```text
Full durability
No non-default Behavior Runtime State
No identity that must survive
No gameplay-significant instance state
→ Stack
```

否则：

```text
→ ItemInstance
```

例如：

```text
全新 Engine
→ Stack

使用过、但仍满耐久且有部分燃料的 Power Equipment
→ ItemInstance

70% Durability Structural Block
→ ItemInstance
```

➡️ **我推荐采用这个统一 predicate，而不是为每一种 Good 写特殊规则。**

所有 Stack unit 和 ItemInstance 在 Storage Capacity 上仍都占 `1`。

---

❓ **Q417** - **Construction / Refit 自动满足 BOM 时是否优先只消费 Stack，ItemInstance 必须显式选择**：

假设需要：

```text
Engine ×2
```

库存有：

```text
Engine Stack ×1

ItemInstance:
Engine #51 100%
Engine #82 40%
Engine #97 15%
```

如果系统自己随便挑一个二手 Engine，玩家可能意外把 15% 耐久的设备装进新车。

➡️ **我推荐：**

```text
Stack Goods
→ 可自动用于 BOM

ItemInstance
→ 必须由玩家 / 上层计划显式指定
```

如果 Stack 不足而又没有明确选定具体 ItemInstance：

```text
Construction validation fails
```

而不是自动从二手库存里挑。

这既避免惊喜，也避免为了自动挑选二手物品再发明“最好耐久优先”“最差耐久优先”等隐含策略。

---

❓ **Q418** - **Production / Recovery Process 是否成为正式的、稳定身份的内容 Definition**：

现在 `ProductionProcessDefinition` 已经承担：

```text
Inputs
Outputs
WorkRequired
Requirements
Facility tags
Power/Labor intensity
```

并且 ActiveBatch、Production Plan、Save 都需要引用它。

➡️ **我推荐它正式进入现有 Definition 内容体系：**

```text
ProductionProcessDefinitionId
RecoveryProcessDefinitionId
```

均采用稳定 namespaced ID，例如：

```text
core:ferrous-refining
core:mechanical-components
core:recover-power-equipment
```

Definition 不保存 Runtime State，并拥有与其他内容相同的 Semantic Revision 思路。

不把 Process 嵌死在某个 Building Definition 里，也不只用显示名称作为身份。

这延续现有项目不同领域使用强类型、namespaced ID 的规则。fileciteturn0file0L93-L97

---

❓ **Q419** - **Active Batch 是否在启动时冻结其完整执行契约**：

这是另一个热重载问题。

假设一个 Batch 已经启动并消耗：

```text
Ore ×4
Coal ×1
```

进度：

```text
60 / 100
```

此时 Mod 热重载把 Process 改成：

```text
Ore ×8
→ Structural Metal ×7
WorkRequired = 300
```

如果 ActiveBatch 立即采用新定义，就会出现“按旧输入启动，却按新输出完成”的不一致。

➡️ **我推荐 Batch 启动时冻结 resolved execution snapshot：**

```text
ActiveBatch
├─ ProcessDefinitionId
├─ StartedFromRevision
├─ frozen Outputs
├─ frozen WorkRequired
├─ frozen factor intensities
└─ ProgressWork
```

Inputs 已经在启动时消费，因此不必再保存为待扣库存，但可以保存用于日志/诊断。

**当前 Batch 完成时严格按启动时契约完成；下一 Batch 才使用当前最新 ProcessDefinition。**

Recovery Batch 同理。

这不是保存一份旧外部 Definition 供世界长期使用，而只是保存**已经发生的事务承诺**。

---

❓ **Q420** - **Refit Job 是否也在启动时冻结完整 Refit Plan**：

Construction Job 已在 Q352 确定冻结启动时的设计快照。

Refit 也应该同样处理：

```text
RefitJob
├─ target Composite identity
├─ expected source layout / identities
├─ final validated layout
├─ move/rotate/add/remove/replace mapping
├─ selected ItemInstances
└─ ProgressWork
```

之后玩家修改 Blueprint，不改变正在进行的 Refit。

➡️ **我推荐采用。**

否则一个耗时 Refit 运行到一半时编辑设计，会重新产生“消耗旧 BOM、提交新 Layout”的同类问题。

---

本轮 Frontier 是 **Q414–Q420**。

如果这些也确认，下一轮我预计只剩最后几个生命周期审计点，例如 **Refit 过程中目标遭到结构破坏时如何失败、内容更新如何作用于 Extraction Job、以及最终术语/分类是否还有重复事实**。再之后 frontier 应该就会接近空，可以进入“确认理解一致”的阶段。

---

## 57. User  (2026-09-14 08:53:46 UTC)

Q414 同意
Q415 同意
Q416 同意
Q417 同意
Q418 同意
Q419 同意
Q420 采用

---

## 58. Assistant  (2026-09-14 08:54:01 UTC)

> [思考摘要] 继续收敛规则：我正在检查剩余边界：目标结构变化、施工地点失效，以及资源行为热更新时，活动作业应继续、失败还是重新验证。接下来我会把这些情形逐一固定，避免隐含规则。 / 继续收敛规则

Q414–Q420 已把最后几个“状态是否会因为库存化、涂装或热重载而失真”的风险堵住了。当前 audit 剩下的不是新功能，而是**正在运行的 Job 遇到外部结构变化或内容更新时怎样保持契约一致**。这一轮应该是最后一批生命周期问题。

> [思考摘要] 明确作业失效：我正在继续固定边界：若目标结构、施工地点或资源行为在作业期间发生变化，我会区分冻结数据与运行时数据，并明确失败、材料损失、保留状态和预留释放规则。 / 明确作业失效

❓ **Q421** - **Refit Job 期间目标 Composite 发生结构变化时，是否与 Deconstruction 一样立即失败**：

例如：

```text
Refit Progress = 60%

计划：
remove A
move B
add C

期间外部 Damage 摧毁了 B
```

冻结的 Refit Plan 已经无法成立。

➡️ **我推荐与 Q405 对称处理：**

```text
target voxel layout / identity set changed
→ Refit Job fails
→ Progress discarded
→ consumed added/replacement Goods lost
→ target Composite 保持事件后的真实状态
```

原本计划移除但实际上仍存在的旧部件继续留在 Composite；不会按照旧 Refit Plan 强制提交。

如果只是 Durability 改变、没有 Voxel 被删除或身份集合改变，则 Job 可以继续，完成时保留该 Voxel 最新的 Runtime State。

---

❓ **Q422** - **Construction Job 期间目标 Site 失效时如何处理**：

Spatial Reservation 可以阻止别人往目标 Cell 建东西，但它不一定阻止外围地形、支撑条件或环境发生变化。

例如：

```text
Construction Progress = 70%
↓
地形变化
↓
最终 Blueprint 已不再满足 Site / placement constraints
```

➡️ **我推荐 Construction Job 失败：**

```text
placement/site validity becomes false
→ Job fails
→ consumed BOM lost
→ Progress discarded
→ Spatial Reservation released
```

不等待玩家无限期修复，也不在完成时强行生成非法 Composite。

Reservation 负责防止**直接占格冲突**，但不是冻结整个附近世界。

---

❓ **Q423** - **Process 内容热更新时，ActiveBatch 与 Production Plan 是否采用不同语义**：

Q419 已经冻结 ActiveBatch 的执行契约，因此我建议正式规定：

### 已经启动的 Batch

```text
Process r1
→ Batch started
→ frozen execution snapshot
```

即使随后发布 Process r2：

```text
current Batch → 按 r1 snapshot 完成
next Batch    → 使用 r2
```

### 持续 Production Plan

Plan 只持有稳定：

```text
ProductionProcessDefinitionId
```

下一次启动 Batch 时解析当前 Definition。

如果热更新**删除了仍被 Production Plan / ActiveBatch 引用的 ProcessDefinitionId**，则整次内容发布拒绝，而不是静默关掉工厂。

➡️ **我推荐采用。**

这与现有“运行中必需引用不能因内容更新而突然消失；无法适配则拒绝整个热重载”的契约一致。fileciteturn0file0L111-L115

---

❓ **Q424** - **Extraction Job 遇到 ResourceSourceBehavior 热更新时是否采用“下一次 Extraction Damage 使用当前规格”**：

Extraction 和 Batch 不同：它没有启动时一次性消费 Input，也没有一个需要兑现的固定 Batch Output Contract。

因此我建议：

```text
ExtractionJob
Target = Source #X
ProgressWork = ...
```

Behavior Yield 从：

```text
1 damage → 2 goods
```

更新成：

```text
1 damage → 3 goods
```

那么：

> 已经发生的 Damage 不重算；下一次 Extraction Damage 开始使用当前 Behavior。

这与 Q414 的区间 Yield 算法完全兼容。

但是如果内容更新让目标 Voxel：

- 不再具有 `resource_source` / ResourceSourceBehavior；
- 或当前 Facility 与该 Source 不再兼容；

➡️ **我推荐拒绝这次热更新，而不是静默取消正在执行的 ExtractionJob。**

这样不会因为编辑内容文件导致已有 Runtime Job 悄悄消失。

---

❓ **Q425** - **Construction / Refit 冻结的是 Layout 与身份映射，而不是所有外部 VoxelDefinition 的旧 Revision**：

例如 ConstructionJob 启动时设计中包含：

```text
core:structural-block
core:power-equipment
```

随后 `core:power-equipment` 的：

```text
Mass
MaxDurability
Behavior parameters
```

发生内容更新。

现有 Definition 规则本来就规定运行时外部内容使用当前安装规格，而不是长期锁死旧 Revision。fileciteturn0file0L42-L45

➡️ **我推荐：**

Construction/Refit Snapshot 冻结：

```text
VoxelDefinitionId
layout
orientation
identity mapping
selected ItemInstances
```

但不嵌入外部 VoxelDefinition 的旧规格。

Job 完成时：

```text
DefinitionId → resolve current Definition
```

因此新生成/安装的设备采用当前规格。

如果内容更新删除了 Job Snapshot 必需的 DefinitionId，则热更新拒绝。

这样不会重新引入“为了施工 Job 保存整套旧外部 Definition”的第二套版本体系。

---

❓ **Q426** - **Job 的执行能力是否永远读取当前 Runtime Capability，而不随 Job 启动被冻结**：

例如 ConstructionJob 已运行一半：

```text
Construction Equipment ×10
→ Capacity = 100
```

随后其中 5 个设备被破坏：

```text
Capacity → 50
```

或者内容更新改变了设备规格。

➡️ **我推荐 Job 只冻结“任务契约”，不冻结执行资源。**

因此：

```text
Production Batch
Construction Job
Refit Job
Deconstruction Job
Extraction Job
```

每个 Tick 都根据**当前**：

```text
Capability
Power
Labor
Facility operational state
```

计算可执行 Work。

于是设备受损会自然减速或暂停，修复后继续。

这符合当前 Capability 必须从实际 Runtime Voxel/Behavior 推导的原则。fileciteturn0file5L97-L108

---

❓ **Q427** - **同一个 Facility 的并行 Job 数是否首版严格限制**：

我们已经确定 Production Facility 同时只运行一个 Production Job。现在需要把另外几类明确下来。

➡️ **我推荐首版采用：每种实际执行 Capability 同时最多一个 Active Job。**

```text
Production Capability
→ 1 Production/Recovery Batch

Extraction Capability
→ 1 Extraction Job

Construction Capability
→ 1 Construction / Refit / Deconstruction Job
```

其中：

```text
Production 与 Recovery
```

共享同一个生产执行槽，因为二者都使用 Industrial Machinery。

```text
Construction / Refit / Deconstruction
```

共享同一个施工执行槽。

如果一栋特殊 Building 同时拥有 Industrial Machinery 和 Construction Equipment，则理论上可以：

```text
1 Production Job
+
1 Construction Job
```

并行，因为它们使用不同 Capability，只需要共同竞争 Power 与 Labor。

首版不做：

```text
同一 Construction Capability
→ 同时建三辆车并手工分配 40% / 30% / 30%
```

这种内部产能分割。

---

❓ **Q428** - **所有长生命周期 Job 是否统一属于权威 Save State**：

目前 Production 和 Construction 已经分别确认保存，但同一原则应覆盖完整集合：

```text
ActiveBatch
ExtractionJob
ConstructionJob
RefitJob
DeconstructionJob
OutputBuffer
Spatial Reservation
Production Plan
FuelEnergyRemaining
```

➡️ **我推荐全部保存，并要求 Save → Load → Continue 不改变结果。**

例如读档不能：

```text
Extraction 83% → 0%
Refit 61% → 消失
Deconstruction reservation → 丢失
```

这些都不是缓存，而是会决定未来世界状态的权威 Runtime State。

这与现有存档原则中“Runtime Delta / Runtime State 必须恢复，并与不中断运行保持一致”的方向一致。fileciteturn0file1L18-L34

---

本轮 Frontier 是 **Q421–Q428**。

如果这些全部确定，我目前的审计结果是：**资源与生产 design tree 将只剩术语和总图层面的核对，不再存在需要玩家决定的结构性分支。** 下一轮我会先把整棵树压缩成最终模型检查一次；如果没有新的矛盾，我会明确告诉你 frontier 已空，然后等你确认“达到理解一致”，再进入正式文档编写阶段。

---

## 59. User  (2026-09-14 08:56:18 UTC)

Q421 同意
Q422 同意
Q423 同意
Q424 同意
Q425 同意
Q426 同意
Q427 同意
Q428 同意

---

## 60. Assistant  (2026-09-14 08:56:23 UTC)

审计结束后，我没有发现新的结构性矛盾。**资源与生产系统的 design tree 现在已经闭合，frontier 为空。** 当前结果也保持了既有项目最重要的架构原则：统一 `VoxelDefinition`、Inventory 与空间 Instance 分离、Composite 由真实 Voxel 构成、能力由 Behavior/Capability 推导、长生命周期状态进入确定性 Simulation 与 Save。fileciteturn0file0L19-L27 fileciteturn0file0L35-L45 fileciteturn0file5L21-L37 fileciteturn0file11L44-L64

### 最终设计树压缩

```text
Resource & Production System
│
├─ 1. Goods boundary
│  ├─ 标准化、物质性、可库存、可交易
│  ├─ 普通 Goods 全部是 VoxelDefinition
│  ├─ Composite 不属于普通 Goods
│  ├─ Crew 等非商品对象排除
│  └─ Power / Labor / Time 等是 Production Factors，不是 Goods
│
├─ 2. Resource acquisition
│  ├─ Resource Source = 普通 VoxelDefinition + resource_source + Behavior
│  ├─ CurrentDurability 同时表示剩余可采量
│  ├─ Extraction 是显式行为，本质上通过正式 Damage Transaction
│  ├─ 普通 Damage 不产生资源
│  ├─ Extraction 只产生 Raw Goods
│  ├─ 单 Source 可产生多种 Raw Goods
│  ├─ Yield 完全确定
│  ├─ Source 耗尽 → Voxel destroyed
│  ├─ Source cluster 只是世界中多个 Voxel，不建立 DepositInstance
│  └─ 长期补充 = 未开发区域生成新 cluster，不原地刷新
│
├─ 3. Raw Goods
│  ├─ Ferrous Ore
│  ├─ Conductive Ore
│  ├─ Rare Mineral
│  ├─ Stone
│  ├─ Silica
│  ├─ Coal
│  ├─ Crude Oil
│  ├─ Grain
│  ├─ Produce
│  ├─ Animal Products
│  ├─ Timber
│  └─ Natural Fiber
│
├─ 4. Resource Sources
│  ├─ Ferrous Ore Deposit
│  ├─ Conductive Ore Deposit
│  ├─ Rare Mineral Deposit
│  ├─ Stone Deposit
│  ├─ Silica Deposit
│  ├─ Coal Deposit
│  ├─ Oil Deposit
│  └─ Natural Forest
│
├─ 5. Primary production
│  ├─ Grain Farming
│  ├─ Produce Farming
│  ├─ Livestock Production（消耗 Grain）
│  ├─ Fiber Farming
│  └─ Forestry
│
├─ 6. Forward Production DAG
│  │
│  ├─ Basic materials
│  │  ├─ Ferrous Ore + Coal → Structural Metal
│  │  ├─ Conductive Ore → Conductive Metal
│  │  ├─ Rare Mineral → Specialty Material
│  │  ├─ Stone → Construction Material
│  │  ├─ Silica → Glass
│  │  ├─ Timber → Lumber
│  │  ├─ Natural Fiber → Textile
│  │  ├─ Crude Oil → Fuel + Chemical Feedstock
│  │  ├─ Coal → Fuel
│  │  └─ Chemical Feedstock → Polymer
│  │
│  ├─ Components
│  │  ├─ Structural Metal + Polymer
│  │  │  → Mechanical Components
│  │  ├─ Conductive Metal + Structural Metal
│  │  │  → Electrical Components
│  │  └─ Electrical Components + Specialty Material
│  │     + Glass + Polymer
│  │     → Electronic Components
│  │
│  ├─ Civilian goods
│  │  ├─ Grain → Staple Food
│  │  ├─ Grain + Produce + Animal Products → Prepared Food
│  │  ├─ Textile → Clothing
│  │  ├─ Lumber + Polymer + Structural Metal + Glass
│  │  │  → Consumer Goods
│  │  ├─ Electronic Components + Polymer + Glass
│  │  │  → Consumer Electronics
│  │  └─ Chemical Feedstock + Textile + Polymer
│  │     → Medical Supplies
│  │
│  ├─ Construction goods
│  │  ├─ Structural Metal → Structural Block
│  │  ├─ Construction Material → Construction Block
│  │  └─ Structural Metal → Rail
│  │
│  └─ Functional equipment
│     ├─ Industrial Machinery
│     ├─ Storage Equipment
│     ├─ Power Equipment
│     ├─ Control Equipment
│     ├─ Extraction Equipment
│     └─ Construction Equipment
│
├─ 7. Future equipment families
│  ├─ Propulsion Equipment
│  ├─ Train Weapons
│  ├─ Crew Weapons
│  ├─ Train Equipment
│  └─ Crew Equipment
│
│  只作为设计占位，不创建万能 VoxelDefinition；
│  对应 Gameplay 系统设计时再拆成真实产品。
│
├─ 8. Production Process model
│  ├─ 一等 ProductionProcessDefinition
│  ├─ 稳定 namespaced strong ID
│  ├─ integer Inputs / Outputs
│  ├─ WorkRequired
│  ├─ PowerPerWork
│  ├─ LaborPerWork
│  ├─ Facility compatibility tags
│  ├─ Environmental requirements
│  ├─ 一个 Facility 同时一个 Production/Recovery job
│  └─ 默认一条 canonical route；
│     替代路线只有确实产生 Gameplay 差异时才增加
│
├─ 9. Production runtime
│  ├─ Production Plan = ProcessId + Enabled + Priority
│  ├─ ActiveBatch 是权威 Runtime State
│  ├─ Batch start 时完整 Inputs 原子扣除
│  ├─ 缺料时等待，不关闭 Plan
│  ├─ Work 连续积累
│  ├─ Power / Labor 不足只减速或暂停
│  ├─ 完成后尽可能写入 Settlement Inventory
│  ├─ 剩余进入 Facility-local OutputBuffer
│  ├─ Buffer 非空只阻塞本 Facility
│  ├─ Buffer 最多容纳当前一个完成 Batch 的剩余输出
│  ├─ OutputBuffer Goods 已真实存在
│  ├─ 已完成 Buffer 优先获得新释放的 Storage Capacity
│  ├─ Tick Barrier 禁止同 Tick 连锁启动下一 Batch
│  └─ ActiveBatch 热重载时冻结本批执行契约
│
├─ 10. Power / Labor / Capacity
│  ├─ Industrial Machinery → Production Capability
│  ├─ Extraction Equipment → Extraction Capability
│  ├─ Construction Equipment → Construction Capability
│  ├─ Storage Equipment → Storage Capability
│  ├─ Power Equipment + Fuel → Power Pool
│  ├─ Population → Labor Pool
│  ├─ Settlement 内统一 Power Pool
│  ├─ Settlement 内统一 Labor Pool
│  ├─ 不模拟内部电网
│  └─ 当前 Capability 每 Tick 重新决定实际 Work
│
├─ 11. Inventory
│  ├─ Goods 不因库存而实体化
│  ├─ 每个 Goods unit 占 1 Storage Capacity
│  ├─ Mass 独立影响质量
│  ├─ Settlement = 统一地点库存
│  ├─ 内部 Facility 直接读写该库存
│  ├─ Train 与 Settlement 必须显式 Transfer
│  ├─ 无全局玩家魔法库存
│  ├─ Capacity 降低允许临时 Overcapacity
│  ├─ canonical/default 个体状态 → Stack
│  └─ 非默认状态 / Identity → ItemInstance
│
├─ 12. Composite Construction
│  ├─ Blueprint 实际 Voxel Layout → 精确 BOM
│  ├─ BOM 指定确切 VoxelDefinitionId
│  ├─ 不允许 Tag 自动替代
│  ├─ 不递归展开 Voxel 上游 Recipe
│  ├─ 消费完成的 Voxel Goods
│  ├─ Construction Job 耗时
│  ├─ 无半成品 Composite
│  ├─ 启动时完整 BOM 原子消费
│  ├─ 可同时取当前 Settlement + Train 可访问库存
│  ├─ 预留最终空间
│  ├─ Work + Power + Labor
│  ├─ Job 冻结 Layout / placement snapshot
│  └─ 完成时原子 Instantiate Composite
│
├─ 13. Refit
│  ├─ 正式 Gameplay 采用耗时 Refit Job
│  ├─ 目标 UnderRefit，不可正常运营
│  ├─ 只消费新增/替换 Goods
│  ├─ Move / Rotate 保留原 Voxel Identity
│  ├─ Work ∝ changed voxel count
│  ├─ 完成时原子提交最终 Layout
│  ├─ 启动时冻结完整 Refit Plan
│  └─ 目标结构集合发生变化 → Job fail
│
├─ 14. Deconstruction
│  ├─ Composite 不能直接 Recovery
│  ├─ 采用耗时 Deconstruction Job
│  ├─ Composite 在完成前保持完整存在
│  ├─ UnderDeconstruction，不可运营
│  ├─ 启动前预留返回部件所需 Storage
│  ├─ Work ∝ total voxel count
│  ├─ 完成时原子移除 Composite
│  ├─ 原有 Voxel / Identity / Runtime State 返回库存
│  └─ 结构变化导致 Job fail
│
├─ 15. Recovery DAG
│  ├─ 与 Production DAG 独立，二者都无环
│  ├─ 显式 opt-in
│  ├─ 不自动反转 Production Recipe
│  ├─ 复用 Production Batch Runtime
│  ├─ 主要覆盖耐用工业品与设备
│  ├─ 不加入万能 Scrap / Waste
│  ├─ 可以多阶段回收
│  ├─ Output 只使用已有 Goods
│  ├─ ItemInstance Durability 影响 Yield
│  └─ 其他 Runtime State 默认销毁
│
├─ 16. Recovery skeleton
│  ├─ Structural Block → Structural Metal
│  ├─ Construction Block → Construction Material
│  ├─ Rail → Structural Metal
│  ├─ Mechanical Components → Structural Metal + Polymer
│  ├─ Electrical Components → Conductive Metal + Structural Metal
│  ├─ Electronic Components
│  │  → Electrical Components + Specialty Material + Polymer
│  └─ Functional Equipment
│     → 对应 Components / Structural Metal / Polymer
│
├─ 17. Extraction runtime
│  ├─ 真实 Extraction Facility
│  ├─ 明确 Extraction range / area
│  ├─ Facility tags 决定可采资源类别
│  ├─ Extraction Job 指向确切 Source Voxel
│  ├─ Work → 整数 Extraction Damage
│  ├─ Yield 基于 old/new CurrentDurability 区间
│  ├─ Output 复用 Facility OutputBuffer
│  └─ 当前 Source Behavior 在后续 Damage 上生效
│
├─ 18. Facility model
│  ├─ Facility = Building Composite
│  ├─ Facility compatibility tags 属于 Composite Definition
│  ├─ Tag 只决定兼容性，不提供 Capability
│  ├─ Capability 来自实际内部 Voxel Behavior
│  ├─ Primary Production 的环境条件属于 Process
│  └─ 有 Active Job / Buffer 时不能进入 Refit/Deconstruction
│
├─ 19. Determinism / persistence / hot reload
│  ├─ 所有共享资源竞争经过 Resolver
│  ├─ 显式 Priority → stable deterministic fallback
│  ├─ 全部长生命周期 Job 进入 Save
│  ├─ OutputBuffer / Reservation / Fuel remainder 进入 Save
│  ├─ Process 使用稳定 DefinitionId
│  ├─ 已启动 Batch 冻结执行 snapshot
│  ├─ Plan 下一 Batch 解析当前 Definition
│  ├─ Job Layout 冻结，但外部 VoxelDefinition 使用当前规格
│  ├─ 缺失仍被 Runtime State 引用的 Definition → 拒绝内容发布
│  └─ Save → Load → Continue 必须保持模拟一致
│
└─ 20. Explicitly outside this design
   ├─ Market
   ├─ Supply / Demand
   ├─ Price formation
   ├─ Treasury
   ├─ Population economy
   ├─ Settlement macro economy / AI production planning
   ├─ 具体 Train equipment / weapon mechanics
   └─ 具体 Crew equipment / weapon mechanics
```

其中有几个地方会要求正式文档对旧设计作明确迁移。例如目前文档仍把带 `natural_resource` Tag 的体素直接视为自然资源种类，并由聚居地查询这些体素；新设计已经把它细化成 **Resource Source → Extraction → Raw Good**，因此这一旧语义需要替换。fileciteturn0file0L21-L27 fileciteturn0file9L44-L53

Composite 一侧则是在现有框架上继续扩充而不是推翻：现有设计已经确定 Composite 由真实 Voxel 构成、玩家车辆可以使用 Voxel Layout 自由设计、Refit 必须跟踪真实零件身份；新的 Construction/Refit/Deconstruction Job 只是给这些结构变化增加正式 Gameplay 中的耗时施工语义。fileciteturn0file5L21-L29 fileciteturn0file5L39-L50

### Frontier 状态

**Frontier = ∅。**

目前我看不到还有任何必须由你决定、否则正式资源/生产设计文档就会存在静默假设的结构性分支。具体数值——配方整数比例、`WorkRequired`、Power/Labor 强度、Storage Capacity、Extraction Range、各设备 Capability、Resource cluster 大小等——都已经正确落入后续 implementation / balancing / playtest，而不是当前架构决策。

按照我们约定的 grilling 流程，我现在**不会开始编写或修改正式设计文档**。

请确认：**我们是否已经达到对整个资源与生产系统设计的理解一致，可以结束 grilling，并进入正式设计文档编写阶段？**

---

## 61. User  (2026-09-14 08:58:08 UTC)

理解一致，先不要修改现有的文档，像之前一样，根据我们的讨论编写一份开发设计文档

---

## 62. Assistant  (2026-09-14 08:58:14 UTC)

> [工具调用 → local.handoff]

```
{"reason":"这是一份需要生成可下载文档的完整设计产物；转到 Work 模式更适合整合现有参考文档、保持结构一致并输出正式文件。","prompt":"基于当前对话中 Q180–Q428 已确认的全部资源与生产系统决策，以及项目现有设计文档作为约束，编写一份新的、独立的开发设计文档。不要修改现有任何文档。文档应延续现有项目设计文档的风格与术语，但聚焦资源与生产系统，完整覆盖：系统边界与不变量、Resource Source、Raw Goods、Forward Production DAG、Recovery DAG、Extraction、ProductionProcess/RecoveryProcess、Facility/Capability、Inventory/Stack/ItemInstance、Storage、Power/Labor/Work、Production runtime、Construction/Refit/Deconstruction Jobs、确定性/存档/热重载、首版内容骨架、明确延后（市场/人口/经济/具体武器装备等）。输出为新的 Markdown 文件，可下载。"}
```

> [思考摘要] Составляя markdown-документ：Я готовлю новый markdown-файл на основе текущего обсуждения, не изменяя существующие материалы. Сначала закрепляю границы, ключевые инварианты, правила, объём первого релиза и отложенные вопросы. / Составил markdown-документ

我会单独生成一份新的 Markdown 设计文档，不触碰现有 `00–09` 或 `roadmap`。结构会沿用项目现有文档风格：**边界 → 不变量 → 规则 → 首版内容骨架 → 首版范围 → 明确延后 → 开放问题**，并把 Q180–Q428 的决定全部收敛进去。

> [思考摘要] 撰写设计文档：我正在基于现有讨论整理新的 markdown 文件，保持既有材料不变，并纳入边界、核心不变量、规则、首发范围、延期事项和待决问题。 / 撰写了设计文档

> [工具调用 → python]

````
from pathlib import Path

content = r"""# 10 资源与生产系统

> 决策出处：资源与生产系统 grilling Q180–Q428。本文在既有体素、Definition、Composite、Behavior、Capability、确定性 Simulation 与存档契约之上，定义自然资源、标准货物、生产、回收、库存、设施能力，以及 Composite 的 Construction / Refit / Deconstruction 资源语义。
>
> 本文为新增设计文档，不修改现有 `00–09` 与 `roadmap.md`。其中与旧文档存在的语义迁移点见“与现有文档的衔接”；在后续统一回写前，现有文件保持原样。

## 边界

本文回答：

- 世界中的自然资源如何表示、开采和耗尽。
- 哪些东西进入资源与货物体系。
- Raw Good、工业材料、Component、民用商品、Construction Good、功能设备之间如何构成无环生产网络。
- Production / Recovery Process 如何定义和执行。
- 生产设施的兼容性、产能、Power、Labor、Storage 如何来自真实世界状态。
- 聚居地与列车库存如何保存 Goods，以及 Stack 与 ItemInstance 如何区分。
- Composite 如何使用完成的 Voxel Goods 进行耗时 Construction、Refit 与 Deconstruction。
- Production、Extraction、Recovery、Construction 等长生命周期 Job 如何满足确定性、存档和热重载要求。

本文**不设计**：

- 市场 Supply / Demand。
- Price 形成机制。
- Treasury、税收和货币循环。
- 人口消费、人口增长、职业、工资等人口经济。
- 聚居地宏观经济 AI 与生产规划策略。
- 具体 Propulsion、Train Weapon、Crew Weapon、Train Equipment、Crew Equipment 的玩法规则。
- 具体战斗、乘员、医疗、列车动力等系统。

未来经济系统以本文产生和消耗的真实 Goods、Inventory、Production Capability 为事实基础，但其规则另行设计。

---

## 不变量

1. **所有标准货物都是 VoxelDefinition。** 不建立独立于 VoxelDefinition 的 GoodDefinition、MaterialDefinition 或 ComponentDefinition 体系。
2. **资源网络中的对象必须是物质性、可库存、标准化且原则上可交易的实物。** Crew、Power、Labor、Time 等不属于 Goods。
3. **Composite 不是普通 Goods。** Composite 可以未来参与资产所有权交易，但不进入普通 Cargo Stack 或 Production DAG。
4. **Production DAG 与 Recovery DAG 分离，且各自都是有向无环图。**
5. **不存在生产 Tier。** Raw、Material、Component、Finished 等仅是内容语义或 Tag，不构成合法性层级。
6. **Gameplay Difference Test 决定是否拆分商品。** 现实中存在差异，不等于游戏中需要独立 Definition。
7. **Resource Source 与 Raw Good 严格分离。** 世界 Source Voxel 是可开采资源源；Raw Good 才是进入标准库存与贸易体系的货物。
8. **Resource Source 不拥有独立 Reserve 字段。** `CurrentDurability` 同时表达该 Source Voxel 的剩余可开采量。
9. **Extraction 是显式 Gameplay 行为。** 它通过正式 Damage Transaction 造成 Effective Damage 并产生 Raw Good；普通 Damage 不产生资源。
10. **Extraction 只产生 Raw Good。**
11. **Goods Quantity 永远是整数。** Production / Recovery 的 Goods Input 和 Output 也必须是整数。
12. **Inventory Quantity 不等于空间 Voxel Instance。** 库存中的 1000 个 Goods 不意味着世界中存在 1000 个实体体素。
13. **普通 Goods 的 Storage Capacity 按 Quantity 计算。** 每 1 个 Goods unit 占 1 点容量；Mass 独立用于质量相关 Gameplay。
14. **没有全局玩家物质库存。** Goods 必须属于明确地点的 Inventory。
15. **聚居地内部物流宏观化。** 同一聚居地使用统一逻辑库存池，不模拟仓库到工厂的内部搬运网络。
16. **Composite 的 BOM 由最终实际 Voxel Layout 唯一派生。** 不维护第二份手写 construction cost。
17. **Composite Construction 消费完成的 Voxel Goods，不递归展开其上游 Production Recipe。**
18. **Blueprint / Construction BOM 要求精确 VoxelDefinitionId。** Tag 不提供隐式替代。
19. **所有真实安装在 Composite 中的 Voxel 都首先是可制造、可库存的 Voxel Good。**
20. **Construction、Refit、Deconstruction 都是显式耗时 Job。** 中间过程不暴露半成品结构；最终结构修改原子提交。
21. **拆卸不等于 Recovery。** 拆卸返回原有 Voxel / Item；Recovery 才进行物质转换。
22. **Capability 必须来自真实 Behavior。** Facility Tag 只负责兼容性，不能伪造 Production / Extraction / Construction / Storage / Power Capability。
23. **共享资源竞争必须显式 Resolve。** 注册顺序、容器枚举顺序、线程完成顺序不得决定 Gameplay。
24. **所有长生命周期 Job、OutputBuffer、Reservation、Fuel remainder 等影响未来模拟的状态都必须持久化。**
25. **已启动事务的承诺不能被内容热更新重新解释。** 需要冻结的执行契约在 Job 启动时冻结；外部 Definition 身份仍按现有 DefinitionId / current-definition 规则解析。
26. **系统设计优先保持宏观、可理解和可扩展。** 不为现实主义而引入没有正向 Gameplay 价值的副产品、废料、材料或中间层。

---

## 术语

### Resource Source

世界中可以通过 Extraction 获取 Raw Good 的 Voxel。

Resource Source：

- 仍然是普通 `VoxelDefinition`。
- 使用 `resource_source` Tag 做被动分类。
- 使用 `ResourceSourceBehavior` 描述实际 Yield 与 Extraction 语义。
- `CurrentDurability` 表示当前剩余可开采量。
- `CurrentDurability <= 0` 时按普通 Voxel 破坏规则销毁。

一个 Source Voxel 可以产生多种 Raw Good，但多产物是内容设计上的例外，不是默认目标。

### Raw Good

通过 Extraction 或 Primary Production 获得、尚未经过有 Gameplay 意义的工业加工的标准货物。

`raw_good` 是内容语义，不表示：

- 图论上的根节点。
- Tier 0。
- 无 Goods 输入。

例如 `Animal Products` 可以消耗 `Grain`，仍然属于 Raw Good。

### Production Process

把 Goods 与 Production Factors 转换为 Goods 的正式内容 Definition。

### Recovery Process

把耐用 Goods 或 ItemInstance 有损转换为已有 Goods 的正式内容 Definition。

Production Process 与 Recovery Process 使用不同的强类型 ID 和不同的 DAG，但复用同一套 Batch Runtime。

### Production Factors

不属于 Goods、不可库存的生产约束，包括：

- Work Capacity
- Power
- Labor
- Site / Environmental Requirements

### Facility

承担 Production、Extraction、Construction 等功能的 Building Composite。

Facility 的职责分成两层：

- **Composite Definition / Facility Tags**：决定允许什么类型的 Process。
- **内部真实 Voxel Behavior / Capability**：决定当前拥有多少实际执行能力。

---

# 1. Resource Source 与 Extraction

## 1.1 Source Definition

Resource Source 继续使用普通 VoxelDefinition，不增加独立 ResourceSourceDefinition。

```text
VoxelDefinition
├─ Tags
│  └─ resource_source
└─ Behaviors
   └─ ResourceSourceBehavior
```

Tag 只回答“这是 Resource Source”；实际产出由 Behavior 定义。

不再使用旧的“带 `natural_resource` Tag 的 Voxel 自身就是货物”语义。

## 1.2 Durability 即剩余可采量

Source 不增加 Reserve 字段。

例如：

```text
Forest Source
MaxDurability = 500
CurrentDurability = 500
```

可表示一个完整 Source Voxel 拥有标准 500 点可开采量。

同一种 Source Definition 的完整 Voxel 使用相同 MaxDurability。资源丰富度主要来自：

```text
Source 类型
× Source Voxel 数量
× 空间密度
```

而不是每个实例额外维护 Richness / Reserve。

## 1.3 Extraction 与普通 Damage

Extraction：

```text
Extraction Command / Job
→ ResourceSourceBehavior
→ formal Damage Transaction
→ Effective Damage
→ deterministic Yield
```

普通武器、事故等 Damage：

```text
Ordinary Damage
→ Damage Transaction
→ Durability decreases
→ no Raw Good yield
```

因此：

> Extraction 是 Damage 的一种显式 Gameplay 使用方式，但 Damage 不等于 Extraction。

## 1.4 Effective Damage

Yield 只能按照实际发生的 Effective Damage 计算：

```text
EffectiveDamage =
min(RequestedDamage, CurrentDurability)
```

Overkill 不得产生额外 Goods。

## 1.5 Yield 比率

ResourceSourceBehavior 支持整数比率：

```text
N Damage → M Goods
```

Goods Quantity 仍然严格为整数。

一次 Extraction 从 `oldDurability` 变为 `newDurability` 时，对某个 Yield：

```text
Yield =
floor(oldDurability × M / N)
-
floor(newDurability × M / N)
```

该公式：

- 不需要小数 Goods。
- 不需要 Yield remainder state。
- 不依赖 MaxDurability，因此内容更新 MaxDurability 不会重算过去的产出。
- 同样总 Effective Damage 分一次或多次发生，最终 Yield 一致。

若 Source 同时产出多个 Raw Good，对每种 Yield 独立应用相同原则。

## 1.6 Yield 确定性

完整 Source 的产出完全确定，不使用逐次随机掉落。

资源稀缺性通过：

- Source 类型。
- World Generation 分布。
- Source Cluster 大小。
- 地理位置。
- 可达性。

表达，而不是开采时重复掷随机数。

## 1.7 Source 耗尽

`CurrentDurability <= 0` 时 Source Voxel 按普通破坏规则消失。

不保存 Exhausted Source 空壳，也不自动转化为另一个资源状态。

未来若某种内容确实需要耗尽后留下专门地形，应作为明确的额外 Gameplay 行为设计，而不是 Resource Source 的通用规则。

## 1.8 Source Cluster

“矿床”“森林资源区”等不建立新的 Runtime Entity。

例如大型矿床只是多个实际 Source Voxel 的空间 Cluster：

```text
■■■■■
■■■■■■
 ■■■■
  ■■
```

其总储量由实际 Source Voxels 的 CurrentDurability 与 Yield 派生。

不建立第二份：

```text
DepositInstance.Reserve
```

作为权威事实。

Cluster 只可以作为：

- World Generation 概念。
- 地图/UI 聚类查询。
- AI 探勘结果。

## 1.9 Extraction Facility

Extraction 必须由真实 Facility 提供，不允许 Settlement 拥有凭空的 `MiningCapacity`。

```text
Extraction Facility Composite
        ↓
Extraction Equipment
        ↓
ExtractionBehavior / ExtractionCapability
        ↓
Extraction Job
```

首版使用一个通用：

```text
Extraction Equipment
```

不提前拆分 Mining Drill、Logging Machine、Oil Pump。

## 1.10 Extraction Facility Compatibility

资源类型差异通过 Facility Tag 与 Source 要求表达，例如：

```text
Mining Facility
facility_tags = [mining]

Forestry Facility
facility_tags = [forestry]

Oil Extraction Facility
facility_tags = [oil_extraction]
```

通用 Extraction Equipment 只提供实际 Extraction Capability。

## 1.11 Extraction Range

每个 Extraction Facility 只能选择其明确 Extraction Area / Range 内、类型兼容的 Source Voxel。

具体范围形状与数值不冻结。

底层：

```text
ExtractionJob.Target = exact WorldVoxelCoord
```

Job 一次明确指向一个 Source Voxel。

“自动开采整片区域”属于更高层 Player / AI Extraction Plan，不由底层 Extraction Runtime 自动承担。

## 1.12 Extraction Work

Extraction 使用连续 Work → 离散 Damage：

```text
ExtractionJob
├─ Target Source
├─ ProgressWork
└─ WorkPerDamage
```

当累计 Work 达到 `WorkPerDamage`：

```text
commit 1 Extraction Damage
→ calculate Yield
```

Source Durability 保持整数。

## 1.13 Extraction Output

Extraction 输出复用 Facility-local OutputBuffer 规则。

若 Settlement Inventory 无空间：

```text
Raw Goods
→ Facility OutputBuffer
→ facility blocked until buffer cleared
```

## 1.14 Resource Renewal

不可再生资源不在原地 Respawn。

长期可持续通过 `Resource Renewal Event`：

```text
existing resource area depleted
        ↓
Resource Renewal Event
        ↓
select eligible undeveloped region
        ↓
generate new Resource Source cluster
```

新资源区的选择必须服从 WorldSeed 与稳定随机键的确定性规则。

以下参数不冻结：

- 触发条件。
- 触发间隔。
- 新 Cluster 大小。
- Eligible / undeveloped region 的精确定义。

---

# 2. Raw Goods

首版 Raw Goods 使用克制的功能性抽象。

## 2.1 Geological

```text
Ferrous Ore
Conductive Ore
Rare Mineral
Stone
Silica
Coal
Crude Oil
```

### Ferrous Ore

廉价、大宗、结构工业的基础金属矿。

### Conductive Ore

电气工业的基础导电金属矿。

### Rare Mineral

抽象稀有金属、稀土和高级工业矿物。

### Stone

建筑材料基础。

### Silica

Glass 与部分精密工业基础。

### Coal

同时连接：

- Structural Metal 冶金。
- Fuel 路线。

### Crude Oil

同时连接：

- Fuel。
- Chemical Feedstock。
- Polymer 化工链。

## 2.2 Biological / Primary

```text
Grain
Produce
Animal Products
Timber
Natural Fiber
```

不拆 Apple / Banana、Beef / Pork、Cotton / Wool 等，除非未来能通过 Gameplay Difference Test。

## 2.3 首批 Resource Sources

```text
Ferrous Ore Deposit      → Ferrous Ore
Conductive Ore Deposit   → Conductive Ore
Rare Mineral Deposit     → Rare Mineral
Stone Deposit            → Stone
Silica Deposit           → Silica
Coal Deposit             → Coal
Oil Deposit              → Crude Oil
Natural Forest           → Timber
```

Source 机制允许多产物，但首批内容默认单主产物。

## 2.4 Primary Production

首批：

```text
Grain Farming        → Grain
Produce Farming      → Produce
Livestock Production → Animal Products
Fiber Farming        → Natural Fiber
Forestry             → Timber
```

其中：

```text
Livestock Production
Inputs:
  Grain
Outputs:
  Animal Products
```

其余四种可以没有 Goods Input，由 Site、Work、Labor 等 Production Factors 约束。

不为了现实主义强行添加：

- Seed
- Water
- Fertilizer
- Sapling
- Feed

等商品。

若未来这些对象产生明确贸易或产业策略价值，再加入。

---

# 3. Forward Production DAG

Forward Production DAG 只表达实际 Goods 生产关系，不存在 Tier。

默认每个商品只有一条 canonical Production Route；只有替代路线产生明确 Gameplay Difference 时才增加额外 Process。

## 3.1 基础工业材料

```text
Ferrous Ore + Coal
→ Structural Metal
```

```text
Conductive Ore
→ Conductive Metal
```

```text
Rare Mineral
→ Specialty Material
```

```text
Stone
→ Construction Material
```

```text
Silica
→ Glass
```

```text
Timber
→ Lumber
```

```text
Natural Fiber
→ Textile
```

## 3.2 Oil / Fuel / Chemical

首个有意义的多输出 Process：

```text
Crude Oil
→ Fuel
+ Chemical Feedstock
```

同时允许明确的替代 Fuel 路线：

```text
Coal
→ Fuel
```

化工继续：

```text
Chemical Feedstock
→ Polymer
```

不增加通用 `Industrial Chemicals` 中间层。

## 3.3 Components

```text
Structural Metal
+ Polymer
→ Mechanical Components
```

```text
Conductive Metal
+ Structural Metal
→ Electrical Components
```

```text
Electrical Components
+ Specialty Material
+ Glass
+ Polymer
→ Electronic Components
```

不提前增加 Precision Components / Advanced Components。

## 3.4 Civilian Goods

```text
Grain
→ Staple Food
```

```text
Grain
+ Produce
+ Animal Products
→ Prepared Food
```

Raw Food 本身也可以拥有未来的最终消费用途；是否被继续加工不构成商品身份限制。

```text
Textile
→ Clothing
```

```text
Lumber
+ Polymer
+ Structural Metal
+ Glass
→ Consumer Goods
```

```text
Electronic Components
+ Polymer
+ Glass
→ Consumer Electronics
```

```text
Chemical Feedstock
+ Textile
+ Polymer
→ Medical Supplies
```

Medical Supplies 是消耗性 Goods，不是永久安装设备。

具体人口/乘员如何消耗 Medical Supplies 留待人口与乘员系统设计。

## 3.5 Construction Goods

首版只冻结：

```text
Structural Metal
→ Structural Block
```

```text
Construction Material
→ Construction Block
```

```text
Structural Metal
→ Rail
```

首版不预置：

- Window
- Door
- Armor Block
- Decoration Goods

未来只有实际 Gameplay 需要时再添加。

Rail 是正常 Voxel Good；直线、曲线、坡道等仍然是实例空间拓扑，不产生不同 Rail 商品。

## 3.6 Functional Equipment

首版存在：

```text
Industrial Machinery
Storage Equipment
Power Equipment
Control Equipment
Extraction Equipment
Construction Equipment
```

推荐骨架：

```text
Industrial Machinery
← Structural Metal
 + Mechanical Components
 + Electrical Components
```

```text
Storage Equipment
← Structural Metal
 + Mechanical Components
```

```text
Power Equipment
← Structural Metal
 + Mechanical Components
 + Electrical Components
```

```text
Extraction Equipment
← Structural Metal
 + Mechanical Components
 + Electrical Components
```

```text
Construction Equipment
← Structural Metal
 + Mechanical Components
 + Electrical Components
```

```text
Control Equipment
← Electrical Components
 + Electronic Components
 + Polymer
```

具体整数配方数量后续平衡。

不同功能设备即使 BOM 类似，也因为 Behavior / Capability 不同而是不同 VoxelDefinition。

## 3.7 Future Product Families

以下仅作为设计占位，不创建万能 VoxelDefinition：

```text
Propulsion Equipment
Train Weapons
Crew Weapons
Train Equipment
Crew Equipment
```

对应 Gameplay 系统设计完成后，再创建具体产品 VoxelDefinition 与具体 Production Process。

---

# 4. ProductionProcessDefinition 与 RecoveryProcessDefinition

## 4.1 稳定身份

Process 是正式内容 Definition，使用强类型、namespaced ID：

```text
ProductionProcessDefinitionId
RecoveryProcessDefinitionId
```

例如：

```text
core:ferrous-refining
core:mechanical-components
core:recover-power-equipment
```

显示名称、文件路径不是身份。

## 4.2 ProductionProcessDefinition

核心语义：

```text
ProductionProcessDefinition
├─ ProcessDefinitionId
├─ Inputs
├─ Outputs
├─ WorkRequired
├─ PowerPerWork
├─ LaborPerWork
├─ RequiredFacilityTags
└─ EnvironmentalRequirements
```

所有 Goods Input / Output Quantity 为正整数。

Process 可以：

- 多输入。
- 多输出。
- 跨所谓“加工层级”直接引用已有 Goods。
- 与同一 Output 的其他显式替代 Process 并存。

Process 不允许：

- 隐式 Tag 替代 Input。
- `any metal` 之类自动配方替换。

若同一 Product 有两种路线，必须是两个显式 ProcessDefinition。

## 4.3 RecoveryProcessDefinition

Recovery 使用单独 Definition 类型和 DAG，但 Runtime 执行复用 Production Batch Framework。

Recovery 输入可以是：

- Stack Good。
- 指定 ItemInstance。

Recovery 不从 Production Recipe 自动反推。

## 4.4 Facility Compatibility

Building Composite Definition 保存 Facility compatibility tags，例如：

```text
metallurgy
assembly
textile
food_processing
agriculture
recovery
mining
forestry
oil_extraction
```

Tag 只表示“此 Facility 允许执行某类 Process”。

实际执行还必须拥有来自真实 Voxel Behavior 的对应 Capability。

## 4.5 Environmental Requirements

Site / Environmental Requirements 属于 Process，而不是硬编码在 Building 类型中。

例如同一个 Agricultural Facility 可以兼容 Grain Farming 和 Produce Farming，但不同 Process 可以拥有不同环境要求。

运行时当前环境必须继续满足 Requirements。

语义上是持续有效条件；实现可以通过：

- Construction。
- Site state change。
- Process start。

等事件重算并缓存，而不要求每 Tick 全地图扫描。

---

# 5. Production Runtime

## 5.1 Production Plan

底层 Production Plan 只表达：

```text
ProductionPlan
├─ ProcessDefinitionId
├─ Enabled
└─ Priority
```

当 Enabled 时，Facility 持续尝试执行该 Process。

底层系统不负责：

- 自动判断最赚钱商品。
- 自动维持目标库存。
- 自动决定生产多少 Batch。

这些属于未来 Player Policy / Settlement AI / Economy 层。

缺 Input 时：

```text
Enabled = true
ActiveBatch = none
WaitingForInputs
```

新 Input 到达后自动继续尝试。

## 5.2 Active Batch

ActiveBatch 是权威 Runtime State：

```text
ActiveBatch
├─ ProcessDefinitionId
├─ StartedFromRevision
├─ FrozenExecutionSnapshot
├─ ProgressWork
└─ diagnostics / provenance as needed
```

Goods 只在完整 Batch 上转换。

Work 可以连续积累。

## 5.3 Batch Start

Batch 启动必须 All-or-Nothing：

```text
check all Inputs
check Facility compatibility
check current execution eligibility
resolve shared resource competition
        ↓
consume complete Inputs atomically
create ActiveBatch
```

不允许：

- 半装料 Batch。
- 缺一个 Input 时先扣其他 Input。
- 完成时才临时检查 Input。

Inputs 一旦扣除，就已经进入在制品状态。

## 5.4 Work

Production 使用抽象 Work：

```text
BatchCompletionTime
= WorkRequired / actual executable Work rate
```

actual Work 受到：

- Production Capability。
- Power。
- Labor。

共同限制。

## 5.5 Production Capability

实际 Work Capacity 主要来自：

```text
Industrial Machinery
→ ProductionBehavior
→ ProductionCapability
```

首版线性叠加：

```text
N Industrial Machinery
→ N × base capability
```

暂不加入：

- 邻接 Bonus。
- 边际递减。
- 规模经济。
- 复杂布局效率。

## 5.6 Power

Power 不是 Goods，不可库存。

Settlement 使用统一 Power Pool：

```text
all active Power Equipment
→ Settlement Power Pool
```

不模拟内部：

- Cable。
- Substation。
- Grid topology。

Power Equipment 消耗 Fuel：

```text
Settlement Inventory Fuel
→ Power Equipment
→ Power Pool
```

采用 demand-driven 发电：

```text
actual delivered Power
→ proportional FuelEnergy consumption
```

闲置 Power Equipment 不按额定功率白白燃料。

## 5.7 Integer Fuel + Continuous Internal Energy

Fuel Inventory Quantity 仍然是整数。

Power Equipment 每次取出一个完整 Fuel unit，并保存设备 Runtime State：

```text
CurrentFuelEnergyRemaining : double
```

实际发电持续扣减。

该状态：

- Pause 时保留。
- Save / Load 时保留。
- 拆卸进入 ItemInstance 时保留。
- 重新安装后继续保留。
- 设备被摧毁时丢失。

它不是可库存 Power。

## 5.8 Labor

Labor 不属于 Goods，不可库存、不被正常生产“消耗”。

Settlement 的外部人口系统未来提供：

```text
AvailableLaborCapacity
```

生产只是占用：

```text
AllocatedLabor
```

停止生产后释放。

人口死亡、迁移、增长等规则不在本文设计。

## 5.9 Factor Intensity

Process 使用：

```text
PowerPerWork
LaborPerWork
```

实际执行 Work 与 Factor 消耗成比例。

Factor 不足时：

```text
actual Work reduced or zero
```

Progress 不损失。

## 5.10 Shared Resource Resolver

多个 Facility 竞争：

- 同一库存 Input。
- Power。
- Labor。
- Storage Capacity。

必须通过 Resolver。

基本排序：

```text
explicit Production Priority
→ stable deterministic fallback
```

Input 竞争按完整 Batch grant。

Power / Labor 允许部分分配。

不得使用：

- Dictionary iteration order。
- System registration order。
- Thread completion order。

作为 Gameplay priority。

## 5.11 单 Facility 并行限制

首版：

```text
Production Capability
→ at most 1 Production or Recovery Batch

Extraction Capability
→ at most 1 Extraction Job

Construction Capability
→ at most 1 Construction / Refit / Deconstruction Job
```

如果同一 Building 同时拥有不同 Capability，可以并行运行不同 Capability 的 Job，并共同竞争 Power / Labor。

不允许在同一 Capability 内手工分配：

```text
40% Job A
30% Job B
30% Job C
```

## 5.12 Tick Barrier

所有 Job 严格遵守 Simulation Tick Barrier。

例如本 Tick 清空 OutputBuffer 后，不允许同 Tick 继续启动下一 Batch。

状态变化先 Commit，下一 Tick 才基于新的 committed state 继续评估。

---

# 6. Output Buffer

## 6.1 Facility-local

每个生产 / 回收 / Extraction Facility 拥有自己的 OutputBuffer。

不存在 Settlement-wide Production Buffer。

## 6.2 只保存一个完成 Batch 的剩余输出

Buffer 不拥有独立 Gameplay Capacity。

Batch 完成时：

```text
produce complete integer Outputs
→ move as much as possible into Settlement Inventory
→ remainder stays in Facility OutputBuffer
```

若 Buffer 非空：

```text
this Facility production/extraction is blocked
```

其他 Facility 正常运行。

## 6.3 Buffer 中的 Goods

OutputBuffer 中的 Goods 已经是真实完成 Goods，但：

- 不占 Settlement Inventory Capacity。
- 不能参与普通 Settlement Inventory 消费。
- 不能作为其他 Facility Input。
- 不能参与未来市场交易。
- 必须先进入 Settlement Inventory。

## 6.4 自动清空

只要 Settlement Inventory 释放空间：

```text
OutputBuffer
→ automatic internal transfer
→ Settlement Inventory
```

Facility 即使生产设备暂时失效，只要 Facility Composite 仍存在，Buffer 仍可继续执行这种抽象内部物流。

## 6.5 Storage Resolver 优先级

已完成 OutputBuffer 的转入优先于新的普通外部入库请求。

多个 Buffer 竞争时：

```text
Production Priority
→ stable Facility identity
```

作为稳定顺序。

## 6.6 多输出顺序

当一个多输出 Batch 只有部分 Output 能立即入库时，使用规范化 `VoxelDefinitionId` Ordinal 顺序填充空间。

这只是确定性 fallback，不表示经济优先级。

## 6.7 Facility destroyed

Facility Composite 真正 destroyed：

- ActiveBatch 丢失。
- 未完成 Inputs 不返还。
- OutputBuffer 中已完成 Goods 丢失。

未来事故 / Salvage 系统可以扩展，但首版不自动实体化散落物，也不强制写入 Overcapacity。

---

# 7. Inventory 与 Storage

## 7.1 地点库存

不存在全局 Player Inventory。

资源系统至少区分：

- Settlement Inventory。
- Train Inventory。
- Facility OutputBuffer。
- 未来其他明确地点 Inventory。

## 7.2 Settlement Inventory

同一 Settlement 使用统一逻辑库存池。

Facility 直接：

```text
Settlement Inventory
→ Inputs
→ Facility
→ Outputs
→ Settlement Inventory
```

不模拟：

- Factory input buffer。
- Conveyor。
- Warehouse-to-factory truck。
- Settlement 内部物流路径。

## 7.3 Train ↔ Settlement Transfer

Train Inventory 与 Settlement Inventory 不自动合并。

必须有显式 Transfer：

```text
Train Inventory
↔
Settlement Inventory
```

生产 Facility 不能直接读取停靠列车货舱。

Transfer 自动化规则以后可以设计，但必须仍是真实库存移动。

## 7.4 Storage Capacity

每个 Goods unit 占 1 点 Storage Capacity。

```text
UsedCapacity =
Σ Stack.Quantity
+ ItemInstance.Count
```

Mass 独立计算：

```text
TotalCargoMass =
Σ Definition.Mass × quantity
```

首版不增加：

- Liquid-only capacity。
- Refrigerated capacity。
- Hazardous capacity。
- Cargo weight limit。

Crude Oil 等仍然作为普通标准库存 Goods。

## 7.5 Storage Capability

Train：

```text
installed Storage Equipment
→ StorageCapability
→ Train Inventory Capacity
```

Settlement：

```text
Warehouse Building Composite
└─ actual Storage Equipment
   → StorageCapability
   → Settlement Inventory Capacity
```

表面上“建仓库增加容量”，底层事实仍由仓库内实际 Storage Voxel 提供。

## 7.6 Overcapacity

若 Storage Capability 因损伤下降到低于当前 UsedCapacity：

```text
Used > Capacity
```

允许临时 Overcapacity。

规则：

- 已有 Goods 不自动删除。
- Goods 可以被取出。
- 不允许新的普通入库。
- 修复 Storage 或减少库存后恢复正常。

事故导致货物散落属于未来显式事故 Gameplay，不由 Storage Capacity 通用规则自动触发。

## 7.7 Stack 与 ItemInstance

统一 Stackability predicate：

只有当一个 Voxel：

- CurrentDurability = current Definition MaxDurability。
- 不存在非默认 Behavior Runtime State。
- 不需要保留长期 Runtime Identity。
- 不存在其他 Gameplay-significant instance state。

才可以折叠为：

```text
Stack
{ VoxelDefinitionId, Quantity }
```

否则进入：

```text
ItemInstance
├─ DefinitionId
├─ Durability
├─ Runtime Identity if needed
└─ Behavior Runtime State
```

例如：

```text
new Engine
→ Stack

Power Equipment with partial fuel
→ ItemInstance

70% Structural Block
→ ItemInstance
```

Stack unit 与 ItemInstance 都占 1 Storage Capacity。

## 7.8 BOM 自动消费 ItemInstance

Construction / Refit 自动满足 BOM 时：

- Stack Good 可自动消费。
- ItemInstance 必须由玩家或上层计划显式选定。

若 Stack 不足且没有明确选定 ItemInstance：

```text
validation fails
```

系统不自动决定“最好耐久”“最差耐久”等策略。

---

# 8. Paint / Presentation Override

纯表现差异不创建不同 Good Definition。

不存在：

```text
Red Structural Block
Blue Structural Block
```

作为经济商品。

PaintColor 属于空间实例 / Composite 的 Presentation Override。

改变颜色：

- 不改变 VoxelDefinitionId。
- 不改变 Value。
- 不改变 Production Recipe。
- 不改变经济身份。

Paint 不作为需要随库存 ItemInstance 保留的 Gameplay State。

涂装 Voxel 被拆回库存后，其颜色 Override 消失；重新安装后可以重新涂装。

---

# 9. Composite Construction

## 9.1 BOM 唯一事实来源

Composite BOM 完全从最终 Voxel Layout 派生。

例如：

```text
Locomotive Layout
├─ Structural Block ×120
├─ Engine X ×2
├─ Storage Equipment ×4
└─ ...
```

BOM 即这些实际 VoxelDefinitionId 与 Quantity。

不另设：

```text
construction_cost
```

作为第二份权威数据。

非 Voxel 元数据：

- Rail Contact。
- Size class。
- Archetype。
- Connector metadata。

不产生物料成本。

## 9.2 精确 DefinitionId

BOM 要求确切 VoxelDefinitionId。

Tag 不允许自动替代：

```text
Basic Engine
≠
Advanced Engine
```

想换另一种设备必须修改 Blueprint / Refit Plan。

## 9.3 不递归展开配方

Construction 消费完成 Goods：

```text
Mechanical Components
→ produce Engine
→ Engine enters Inventory
→ Construction consumes Engine
```

Construction 不允许：

```text
missing Engine
→ automatically expand Engine recipe
→ directly consume upstream materials
```

这是核心经济边界之一。

## 9.4 Production Good → VoxelInstance

库存 Goods 只有在显式：

- Placement。
- Construction。
- Refit add / replacement。

时实体化。

新实例初始化：

```text
DefinitionId
CurrentDurability = current MaxDurability
default Behavior Runtime State
RuntimeId if required
```

## 9.5 Construction Job

正式 Gameplay 的 Composite Construction 是耗时 Job。

Construction 期间不存在半成品 Composite。

```text
ConstructionJob
0% ──────────→ 100%
                  ↓
           atomic instantiate
```

视觉层以后可以表现脚手架、施工动画等，但不是权威 Composite。

## 9.6 Construction Job start

启动时：

```text
validate Blueprint / Definition
validate final placement
derive exact BOM
validate accessible inventories
validate Construction Capability
validate Site constraints
        ↓
consume complete BOM atomically
reserve final spatial area
create ConstructionJob
```

BOM 在启动时完整消耗。

不允许 Cancel 后全额退款。

## 9.7 Accessible inventories

在玩家自己的聚居地施工时，可以同时使用：

- 当前玩家 Settlement Inventory。
- 当前所在地可访问的玩家 Train Inventory。

它们仍然是两个独立 Inventory。

Construction start transaction 必须记录实际消费来源，并采用稳定顺序或显式选择。

首版可以默认：

```text
Settlement Inventory first
→ Train Inventory
```

未来 UI 可以允许指定来源。

## 9.8 Spatial Reservation

Construction Job 必须预留最终 Composite 所需空间。

Reservation：

- 不是 Voxel occupant。
- 不创建半成品 Collision。
- 阻止其他 Placement / Construction 占用目标区域。
- 属于权威 Runtime State。
- 必须存档。

若外围世界变化导致最终 Placement / Site 约束失效：

```text
Construction Job fails
→ consumed BOM lost
→ Progress discarded
→ Reservation released
```

Reservation 只保护直接施工区域，不冻结附近世界。

## 9.9 Construction Capability

实际施工能力来自：

```text
Construction Equipment
→ ConstructionBehavior
→ ConstructionCapability
```

不由 Building 类型凭空产生。

Construction Work 使用统一 Work / Power / Labor 模型。

## 9.10 Construction Work

首版：

```text
ConstructionWork =
VoxelCount × BaseConstructionWorkPerVoxel
```

不为每种 VoxelDefinition 增加 AssemblyWork。

复杂部件的制造复杂度已经体现在其 Production DAG 中；Construction 只模拟安装完成部件。

## 9.11 Construction snapshot

Job 启动时冻结：

- Blueprint / Layout snapshot。
- VoxelDefinitionId 列表。
- Orientation。
- target placement。
- spatial reservation。

之后修改 Blueprint 不改变已经运行的 Job。

但 Snapshot 不嵌入所有外部 VoxelDefinition 的旧规格。

完成时：

```text
VoxelDefinitionId
→ resolve current installed Definition
```

新实例采用当前规格。

若热更新删除 Job 必需 DefinitionId，则内容发布拒绝。

---

# 10. Refit

## 10.1 正式 Gameplay 使用耗时 Refit Job

现有开发测试场景可以继续拥有瞬时 Atomic Refit 入口，用于验证结构修改。

正式 Gameplay：

```text
Refit Planning
→ validate final layout
→ derive delta BOM
→ consume added/replacement Goods
→ Refit Job
→ atomic final commit
```

## 10.2 Delta BOM

只消费新增或替换进去的 Goods。

例如：

```text
old:
100 Structural Block
2 Engine

new:
105 Structural Block
2 same Engine
1 Storage Equipment
```

需要：

```text
Structural Block ×5
Storage Equipment ×1
```

移动、旋转已有 Voxel 不消费新 Goods。

## 10.3 Identity

Move / Rotate 保留原 Voxel identity 与 Runtime State。

Replace：

```text
old Voxel removed
new Good materialized
```

旧件进入：

- Stack，若满足 Stackability。
- ItemInstance，否则。

## 10.4 Refit Work

首版：

```text
RefitWork =
ChangedVoxelCount × BaseRefitWorkPerChangedVoxel
```

Add、Remove、Move、Rotate、Replace 各算一个修改单位。

不进一步为不同操作设置权重。

## 10.5 UnderRefit

Refit 期间目标 Composite 进入锁定状态：

```text
UnderRefit
```

例如列车：

- 不能正常运行。
- 不能继续贸易运营。
- 不能同时启动第二个 Refit。
- 不能同时 Deconstruct。

完成时才原子切换最终 Layout。

## 10.6 Refit snapshot

Job 启动时冻结：

- target Composite identity。
- expected source Voxel identity/layout。
- final validated layout。
- move / rotate / add / remove / replace mapping。
- selected ItemInstances。
- ProgressWork。

Blueprint 后续修改不影响当前 Job。

## 10.7 外部结构变化

若 Refit 期间 target 的 Voxel layout / identity set 被外部事件改变：

```text
Refit Job fails
→ Progress discarded
→ consumed added/replacement Goods lost
→ target keeps current actual state
```

若只是 Durability 改变而 Voxel 仍存在，Job 可继续；完成时保留其最新 Runtime State。

---

# 11. Deconstruction

## 11.1 Composite 不能直接 Recovery

禁止：

```text
Locomotive Composite
→ Recovery
→ materials
```

Composite 必须先显式 Deconstruction。

## 11.2 Deconstruction Job

```text
Composite
→ Deconstruction Job
→ atomic completion
→ Composite removed
→ actual Voxels returned to inventory
```

使用 Construction Capability，不新增 Deconstruction Equipment。

## 11.3 No partial deconstruction

0–99%：

- 原 Composite 完整存在。
- Occupancy 不变。
- 不逐 Voxel 消失。

100%：

```text
atomic remove Composite
→ transfer actual Voxels to inventory
```

## 11.4 UnderDeconstruction

期间：

- Composite 不可正常运营。
- Building 不执行 Production。
- Train 不运行。
- 不允许 Refit。
- 不允许第二个 Deconstruction。

## 11.5 Storage Reservation

启动前必须预留所有预计返回部件所需 Storage Capacity。

与 Production OutputBuffer 不同，Deconstruction 的输出在开始前完全已知，因此使用 Reservation，不允许完成后制造巨大临时 Buffer。

## 11.6 Identity / Runtime State

Deconstruction 返回原来的 Voxel：

```text
Engine #812
Durability = 63
Behavior State = ...
```

仍然是同一设备。

符合 Stackability 时可以折叠 Stack，否则返回 ItemInstance。

Deconstruction 不自动修复部件。

## 11.7 Work

首版：

```text
DeconstructionWork =
VoxelCount × BaseDeconstructionWorkPerVoxel
```

## 11.8 Cancel

Active Deconstruction Job 不允许主动取消，只允许暂停。

## 11.9 外部结构变化

若目标 Voxel Layout / Identity Set 发生结构变化：

```text
Deconstruction Job fails
→ Progress discarded
→ Storage reservation released
→ Composite keeps actual post-event state
```

普通 Durability 变化若未删除 Voxel，不令 Job 失败；最终返回最新状态。

---

# 12. Recovery

## 12.1 两张独立 DAG

```text
Forward Production DAG
Natural / Primary
→ Materials
→ Components
→ Goods / Equipment
```

```text
Recovery DAG
Durable Goods / Equipment
→ Components / Materials
→ Materials
```

二者分别无环。

## 12.2 Explicit opt-in

只有存在明确 RecoveryProcessDefinition 的 Goods 才可回收。

系统不自动推导“所有东西都能拆”。

## 12.3 首版覆盖范围

首版重点覆盖：

### Construction Goods

```text
Structural Block
Construction Block
Rail
```

### Components

```text
Mechanical Components
Electrical Components
Electronic Components
```

### Functional Equipment

```text
Industrial Machinery
Storage Equipment
Power Equipment
Control Equipment
Extraction Equipment
Construction Equipment
```

默认不为以下内容建立 Recovery：

- Raw Goods。
- Basic Materials。
- Food。
- Fuel。
- Chemical Feedstock。
- Clothing。
- Medical Supplies。
- 普通民用消费品。

未来按 Gameplay 需要显式增加。

## 12.4 不使用万能 Scrap

首版没有：

```text
Scrap
Waste
Broken Electronics
```

等通用回收中间品。

如果未来废料运输本身成为有价值的物流 Gameplay，再设计具体废料 Good。

## 12.5 多阶段 Recovery

允许：

```text
Equipment
→ Components
→ Materials
```

而不是要求所有 Recovery 一步还原到底层资源。

## 12.6 Recovery outputs

Recovery 只输出已经存在于 Forward Goods Catalog 中的 Goods。

不为了回收单独创建另一棵废品资源树。

## 12.7 首批 Recovery Skeleton

### Construction Goods

```text
Structural Block
→ Structural Metal
```

```text
Construction Block
→ Construction Material
```

```text
Rail
→ Structural Metal
```

### Components

```text
Mechanical Components
→ Structural Metal
+ Polymer
```

```text
Electrical Components
→ Conductive Metal
+ Structural Metal
```

```text
Electronic Components
→ Electrical Components
+ Specialty Material
+ Polymer
```

### Functional Equipment

```text
Industrial Machinery
→ Mechanical Components
+ Electrical Components
+ Structural Metal
```

```text
Storage Equipment
→ Mechanical Components
+ Structural Metal
```

```text
Power Equipment
→ Mechanical Components
+ Electrical Components
+ Structural Metal
```

```text
Control Equipment
→ Electronic Components
+ Electrical Components
+ Polymer
```

```text
Extraction Equipment
→ Mechanical Components
+ Electrical Components
+ Structural Metal
```

```text
Construction Equipment
→ Mechanical Components
+ Electrical Components
+ Structural Metal
```

具体数量后续平衡。

Recovery 明确不保证还原所有 Production Inputs。

## 12.8 Stack 与 ItemInstance Recovery

Stack Good：

```text
DefinitionId + Quantity
```

按完整标准状态执行 Recovery。

ItemInstance：

```text
specific identity
+ CurrentDurability
+ Runtime State
```

Recovery 必须真正消费指定实例。

## 12.9 Durability Yield

对 ItemInstance：

```text
Recovered_i =
floor(
  BaseRecoveryOutput_i
  × CurrentDurability
  / MaxDurability
)
```

每种 Output 独立计算。

结果可以为 0。

Recovery 不保存小数 remainder，也不随机补偿。

## 12.10 其他 Runtime State

除明确参与 Recovery 计算的状态外，其余 Runtime State 在 Recovery 中销毁。

例如 Power Equipment：

```text
CurrentFuelEnergyRemaining
```

不会自动还原为部分 Fuel Good。

---

# 13. Facility 与 Capability

## 13.1 Facility 是真实 Building Composite

Settlement 不直接拥有抽象：

```text
IndustrialCapacity
MiningCapacity
StorageCapacity
```

作为无来源事实。

能力来自实际 Building Composite 内的功能 Voxel。

## 13.2 Facility Definition 与 Runtime Capability 分离

```text
Building Composite Definition
→ facility tags / process compatibility
```

```text
runtime internal Voxels
→ Behaviors
→ Capabilities
```

例如：

```text
Smelter
facility_tags = [metallurgy]

Industrial Machinery inside Smelter
→ actual ProductionCapability
```

即使设备全部损坏：

```text
facility tag still metallurgy
ProductionCapability = 0
```

## 13.3 通用设备

首版通用能力设备：

```text
Industrial Machinery
→ ProductionCapability

Extraction Equipment
→ ExtractionCapability

Construction Equipment
→ ConstructionCapability

Storage Equipment
→ StorageCapability

Power Equipment
→ PowerCapability

Control Equipment
→ corresponding future control capabilities
```

Control Equipment 的具体玩法由后续系统定义。

## 13.4 Job 与 Facility lifecycle

存在以下任意状态时，Facility 不允许直接进入 Refit / Deconstruction：

```text
ActiveBatch != none
OutputBuffer != empty
ExtractionJob != none
RecoveryJob != none
```

持续 Production Plan 应先：

```text
Enabled = false
→ current Batch naturally completes
→ OutputBuffer cleared
→ facility idle
→ begin Refit / Deconstruction
```

施工系统不隐式 Cancel 生产事务。

---

# 14. Job 失败与暂停语义

## 14.1 Production / Recovery

Power / Labor 不足：

```text
Progress preserved
ActualWork reduced or zero
```

Facility 仍存在但 Capability 暂时为 0：

- ActiveBatch 保留。
- OutputBuffer 保留。
- Production Plan 保留。
- 修复后继续。

Facility destroyed：

- ActiveBatch 丢失。
- Inputs 不返还。
- OutputBuffer Goods 丢失。

## 14.2 Extraction

Source destroyed / invalid：

Job 结束。

若内容热更新使正在运行的 Source：

- 不再是 Resource Source。
- 不再具有 ResourceSourceBehavior。
- 与 Facility 不再兼容。

则拒绝本次内容发布，不静默取消正在运行 Job。

## 14.3 Construction

Power / Labor / Capability 不足：

- Progress 保留。
- BOM 不返还。
- Reservation 保留。

Site / placement validity 失效：

- Job fail。
- Progress 丢失。
- 已投入 BOM 丢失。
- Reservation 释放。

Construction Facility destroyed / 失去 Job 执行资格：

- Job fail。
- 已投入 Goods 不返还。
- Reservation 释放。

## 14.4 Refit

结构身份集合变化：

- Job fail。
- 已投入新增/替换 Goods 不返还。
- 现有 Composite 保持真实事件后状态。

## 14.5 Deconstruction

结构身份集合变化：

- Job fail。
- Progress 丢失。
- Storage reservation 释放。
- Composite 保持事件后状态。

---

# 15. Save / Load

以下全部属于权威 Runtime State，必须进入 Save：

```text
Production Plans
ActiveBatch
OutputBuffer
ExtractionJob
ConstructionJob
RefitJob
DeconstructionJob
Spatial Reservations
Storage Reservations
CurrentFuelEnergyRemaining
selected ItemInstances / identity mapping where required
job ProgressWork
job priorities
```

Save → Load → Continue 必须与不中断运行在相同语义内容和命令输入下产生一致结果。

这些状态不是渲染缓存，也不能读档后重置。

---

# 16. 内容更新与热重载

## 16.1 Process Definition

ProductionProcessDefinition 与 RecoveryProcessDefinition 使用稳定 ID 和 Semantic Revision。

## 16.2 ActiveBatch frozen execution snapshot

Batch 启动时冻结其执行契约：

```text
ProcessDefinitionId
StartedFromRevision
Frozen Outputs
Frozen WorkRequired
Frozen factor intensities
ProgressWork
```

Inputs 已在启动时消费。

当前 Batch 按启动时 snapshot 完成。

下一 Batch：

```text
ProcessDefinitionId
→ resolve current Definition
```

使用当前规格。

因此 Process 热更新不会出现：

> 按旧 Inputs 启动、按新 Outputs 完成。

## 16.3 Production Plan

Plan 只保存稳定 ProcessDefinitionId。

若更新删除仍被 Plan / ActiveBatch 引用的必需 ProcessDefinitionId：

```text
reject entire content publish
```

不静默关闭工厂。

## 16.4 Extraction

Extraction Job 不冻结整个 Yield Definition。

已经发生的 Damage 不重算。

下一次 Extraction Damage 使用当前 ResourceSourceBehavior。

若更新导致现有 Job 无法解释，拒绝发布。

## 16.5 Construction / Refit Snapshot

Construction / Refit 冻结：

- Layout。
- DefinitionId。
- Orientation。
- identity mapping。
- selected ItemInstances。
- target placement / plan。

但不嵌入所有外部 VoxelDefinition 的旧 Revision。

完成时：

```text
DefinitionId
→ current installed Definition
```

若必需 DefinitionId 被删除，拒绝热重载。

## 16.6 Runtime Capability

Job 不冻结执行能力。

每 Tick 都读取当前：

```text
Capability
Power
Labor
Facility operational state
```

设备受损、修复或内容规格更新可以自然改变执行速度。

---

# 17. 内容设计原则

## 17.1 Gameplay Difference Test

拆分新 Goods 前必须至少能回答：

> 它是否改变玩家的资源地域选择、加工路线、运输、市场需求、最终功能或其他有效决策？

如果答案是否定，就不拆。

因此首版不设计：

- Apple / Banana。
- Beef / Pork。
- Gear / Bearing / Wire。
- Cotton / Wool。
- Coke。
- Industrial Chemicals。
- Precision Components。
- Scrap。
- Waste。
- 大量 Decoration Goods。

## 17.2 副产品

系统允许多输出 Process，但内容上克制使用。

副产品必须对 Gameplay 有正向意义，否则不设计。

多输出不是现实主义展示工具。

## 17.3 商品阶段不是互斥身份

一个 Goods 可以同时：

- 被直接消费。
- 作为其他 Process Input。
- 用于 Construction。
- 用于设备运行。
- 被交易。

Raw / Material / Component / Finished 只用于内容组织，不限制 DAG 边。

## 17.4 同一 Product 多路线

机制允许多个 Process 生产同一 Product。

默认只有一条 canonical route。

只有当替代路线能产生真正不同的：

- Resource dependency。
- Facility requirement。
- 成本结构。
- 地域策略。

时才增加。

---

# 18. 首版内容骨架总览

## 18.1 Raw Goods

```text
Ferrous Ore
Conductive Ore
Rare Mineral
Stone
Silica
Coal
Crude Oil
Grain
Produce
Animal Products
Timber
Natural Fiber
```

## 18.2 Industrial Materials

```text
Structural Metal
Conductive Metal
Specialty Material
Construction Material
Glass
Fuel
Chemical Feedstock
Polymer
Lumber
Textile
```

## 18.3 Components

```text
Mechanical Components
Electrical Components
Electronic Components
```

## 18.4 Civilian / Consumable Goods

```text
Staple Food
Prepared Food
Clothing
Consumer Goods
Consumer Electronics
Medical Supplies
```

## 18.5 Construction Goods

```text
Structural Block
Construction Block
Rail
```

## 18.6 Functional Equipment

```text
Industrial Machinery
Storage Equipment
Power Equipment
Control Equipment
Extraction Equipment
Construction Equipment
```

## 18.7 Future Product Families

仅设计占位：

```text
Propulsion Equipment
Train Weapons
Crew Weapons
Train Equipment
Crew Equipment
```

不创建运行时万能 Definition。

---

# 19. 首版范围

资源与生产系统首版设计范围包括：

- `resource_source` 与 `raw_good` 语义。
- ResourceSourceBehavior。
- Durability-based deterministic Extraction。
- 8 类基础 Resource Source。
- 12 种 Raw Goods。
- Primary Production。
- Forward Production DAG。
- ProductionProcessDefinition 与稳定 ID。
- RecoveryProcessDefinition 与独立 Recovery DAG。
- Facility Tags 与 Behavior/Capability 分层。
- Industrial / Extraction / Construction / Storage / Power 等基础 Capability。
- Settlement-wide Inventory、Power Pool、Labor Pool 接口。
- Stack / ItemInstance。
- Facility-local OutputBuffer。
- Production Plan 与 ActiveBatch。
- Production / Recovery / Extraction Runtime。
- Composite Construction Job。
- 耗时 Refit Job。
- Deconstruction Job。
- Space / Storage Reservation。
- Save / Load。
- Runtime Job 与内容热重载语义。
- Resource Renewal Event 的架构方向。

本文冻结的是**结构、语义与不变量**，不是平衡数值。

---

# 20. 明确延后

以下不在本次资源与生产设计中：

## 20.1 经济与市场

- Supply。
- Demand。
- Price curve。
- Treasury。
- Buy / Sell spread。
- Trade fee / tax。
- Settlement MarketState。
- AI economic planning。

现有 `VoxelDefinition.Value` 与 `Price belongs to Market Context` 的既有边界继续保留，但 Price 规则不在本文扩展。

## 20.2 人口模拟

- 人口增长。
- 人口消费率。
- Labor 由人口如何换算。
- 职业。
- 工资。
- 疾病。
- 医疗系统。

本文只要求未来人口系统能够提供 Settlement Labor Capacity。

## 20.3 具体装备 Gameplay

- Propulsion Equipment 具体种类。
- Train Weapon。
- Crew Weapon。
- Train Equipment。
- Crew Equipment。
- 具体伤害、功率、武器、传感、控制、医疗效果。

## 20.4 复杂物流与仓储

- Settlement 内部物流网络。
- Conveyor。
- Truck。
- 专用液体罐。
- 冷藏。
- 危险品。
- 重量容量双限制。

## 20.5 复杂生产

- 多 Production Slot。
- 机器邻接加成。
- 规模经济。
- 复杂效率曲线。
- 连续工艺故障。
- 半成品腐坏。
- Production Batch 主动 Cancel / Refund。
- Construction / Refit / Deconstruction 主动 Cancel。
- 特殊 Process 的连续运行失败规则。

## 20.6 复杂回收与事故

- Scrap / Waste 经济。
- 废旧电子产业。
- 事故散落 Goods。
- Construction accident salvage。
- Facility destroyed 后自动回收。
- Composite 直接 Recovery。

---

# 21. 未冻结参数

以下属于实现、Benchmark、Playtest 和 Balance，而不是架构不变量：

- 每个 Resource Source 的 MaxDurability。
- 各 Source Yield 的 `N Damage → M Goods`。
- Source Cluster 大小和分布。
- Extraction Range / Area。
- WorkPerDamage。
- 每个 Production Recipe 的整数 Input / Output 数量。
- WorkRequired。
- PowerPerWork。
- LaborPerWork。
- Industrial Machinery base capability。
- Extraction Equipment base capability。
- Construction Equipment base capability。
- Storage Equipment capacity。
- FuelEnergyPerUnit。
- Power Equipment output capability。
- ConstructionWorkPerVoxel。
- RefitWorkPerChangedVoxel。
- DeconstructionWorkPerVoxel。
- Recovery BaseOutput 数量。
- Primary Production Site 条件阈值。
- Resource Renewal Event 的触发条件、间隔和规模。
- Facility Priority 默认值。
- Simulation 中各 Resolver 的具体调度参数。

这些参数必须通过实现、Profiling、经济模拟和 Playtest 决定。

---

# 22. 与现有文档的衔接

本文是新增文档，当前不修改已有设计文件。未来统一回写时需要注意以下迁移点。

## 22.1 `02-definitions-and-content.md`

旧语义：

```text
natural_resource tag
→ 当前 Voxel 本身就是自然资源种类
```

新语义：

```text
resource_source tag
→ 世界 Source Voxel

ResourceSourceBehavior
→ Raw Good yields

raw_good tag
→ 标准初级货物
```

应删除或迁移旧 `natural_resource` 语义，不长期保留含糊兼容层。

## 22.2 `05-world-generation-and-settlements.md`

旧“聚居地周边自然资源种类”直接由 `natural_resource` VoxelDefinitionId 派生。

新模型应改为：

```text
nearby Resource Source Voxels
→ ResourceSourceBehavior
→ obtainable Raw Good DefinitionIds
```

Cluster 本身仍然不建立第二份储量事实。

## 22.3 `03-composite-and-behavior.md`

现有 Refit 的 Atomic Transaction 原则继续有效：

- 中间非法结构不暴露给 Simulation。
- 最终结构修改原子提交。
- Voxel Identity 保留。

正式 Gameplay 在此基础上增加：

```text
Timed Refit Job
→ atomic final commit
```

当前测试场景的瞬时 Atomic Refit 可以继续作为开发验证入口，不代表正式 Gameplay 零耗时。

## 22.4 `09-persistence.md`

后续实现资源生产系统时，需要将本文定义的所有长生命周期 Job、Buffer、Reservation 与设备 Runtime State 纳入 Runtime Delta / Save。

仍然遵守现有：

```text
Save → Quit → Load → Continue
```

一致性目标。

## 22.5 Railway

`Rail` 正式属于可生产、库存和交易的 Voxel Good。

Rail Definition 仍只表达轨道种类；直线、曲线、坡道等不成为不同 Goods。

---

# 23. 开放问题

**当前没有未解决的结构性设计问题。**

本次 grilling 的 design frontier 已为空。

后续需要独立设计的系统包括：

1. 市场与价格。
2. 人口与聚居地经济模拟。
3. Settlement AI / Production Planning。
4. 列车动力与 Propulsion Equipment。
5. Train Weapon / Train Equipment。
6. Crew Weapon / Crew Equipment。
7. 具体医疗 Gameplay。
8. 资源探勘与地图信息呈现。
9. 上述系统完成后新增的确有 Gameplay 价值的 Goods / Process。

这些系统应在各自设计时通过现有稳定接口接入，而不重新打开本文已经确定的核心不变量。
"""

path = Path("/mnt/data/10-resources-and-production.md")
path.write_text(content, encoding="utf-8")
print(f"已生成：{path}")
print(f"行数：{len(content.splitlines())}")
print(f"字符数：{len(content)}")
````

> [工具输出]

```
已生成：/mnt/data/10-resources-and-production.md
行数：2777
字符数：39996
```

已完成。没有修改现有 `00–09`、`roadmap.md` 或其他项目文档，而是新增了一份独立设计文档：

**`10-resources-and-production.md`**

文档完整收敛了 Q180–Q428 的讨论，包括：

- Resource Source / Raw Good 与 Durability-based Extraction
- 12 种 Raw Goods 与首版 Resource Source
- Forward Production DAG
- ProductionProcess / RecoveryProcess
- Inventory、Stack / ItemInstance、Storage
- Power / Labor / Work / Facility Capability
- Production Runtime 与 Output Buffer
- Composite Construction / Refit / Deconstruction
- Recovery DAG
- 确定性、Save / Load、Hot Reload
- 首版内容骨架、未冻结参数
- 与现有 `02 / 03 / 05 / 09 / Railway` 文档未来需要衔接的迁移点
- 明确排除市场、人口经济、具体装备玩法等后续系统

[下载 10-resources-and-production.md](sandbox:/mnt/data/10-resources-and-production.md)

---

If you want, I can:

- 继续审计边界问题Q429–Q431
- 展开说明热重载对运行任务的具体影响
- 提供Q421–Q428设计决策总结

---
