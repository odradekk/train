---
name: train-code-review
description: Review a change to train against this repository's rules and the failures it has had before. Use when reviewing a pull request, a branch, commits made straight to master, or uncommitted changes in train.
---

# 审查 train 的改动

由 `setup-code-review` 技能生成，也用它刷新。可以直接修改，但每条检查都要保留出处。

## 谁来审

改动由没参与编写的人审。本项目的 pair 里，reviewer 不写代码，可以直接审 worker 的改动；作者自己审时（例如协调者自己的改动），能开子代理就开一个新的、用最强模型的子代理来审，自己只负责协调。

1. 找出改动：PR 比较 head 和 `master` 的合并基点；分支比较它和 `master` 的合并基点；直接提交到 `master` 的，取用户指定的提交，或尚未进入 `origin/master` 的提交，再加未提交的改动。
2. 子代理只拿到：改动范围、本技能的路径、改动对应的工单和决策笔记 ID，以及 PR 描述（作为待核对的说法）。写改动时的对话和推理不交给它。改动跨多个区域时，每组区域一个审查者，再加一个只对照工单验收条件的审查者。
3. 原样转交发现。核对事实性说法（例如某行是否存在）；不同意某个判断时，把理由写在旁边，不删掉那条发现。

没有子代理可用时自己审，并在报告里写明审查是在作者的上下文里做的。

## 流程

1. 列出改动的路径，先读下表里管这些路径的文档，再读 diff。
2. 读 diff，以及足够理解设计的周边代码。
3. 对改动的路径运行「需要审查者运行的检查」。
4. 依次应用阻断要求、改动路径相关的建议、流程检查。
5. 写报告。

## 规则来源

