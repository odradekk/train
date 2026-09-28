# Playground 跨进程对照 — Issue #127

实际命令：pwsh -NoProfile -File scripts/run-godot-playground-cross-process.ps1 -OutputDirectory .godot/validation/issue-127/tested-b44069c；结果：退出 0。原始配置、检查点、存档与 stdout/stderr 保留在该工作树的 .godot/validation/issue-127/tested-b44069c/，本文件中的相对证据名以该目录为根。

受测提交：b44069ceffbdfa3d7436b930eff36fc4035683bd（工作树变更：False）；配置 straight-rail-train v1；正式序列 cross-process-comparison，保存于检查点 8、边界 0:8，待执行输入 4 条。

构建：2D12F34F3B089DFC4215BABF114C9283261F3EB6DC9A48F30483C81F490013A7；语义内容摘要：EC6EEC3F2727079079C2515B25F2A43A7A818C6BF4130B06C61213A091546E90；环境：Microsoft Windows 10.0.26200 / X64，.NET 10.0.112，Godot 4.7.2.stable.mono.official.ed1daf0bf，PowerShell 7.6.6。原始配置见 config.json，独立进程 PID/退出码见 processes.log。

实际输入：1 制造桥接车辆；2 损伤桥接部件并断裂；3 启动车头；4 放置探针体素；5–8 在暂停边界依次排入两次移除、放置与损伤（目标步 3）；保存并退出；9 推进至步 3，第二次移除拒绝而后续输入执行；10 毁掉探针体素；11 推进至步 300；12 损伤轨道；13 推进至步 2300 并检查 ForwardGap 故障停车。每个正式输入的完整规范载荷、结果及边界见 continuous.stdout.log 和证据 JSON。

| 证据层 | 本次结果 | 原始证据 |
| --- | --- | --- |
| 领域 | 正式序列 13 步操作状态/断言通过；暂停期 4 条待执行输入、失效拒绝后继续、ID/断裂、速度及断轨停车进入权威检查点。完整领域单测结果另随交付记录。 | continuous.stdout.log、evidence/00–13-evidence.json |
| 真实 Godot | 每步检查正式世界显示计数、轨道实体、发布碰撞命中、探针体素增删的显示/碰撞生命周期和车辆视觉；观察过程执行面板/查询、自由/聚焦/跟随及目标消失，并改变帧批次。 | continuous.stdout.log、observe.stdout.log |
| 跨进程 | 连续、保存退出、另进程恢复、观察四路在同一输入和边界逐检查点相等；恢复默认暂停。字段注入在检查点 9 报 next_entity_id 与输入位置并退出非零；坏档恢复入口也退出非零并报 base/configuration_version，当前会话保持。 | processes.log、save.stdout.log、resume.stdout.log、field-difference.stdout.log、failure.stdout.log、bad-restore.stderr.log |
| 内容适配 | 独立 Godot 进程修改已存语义修订标识后普通读档成功，报告 semantic_revision 适配并暂停；不参与同输入逐点摘要比较。 | adaptation.stdout.log、adaptation-user-data |
| 人工 | 待用户在图形窗口检查镜头切换、跟随连续性、控制反馈、失败诊断和保存切换保护。Headless 断言不视作人工观感通过。 | 下列步骤 |

取消、保存失败、缺失候选、坏档均保持当前会话；内容适配与同输入比较分列。上述适配只改变存档依赖记录的修订标识，不覆盖未来真实内容规格变化的全矩阵。

## 父票 #109 验收映射

| #109 AC | 子票 | 本次新增证据与边界 |
| --- | --- | --- |
| 1 固定配置/普通存档默认暂停 | #121、#125、#127 | continuous 与 resume 检查入口暂停；保存后状态在检查点 8 对照。 |
| 2 观察模式及目标生命周期 | #124、#127 | observe 的模式、目标失效与相机位置断言；人工连续性待验证。 |
| 3 模拟控制/观察隔离 | #121、#124、#127 | 检查点 0–13 与 observe 同输入比较；帧批次变化。 |
| 4 切换保护与失败保留 | #125、#127 | failure 检查取消、写入失败、缺失候选、坏档。 |
| 5 配置/正式序列分离 | #121–#123、#127 | cross-process-comparison 引用 straight-rail-train，四个独立进程段复用。 |
| 6 输入全序及失效继续 | #122、#123、#127 | 检查点 8/9 的边界、四条待执行输入和拒绝后执行。 |
| 7 规范编码 | #122、#126 | 复用 AuthorityCodec 与既有黄金向量；本次未重做编码矩阵。 |
| 8 实体 ID | #122、#126、#127 | 每点 next_entity_id 跨进程对照；既有失败分配/溢出单测继续保留。 |
| 9 无状态随机 | #126 | 断裂后的权威状态参与对照；算法黄金向量沿用 #126。 |
| 10 权威摘要与首差异 | #126、#127 | 14 个检查点的构建/内容/输入/边界比较；字段注入定位检查点 9。 |
| 11 分层报告 | #127 | 本报告四栏，内容适配单列；人工明确待验证。 |

重跑：pwsh -NoProfile -File scripts/run-godot-playground-cross-process.ps1 -OutputDirectory <新的空路径>。人工：运行 dotnet build Train.csproj 与 Godot 图形入口；选择 Playground 的 straight-rail-train，依次检查自由相机、选择、聚焦、车辆跟随、运行/暂停/单步/N 步、目标销毁提示；修改后分别尝试取消切换、保存后切换、保存失败及坏档读取，核对世界、镜头与反馈。使用新存档，勿覆盖本目录的用户数据。

边界：保留四个旧真实入口；#118 迁移/退役未验收；无有界录制、隔离重放、ZIP 或性能结论。
