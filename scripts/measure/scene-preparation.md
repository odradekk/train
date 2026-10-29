# 每帧场景准备

四个打开场景（`scene-node-first`、`scene-node`、`scene-edge`、`scene-follow`）和 `scene-route-*` 的报告包含 `scene_preparation_maximum`。capture/reference 场景不作性能判定。

目标场景第一次整帧提交到显示且 `View.Interactive` 为真后，从下一绘制帧开始统计。每次 `Prepare` 和 `PrepareCandidate` 在计时起止各读一次 `GC.GetTotalPauseDuration()`，以差值记录计时内的 GC 暂停。每帧累计这些调用的主线程墙钟耗时和暂停差值，先相减，再取最大值；排除 `Commit`。按[用户决定](https://github.com/odradekk/train/issues/859#issuecomment-6498312019)，该值对照[设计 00](../../docs/design/00-product-and-core-loop.md#本阶段性能目标)的 ≤4 ms 判定，GC 引起的超时归 #864。`excess` 给出扣除后超出预算的秒数，通过时为 0，未知时为 `null`。生产准备预算仍用含 GC 的墙钟，不改变准备行为。

暂停量采用 .NET 的进程累计计数差，不是 EventPipe 从请求挂起到完成恢复的整个包络；不另行扣除恢复尾部，也不把两者当作精确相同的暂停量。GC 读数放在墙钟起止内，避免扣入结束快照之后发生的暂停。

首次提交帧属于打开耗时。之后每次切换空间，只排除新空间正式显示的首个绘制帧；前后的补齐和候选资源准备照常计入，不重新开始整个统计窗口。该起点早于原有打开时延的判定；后者还要求附近的场景对象可交互，且镜头拖动已响应。现有打开时延和路线帧耗时指标保持原定义。

原始 `scene-resource-frames.jsonl` 保留含 GC 的 `preparation_seconds`、`candidate_preparation_seconds` 及其和 `preparation_total_seconds`，新增两类调用累计的 `preparation_gc_pause_seconds`。`preparation_calls`、`candidate_preparation_calls` 记录该绘制帧内的两类调用次数，含没有推进资源的空调用；耗时仍按同帧所有调用之和统计。报告的 `resource_work.phases` 同时保留总调用次数，以及原始准备总耗时、候选耗时分别最大的整条帧记录，供核对一次长调用与多次累加。

`after_operable` 标明各帧是否参与统计，`excluded_space_change` 标明是否因新空间首帧而排除；排除帧的 `after_operable` 必须为 `false`。报告中 `scene_preparation_maximum.measured` 为扣除后的最大值，`raw_maximum`、`gc_pause_maximum` 分别保留原始最大值和最大暂停量；三者可能来自不同帧，不能用两个最大值相减。`excluded_frames` 给出排除帧数，`identity.scene_budget.resource_work.frame_count` 核对原始记录数。缺文件、缺标记、标记冲突、缺暂停数据、耗时或暂停量无效、无有效窗口帧、记录数不符或截断均为 `unknown`，此时 `excluded_frames` 为 `null`。

离线汇总的 `scene_preparation` 保留扣除后的各轮最大值 `run_maxima` 和总最大值 `maximum`；`raw_run_maxima`／`raw_maximum`、`gc_pause_run_maxima`／`gc_pause_maximum` 保留原始耗时和暂停量。汇总采用报告的逐轮判定；单轮超限不能被跨轮中位数掩盖，有未知轮次不能判通过。已知超限与未知轮次并存时保留超限结论，同时列出未知轮次。

这项定义从包含 GC 改为扣除计时内的 GC 暂停，新旧场景准备数字不可直接比较。旧旁车和报告缺少暂停数据，按新定义为 `unknown`。`baselines/` 的三份基线均没有场景准备数字，此次不更新；打开、补齐和路线帧耗时的定义不变。

`FramePreparationStarted` 在复用匹配完成后发出，可能比冻结候选晚几帧。因此 `missing_parts`／`rebuilding_parts`、`PreviewCounts`、覆盖采样分类和 `admitted_to_resources_prepared` 等流式诊断不能和 #813 直接比较。打开、补齐和帧耗时仍沿用原定义。

#813 的 228.8 ms 从打开判定的 `first_operable` 起算，按 phase 拆分，且不排除新空间首帧；本项从首次完整提交且 `Interactive` 为真之后起算，排除新空间首帧，两者不能直接比较。