| 路径 | 规则在 |
|---|---|
| `Core/**` | [AGENTS.md](../../../AGENTS.md)「Source layout」；[设计文档](../../../docs/design/README.md) 00–06、09–19；[Core 契约](../../../docs/contracts/core.md)；决策笔记 `2026-09-05-scalar-doubles-and-stateless-random`、`2026-10-05-voxel-free-core-and-presentation-scenes`、`2026-09-04-world-model-as-single-source-of-truth`、`2026-10-09-content-project-and-split-by-reader` |
| `Content/**`、`builtin/**` | [Content 契约](../../../docs/contracts/content.md)；[内容标准](../../../docs/content/README.md)；设计 02；`.agents/notes/accepted/` 里内容相关的笔记（数据包、身份、定义、行为类型、实例设计、形状、包依赖、嵌套、资产字节） |
| `Scenes/**` | [Scenes 契约](../../../docs/contracts/scenes.md)；设计 01、07 |
| `App/**` | [宿主设计](../../../docs/godot-host-design.md)；[Godot 契约](../../../docs/contracts/godot.md)、[事务](../../../docs/contracts/transactions.md)、[编辑](../../../docs/contracts/editing.md)；[Godot 资产](../../../docs/godot-assets.md)；[控制台手册](../../../docs/console/README.md) |
| `Rendering/**` | 美术 02、03、05；设计 07；决策笔记 `2026-10-09-render-with-forward-plus` |
| `Ui/**` | 设计 08；美术 07 |
| `Contracts/**`、`prototypes/**` | [契约说明](../../../docs/contracts/README.md)、[约定](../../../docs/contracts/conventions.md)、`docs/contracts/public-api.txt` |
| `Tests/**` | [Testing standard](../../../docs/implement-sop.md#testing-standard)；[测试入口](../../../docs/run-tests.md)；[契约测试](../../../docs/contracts/testing.md) |
| `Tools/Cli/**`、`examples/**` | AGENTS.md「Source layout」；[示例存档](../../../examples/README.md) |
| `scripts/**`、`.github/**` | [测试入口](../../../docs/run-tests.md)；[CI](../../../docs/ci.md)；[本地环境](../../../docs/local-environment.md) |
| `addons/**` | [Godot MCP](../../../docs/godot-mcp.md)；Implementation SOP「Godot MCP validation」 |
| `art-source/**` | 美术 08 |
| `docs/**` | AGENTS.md「Agent workflow」的 Prose 行；[设计 README](../../../docs/design/README.md)；[术语表](../../../CONTEXT.md) |
| `archive/**` | AGENTS.md：只读 |

目标性文档的用法：设计 00 的性能目标、事务矩阵（transactions.md）、编辑契约、美术 07 的符号、Demo 内容目录还没完全实现。已实现的部分按规则审；未实现的部分只约束改动新写的代码，已知差距不算发现。文档里已经过时的行（路径、符号、数字和代码不符）不作为规则。

## 自动运行的检查

每个发往 `master` 的 PR 都会跑 Windows routine（`.github/workflows/windows-checks.yml`）：文件大小检查；构建 `Train.sln`；按改动选中的 MSTest fast 和 integration 层；改到契约相关文件时的 `structure`、`verify`；改到项目文件时的 `isolate`；改到内容时的 Schema 检查。这些检查能抓到的问题不写进报告。引擎层也会跑，但不阻断，结果要审查者自己看（见下一节）。`master` 上的 Linux 全量检查在合并后才跑，不算在内。

## 需要审查者运行的检查

用 `pre-push-checks` 技能按改动路径选择，运行后把命令和结果写进报告：

- AGENTS.md「Agent workflow」表格里对应改动的最小检查。
- 改了 `App/**`、`Tools/Cli/**` 或任何工厂调用时，运行 `structure`：CI 只在契约相关文件改动时跑它，而「工厂只在组装根调用」由它检查。
- 改了 Godot 相关代码时，看 PR 上引擎层的结果，或运行 `pwsh -NoProfile -File scripts/test.ps1 -Layer engine`。
- 改了 `.agents/notes/` 或引用笔记的代码时，运行 `python <decision-notes 技能目录>/scripts/verify_notes.py .agents/notes --base <合并基点>`，没有钩子或 CI 跑它。
- 改了内容、配方或存档格式时，用 CLI `check` 检查受影响的示例存档。

## 阻断要求

1. **模块边界。** Core、Content、Scenes、Contracts 不出现 Godot 类型；依赖方向是渲染 → 体素 → 核心；实现只依赖契约项目；工厂只在 `App/Composition/` 和 `Tools/Cli/Composition.cs` 调用；Core 不读坐标、形状和文本。出处：AGENTS.md「Source layout」；笔记 `2026-09-04-world-model-as-single-source-of-truth`、`2026-10-05-voxel-free-core-and-presentation-scenes`、`2026-10-10-separate-module-contracts`、`2026-10-09-content-project-and-split-by-reader`。
2. **Core 确定性。** 不用 `float`、`System.Random`、时间和计时器，不依赖哈希或容器顺序；排序用 ordinal；存读前后顺序稳定。出处：笔记 `2026-09-05-scalar-doubles-and-stateless-random`；[#307](https://github.com/odradekk/train/issues/307#issuecomment-6133455000)、[#550](https://github.com/odradekk/train/issues/550#issuecomment-6208079024)；提交 f6f91f24。
3. **契约受控变更。** 任何契约改动（包括签名不变的行为改动）先经用户确认；同一个 PR 同步改声明、文档、测试、调用样例和替身；`public-api.txt` 只用 `snapshot` 更新；不留兼容垫片、`NotImplementedException` 工厂或第二套接口。出处：[约定](../../../docs/contracts/conventions.md)「契约基线与受控变更」；[#543](https://github.com/odradekk/train/issues/543#issuecomment-6211350224)。
4. **存读完整性。** 新增或改变的 Core 状态有对应的校验规则和「保存后读取」往返测试；内容或格式变了，用 CLI `make` 重制受影响的示例存档，不手改生成的数据。出处：[示例存档](../../../examples/README.md)「重制与长验证」；提交 4d395f12、af37ad02；[#391](https://github.com/odradekk/train/issues/391#issuecomment-6246465237)。
5. **场景流式切换。** 晋升、取消、重新绑定和 Dispose 时，正在进行的构建、网格打包、资源和预算都有明确去处；有在构建中途触发切换的测试。出处：提交 909ecab3、d6ce2f03、4b9bcddc、075833f3、8f757964。
6. **Godot 对象生命周期。** 传给 Godot 的对象用 `using var v = Variant.From(x)`；从 Godot 数组读出的临时对象要释放；`GD.Load` 不放进静态初始化；RefCounted 只在主线程释放；Node 用 `Free()`；宿主关闭后不再调用 Godot。出处：提交 928b491e、fcfc1e60、6f2b8890；[#687](https://github.com/odradekk/train/issues/687#issuecomment-6310065444)、[#776](https://github.com/odradekk/train/issues/776#issuecomment-6391985813)。
7. **主线程和热路径。** 主线程不 `.Wait()`、不取 `.Result`；Present、`_Process` 和每个模拟步的工作量随变化量增长，不随世界大小增长；不在热路径新增大块分配；缓存不以浮动原点为键。出处：[宿主设计](../../../docs/godot-host-design.md)「协调线程与帧驱动」；提交 1f84482e；[#383](https://github.com/odradekk/train/issues/383#issuecomment-6189325680)、[#741](https://github.com/odradekk/train/issues/741#issuecomment-6386916209)。
8. **缓存键完整。** 缓存的计算读到的每个输入都进入键；有「只改这个输入就失效」的测试。出处：提交 d0433b76、9d2163b1、7f04d975。
9. **错误语义。** 预期内的失败返回带文档代码的 Failure；`Faulted` 只给内部错误；前置条件不满足抛 `ArgumentException`；不伪装成功，不静默跳过输入，缺内容要有诊断。出处：[#312](https://github.com/odradekk/train/issues/312#issuecomment-6134202686)、[#325](https://github.com/odradekk/train/issues/325#issuecomment-6173526465)。
10. **玩家看到的文字。** 不出现枚举名、失败代码、异常消息、定义 ID、哈希或原始记录；新失败代码有中文说明；界面文本用 `ReplaceLineEndings("\n")`，不用 `AppendLine`。出处：提交 158ddd6a、2b328b2f、6f55563b；[#743](https://github.com/odradekk/train/issues/743#issuecomment-6372528323)。
11. **面板状态。** 改了面板时，确认会话重新绑定、内容世代变化和修订刷新后不留旧状态；世代只在真正变化时前进；玩家未提交的输入在刷新后保留。出处：提交 b0a4bc02、c58731cd、9f193865、7ab0fa56。
12. **测试能发现缺陷。** 期望值独立算出，不来自被测代码；没有永真的断言；测试名声称的边界真的被执行；修 bug 附回归测试；新断言至少见过一次失败。出处：[Testing standard](../../../docs/implement-sop.md#testing-standard)；[#380](https://github.com/odradekk/train/issues/380#issuecomment-6183380473)、[#722](https://github.com/odradekk/train/issues/722#issuecomment-6364040923)；提交 44a0d08e。
13. **测试不照抄实现。** 不复制内容数值、映射表、布局常量或文案；同一断言不在多层重复；不比契约更严。出处：[#517](https://github.com/odradekk/train/issues/517#issuecomment-6196509755)、[#574](https://github.com/odradekk/train/issues/574#issuecomment-6285379092)。
14. **异步和时序测试。** 用进入和放行信号控制顺序，不靠 sleep；超时只防挂起并报「挂起」；等待要容忍取消，并覆盖 `Pump` 可能启动的所有任务；被阻塞的构建在 `finally` 里放行；清理不掩盖原失败。出处：[#317](https://github.com/odradekk/train/issues/317#issuecomment-6137110770)、[#741](https://github.com/odradekk/train/issues/741#issuecomment-6388660390)；提交 c7eb3ab4；#807。
15. **测试分层和时限。** fast 层不做真实文件读写和子进程，单个用例 ≤ 1 秒；新时限按多 pair 常态负载下实测的最坏值留余量；改时限时 `scripts/test.ps1`、工作流和 `docs/ci.md` 一起改；删改测试时更新最少用例数，新测试项目要登记。出处：[CI](../../../docs/ci.md)；[测试入口](../../../docs/run-tests.md)「执行与边界」；提交 463965e1、cc31dc21；[#640](https://github.com/odradekk/train/issues/640#issuecomment-6262565321)。
16. **不留无用代码。** 没有调用方的成员、只给测试或测量用的产品成员、走不到的分支、可推导的存档状态、多余的校验都删掉。出处：AGENTS.md「Code taste and design principles」；[#383](https://github.com/odradekk/train/issues/383#issuecomment-6189325680)、[#391](https://github.com/odradekk/train/issues/391#issuecomment-6245025040)。
17. **复用已有实现。** 同一个公式、查询、路径规则或查找表只定义一处，改动应调用已有的。出处：AGENTS.md「Code taste and design principles」；[#726](https://github.com/odradekk/train/issues/726#issuecomment-6364320314)；提交 56cea83a、82c3837a。
18. **信息隐藏。** 实现类型默认 `internal`，只公开工厂；不为共享代码把 `private` 放宽；别的模块的不透明数据（如存档负载）原样传递。出处：AGENTS.md「Code taste and design principles」；[契约说明](../../../docs/contracts/README.md)；[#380](https://github.com/odradekk/train/issues/380#issuecomment-6183380473)。
19. **不超出范围。** 改动不超出工单和批准的计划；不顺手删除已有路径，不改契约或测试规则。出处：[#506](https://github.com/odradekk/train/issues/506#issuecomment-6186845383)、[#337](https://github.com/odradekk/train/issues/337#issuecomment-6238032962)。
20. **文档随行为更新。** 同一个 PR 里更新受影响的契约、设计、测试说明、SOP 和控制台页面（含索引和帮助列表）；文档写当前状态，不写改动历史。出处：AGENTS.md「Console command documentation」；[#407](https://github.com/odradekk/train/issues/407#issuecomment-6138751352)、[#583](https://github.com/odradekk/train/issues/583#issuecomment-6240224970)。
21. **性能结论可信。** 用 Release 构建；改前改后用同一工具；正式对照在 Linux runner 上做；采样本身的开销不计入；在合并后的 head 上复测；数字对应它要改善的指标；缺数据或被截断时报「未知」，不报 0 或通过。出处：笔记 `2026-10-26-post-demo-performance-and-memory-gaps`；[#738](https://github.com/odradekk/train/issues/738#issuecomment-6370632313)、[#774](https://github.com/odradekk/train/issues/774#issuecomment-6391263104)；提交 54318be4。
22. **验收证据能证明行为。** 截图和真实运行要显示改动后的行为；没跑的检查写明未验证；Godot 改动看保存的场景和资源 diff，不只看工具返回的成功消息。出处：[Review SOP](../../../docs/review-sop.md)「3. Review the delivery」；[#652](https://github.com/odradekk/train/issues/652#issuecomment-6274127038)、[#689](https://github.com/odradekk/train/issues/689#issuecomment-6309402895)。
23. **脚本不依赖当前目录。** 子进程显式指定工作目录；不依赖调用目录或输出目录结构；平台相关代码同时覆盖 Windows 和 Linux。出处：提交 5e76f9dd、86a43c1b、abc90b82。
24. **Godot MCP 副作用。** 撤掉 `run_project` 顺带保存的无关场景和 `.uid` 改动；不改 `addons/godot_mcp/`。出处：Implementation SOP「Godot MCP validation」。
25. **内容数据规则。** 一个定义一个 `<id>.<kind>.json` 文件；未知字段、版本或行为类型让整个包失效；新定义类型配文档、schema、`SCHEMA_BY_SUFFIX` 并升 `schema_version`；生成的内容用 `generate_small_world.py` 改。出处：笔记 `2026-10-09-package-dependencies-and-missing-placeholders`、`2026-10-09-code-behavior-types-and-sealed-composites`；[内容标准](../../../docs/content/README.md)。
26. **不改 `archive/`。** 出处：AGENTS.md「Source layout」。

## 建议

报告为建议，不阻断合并。

- **坐标系**（`Scenes/**`、`Ui/WorldCamera*`）：函数之间传递的位置写明所在坐标系；测试包含旋转的宿主和原点平移。出处：提交 c8b46c36、b8fa2a9e、9897634b。
- **测试替身**（`Tests/**`）：替身遵守它替代的契约；共享替身放在 `Tests/ContractSupport` 并有自测；夹具不构造读档校验会拒绝的世界。出处：[#547](https://github.com/odradekk/train/issues/547#issuecomment-6208789567)、[#317](https://github.com/odradekk/train/issues/317#issuecomment-6136357217)。
- **不过度设计**（全部代码）：不用反射、代码生成或通用框架；单一实现不另立接口，模块契约、宿主端口和测试替身除外；不引入设计 README「设计指导思想」列出的反模式。出处：AGENTS.md「Code taste and design principles」；[设计 README](../../../docs/design/README.md)。
- **注释**（全部代码）：不复述代码，不写 XML 文档注释，不写 Issue 号或改动历史；只写代码表达不了的约束。出处：AGENTS.md「Code taste and design principles」；PR #401（994775e6）；[#654](https://github.com/odradekk/train/issues/654#issuecomment-6272809547)。
- **命名和术语**（全部代码和文档）：命名空间 = 项目名 + 目录路径；用 `CONTEXT.md` 的术语，避开它列出的词。出处：AGENTS.md「Source layout」「Domain glossary」。
- **Godot 界面和导出**（`Ui/**`、`export_presets.cfg`）：节点名不当身份用；缩短列表前先清焦点；开发专用的 `res://` 文件在两个导出预设里都排除。出处：提交 9009c9c8、64cab962、e1ef41bd。
- **渲染和美术**（`Rendering/**`、`builtin/**`、`art-source/**`）：Forward+、关闭 SDFGI、颜色贴图用 `source_color`；内置 PNG 和 GLB 用 `importer="keep"`；资产命名、尺寸和材质类别按美术 08。出处：笔记 `2026-10-09-render-with-forward-plus`、`2026-10-10-raw-asset-bytes-by-revision`；美术 02、08。

## 流程检查

- 改动满足工单的验收条件；PR 描述用 `Refs #N`，不用关闭关键字，分支上的提交不带工单号。出处：AGENTS.md「Agent workflow」的 Tracker 行。
- 改动不编辑 `proposed/` 以外的决策笔记，`verify_notes.py --base` 通过；和已接受笔记冲突的地方报给规划，不改笔记。出处：Decision notes 行。
- 引用了决策笔记（`Decision note: <ID>`）的代码，改动后仍符合该笔记，或取代它的笔记。出处：`decision-notes` 技能。
- 新增或修改的文字按 `plain-language` 技能写。出处：Prose 行。

## 报告

每条发现写明：来自哪条检查、缺陷、位置、影响和证据。阻断的排在建议前面，最后列出运行过的检查和结果。报告发在工单的 Issue 评论里，按 [Review SOP](../../../docs/review-sop.md) 的格式，每轮一条。收到别人的审查时，先用 `verify-bug` 技能核实各条发现，成立的修掉，其余给出证据。

规则来源核对到提交 `35039350`（2026-10-26）。
