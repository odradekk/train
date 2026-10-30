# 每帧场景准备

四个打开场景（`scene-node-first`、`scene-node`、`scene-edge`、`scene-follow`）和 `scene-route-*` 的报告包含 `scene_preparation_maximum`。capture/reference 场景不作性能判定。

目标场景第一次整帧提交到显示且 `View.Interactive` 为真后，从下一绘制帧开始统计。每次 `Prepare` 和 `PrepareCandidate` 在计时起止各读一次 `GC.GetTotalPauseDuration()`，以差值记录计时内的 GC 暂停。每帧累计这些调用的主线程墙钟耗时和暂停差值，先相减，再取最大值；排除 `Commit`。按[用户决定](https://github.com/odradekk/train/issues/859#issuecomment-6498312019)，该值对照[设计 00](../../docs/design/00-product-and-core-loop.md#本阶段性能目标)的 ≤4 ms 判定，GC 引起的超时归 #864。`excess` 给出扣除后超出预算的秒数，通过时为 0，未知时为 `null`。生产准备预算仍用含 GC 的墙钟。

暂停量采用 .NET 的进程累计计数差，不是 EventPipe 从请求挂起到完成恢复的整个区间；不另行扣除恢复尾部，也不把两者当作精确相同的暂停量。GC 读数放在墙钟起止内，避免扣入结束快照之后发生的暂停。

首次提交帧属于打开耗时。之后每次切换空间，只排除新空间正式显示的首个绘制帧；前后的补齐和候选资源准备照常计入，不重新开始整个统计窗口。本项起点使用宿主的 `View.Interactive` 状态标记；原有打开时延还会验证附近对象的实际交互与镜头拖动响应，因此起点更晚。现有打开时延和路线帧耗时指标保持原定义。

原始 `scene-resource-frames.jsonl` 保留含 GC 的 `preparation_seconds`、`candidate_preparation_seconds` 及其和 `preparation_total_seconds`，记录两类调用累计的 `preparation_gc_pause_seconds`。`preparation_calls`、`candidate_preparation_calls` 记录该绘制帧内的两类调用次数，含没有推进资源的空调用；耗时仍按同帧所有调用之和统计。报告的 `resource_work.phases` 同时保留总调用次数，以及原始准备总耗时、候选耗时分别最大的整条帧记录，供核对一次长调用与多次累加。

`after_operable` 标明各帧是否参与统计，`excluded_space_change` 标明是否因新空间首帧而排除；排除帧的 `after_operable` 必须为 `false`。报告中 `scene_preparation_maximum.measured` 为扣除后的最大值，`raw_maximum`、`gc_pause_maximum` 分别保留原始最大值和最大暂停量；三者可能来自不同帧，不能用两个最大值相减。`excluded_frames` 给出排除帧数，`identity.scene_budget.resource_work.frame_count` 核对原始记录数。缺文件、缺标记、标记冲突、缺暂停数据、耗时或暂停量无效、无有效窗口帧、记录数不符或截断均为 `unknown`，此时 `excluded_frames` 为 `null`。

离线汇总的 `scene_preparation` 保留扣除后的各轮最大值 `run_maxima` 和总最大值 `maximum`；`raw_run_maxima`／`raw_maximum`、`gc_pause_run_maxima`／`gc_pause_maximum` 保留原始耗时和暂停量。汇总采用报告的逐轮判定；单轮超限不能被跨轮中位数掩盖，有未知轮次不能判通过。已知超限与未知轮次并存时保留超限结论，同时列出未知轮次。

缺少暂停数据的旧旁车和报告按本定义为 `unknown`，场景准备数字不可直接比较；打开、补齐和路线帧耗时的定义不变。

## 当前结果与已知差距

`5205be78` 的正式结果仍未满足 ≤4 ms，判定保持 `failed`。[用户决定](https://github.com/odradekk/train/issues/859#issuecomment-6515542064)接受当前优化，将验收标准 2 记为未达标的已知差距，暂不另开工单；4 ms 目标不变。

数字来自第二轮正式 [A：40907420381](https://github.com/odradekk/train/actions/runs/40907420381)（四场景各 30 轮）和 [B：40920558598](https://github.com/odradekk/train/actions/runs/40920558598)（完整路线 3 轮）。超时比例的分母仅含本项有效统计帧，分子为扣除计时内 GC 后仍超过 4 ms 的帧；各最大值可能来自不同帧。

|场景|超时帧 / 统计帧|比例|扣 GC 最大 ms|原始最大 ms|GC 暂停最大 ms|
|---|---:|---:|---:|---:|---:|
|scene-node-first|731 / 6231|11.73%|12.2528|12.2528|8.168|
|scene-node|4255 / 41203|10.33%|19.6007|229.8894|229.464|
|scene-edge|654 / 48783|1.34%|8.4692|14.9377|13.594|
|scene-follow|859 / 6039|14.22%|15.8423|20.4122|14.523|
|scene-route-1x（三轮合计）|1183 / 47154|2.51%|12.3961|322.2609|320.371|

A 合计 6499 / 102256 帧超时（6.36%），无新空间首帧排除；B 排除 12 个新空间正式首帧。含 GC 的原始尖峰不能直接当作本项未达标的幅度。

剩余差距包括以下三类情况；等待的具体来源和雾循环的 JIT 归因尚未证实。[runner 诊断及证据边界](https://github.com/odradekk/train/issues/859#issuecomment-6511540974)提供了逐项记录，诊断数据不替代正式对照：

- 网格接入中的主线程等待。诊断里一次 AddSurface 墙钟为 9.274 ms、主线程 CPU 仅 0.591 ms；等待发生在不可中断的引擎调用内部。尚未区分调度、引擎同步或驱动等待，避开主线程创建需先调整线程约束。
- 最后获准项越过剩余额度。准入按最近实测成本预测，项开始后不可抢占；实际成本高于预测就可能超出。提前停止准入会让准备跨更多帧，影响补齐速度。
- 雾循环中两条诊断记录之间的 CPU 耗时。三轮日志均在调用第 998 个循环项的结束记录之后、下一次准入记录之前出现约 4.8–5.2 ms 的 CPU 区间；疑似 JIT 栈上替换（OSR），尚未用 JIT 事件或调用栈证实，不能将其算成单次雾移除的成本。
