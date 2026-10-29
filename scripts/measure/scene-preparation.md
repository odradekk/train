# 每帧场景准备

四个打开场景（`scene-node-first`、`scene-node`、`scene-edge`、`scene-follow`）和 `scene-route-*` 的报告包含 `scene_preparation_maximum`。capture/reference 场景不作性能判定。

目标场景第一次整帧提交到显示且 `View.Interactive` 为真后，从下一绘制帧开始统计。每帧分别求所有 `Prepare` 和 `PrepareCandidate` 的主线程墙钟耗时之和，再取最大值，包含 GC，排除 `Commit`。最大值按[设计 00](../../docs/design/00-product-and-core-loop.md#本阶段性能目标)的 ≤4 ms 判定，`excess` 给出超出预算的秒数，通过时为 0，未知时为 `null`。

首次提交帧属于打开耗时。之后每次切换空间，只排除新空间正式显示的首个绘制帧；前后的补齐和候选资源准备照常计入，不重新开始整个统计窗口。该起点早于原有打开时延的判定；后者还要求附近的场景对象可交互，且镜头拖动已响应。现有打开时延和路线帧耗时指标保持原定义。

原始 `scene-resource-frames.jsonl` 的 `after_operable` 标明各帧是否参与统计，`excluded_space_change` 标明是否因新空间首帧而排除；排除帧的 `after_operable` 必须为 `false`。报告中 `scene_preparation_maximum` 的 `excluded_frames` 给出这类排除帧数，`identity.scene_budget.resource_work.frame_count` 用来核对原始记录数。缺文件、缺任一标记、标记冲突、无有效窗口帧、记录数不符或截断均为 `unknown`，此时 `excluded_frames` 为 `null`；旧报告没有这些证据时也不推算为通过。

离线汇总的 `scene_preparation` 保留各轮最大值 `run_maxima`、总最大值和有效／未知轮数，采用报告的逐轮判定；单轮超限不能被跨轮中位数掩盖，有未知轮次不能判通过。已知超限与未知轮次并存时，汇总保留超限结论，同时列出未知轮次。
