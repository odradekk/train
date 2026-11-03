# 场景打开：2026-11-03

这是 [#888](https://github.com/odradekk/train/issues/888) 将单击选中与进入场景分开后的 Linux 历史基线。15 轮均完成，30 次打开的计时及参考校验有效；性能判定仍有 failed 和 unknown，整体待验收。每个场景只有 **3 轮**，没有交替对照，也未定位耗时瓶颈，不据此判断优化收益。

新流程先选中对象，等选中帧绘制完成，再记录 `click` 并进入场景。旧入口在打开节点和线路时都会构建检查器；本次两者均将检查器构建及其绘制移到计时之前。因此与旧基线在这一项上不可比。可操作判定、近处检查、拖动确认和 Ready 完成点不变；`opening_frames` 仍为完整绘制间隔，探针开销算法不变，但采样窗口随新起点变化。字段逐项对照见 [PR #983](https://github.com/odradekk/train/pull/983)。

同一入口还用于 `scene-memory`、`scene-reference`、`scene-capture` 和窗口长测中的打开；影响不限于本次五类场景。[2026-11-02 重负载基线](2026-11-02-heavy-load-scene-opening.md) 的打开时延也不能与新口径直接比较。本批未重测这些其他场景。

## 构建与输入

| Actions run | 输入 | 场景 | 正式轮数 |
| --- | --- | --- | ---: |
| [41578912924](https://github.com/odradekk/train/actions/runs/41578912924) | `examples/standard-world/save.json` | `scene-node-first,scene-node,scene-edge,scene-follow,scene-route-8x` | 每场景 3 |

测量提交为 `f56daeb2a58f44e308099fea68f85fca7d826136`，构建配置为 ExportRelease。build、import、export、optimizations 均成功；导出成功也表明脚本的前后工作树干净、HEAD 不变检查通过。本批 artifact 未单列 `source_dirty` 字段。基线文档在测量后单独提交，不以文档提交替代构建身份。

输入最近内容提交为 `056da9151b1cc2fe42429d8671a52c08eb59d306`，SHA256 为 `8555a359e2e675a64bebdcb278922a8a2fbdf887af320de393f49b6e8fabf42b`。内置内容以测量提交的 Git 树 `40587b300d886f6c281be9a81a9011684fcfb426` 标识；本批未另列内置内容树 SHA256。

15 份正式身份中的构建、输入及以下 SHA256 均一致：

| 产物 | SHA256 |
| --- | --- |
| 程序集 | `ec2e18870ed7c8eeb302d86dcdfa7afaa4461e85e7ffc6cc20a537221eaf6b70` |
| PCK | `c465fa92a25626163c7bfe3df1a789e3a4cb538487c09c3c8c87cc0b974f4b66` |
| 可执行文件 | `d776f83ae09d82cfa1295fb3d70ad7ab8a77fe66a4cc4f9d42244fae3665d8cc` |

## 机器与运行条件

Arch Linux；Intel Core Ultra 7 258V，8 逻辑处理器；Intel Graphics（LNL）核显；RAM 33,137,618,944 B。正式样本交流电均在线，电源模式 balanced。Godot 4.7.2，.NET 运行时 10.0.12、SDK 10.0.112，PowerShell 7.6.6，gamescope 3.17.0／X11；Forward+／Vulkan API 1.4.354，GPU 驱动版本未知。窗口和视口均为 1920×1080，59.96 Hz，VSync Enabled，maximum_fps=0，MSAA 4×，SDFGI 和 TAA 关闭。

各轮为新进程及隔离用户目录；报告声明 Mesa 着色器缓存初始为空，未独立验证，OS 文件缓存未清空。无预热轮。先生成同构建 `scene-reference` 一轮，供需要参考的操作校验，不计入正式 15 轮。`measure_seconds=0`，节点 Ready 长观察开关使用默认 false；各打开操作 Ready 后观察 5 秒。

录制开启，保留 3 个起点，自动存档间隔 3600 秒。四类打开场景保留遇敌默认 SlowToOne；`scene-route-8x` 按既有规则改为 None，避免自动降速干扰倍率测量。

正式样本的外部负载共 692 条，CPU 有效 677 条，每文件首条差分为空。合并有效记录后按 nearest-rank 取中位数，不平均各轮分位数：

| 记录 | 最小 | 中位数 | 最大 |
| --- | ---: | ---: | ---: |
| 整机 CPU（含游戏），% | 3.019324 | 34.569378 | 99.882767 |
| MemAvailable，B | 21,288,022,016 | 24,049,369,088 | 26,169,966,592 |
| 热区最高温度，°C | 78.05 | 91 | 96 |

采样的非游戏进程只出现 dotnet；逐文件、逐 PID 首末 CPU 增量合计 0.01 秒。采样范围有限，不能确认整机空闲。最高温度 **96°C，不能排除降频**；本批保留这一限制，不代表目标配置实机达标。依据为 `identity.json`、`report.json`、`ci/environment.txt` 和各轮 `external-load.jsonl`。

## 打开时间

共 15 个正式进程，其中 12 个打开场景进程产生 30 条 `open`，每种操作有效 n=3、无效 n=0。全部报告 `outcome=completed`，进程退出码 0，无超时，清理成功；30 条记录的 `sample_valid`、`reference_validation.valid` 均为 true。

逐条核对 `click`、首次可见、首次可操作、Ready 及相对时延非空、有限且公式一致；观察结束均满足请求的 Ready 后 5 秒。`opening_frames`、两段探针开销、近处检查、拖动、缓存及负载起点、进程峰值水位和稳态读数均有记录。Ready 只确认必要范围，不表示全部远景已补齐。

下表单位秒。中位数为三轮排序后的第二项，范围为最小–最大；首次节点的两个场景分开统计，不合并成六轮。

| 场景／操作 | 进入到可操作：中位数（范围） | 可操作到 Ready：中位数（范围） |
| --- | --- | --- |
| `scene-node-first`／map-node | 3.563583（3.526576–3.633458） | 0.317334（0.293372–0.333187） |
| `scene-node`／map-node | 3.600808（3.516974–3.644907） | 0.328410（0.262257–0.378964） |
| `scene-node`／node-reopen | 0.341779（0.331452–0.349192） | 0.289899（0.238805–0.308968） |
| `scene-node`／node-to-edge | 0.820593（0.813737–1.140610） | 0.116746（0–0.120502） |
| `scene-node`／edge-to-node | 0.224396（0.189743–0.251950） | 0.392179（0.247080–0.475952） |
| `scene-node`／reload-node | 0.505842（0.486058–0.511075） | 0.336109（0.237168–0.424738） |
| `scene-edge`／map-edge | 3.619359（3.617096–3.709757） | 0.063872（0.055800–0.072413） |
| `scene-edge`／edge-reopen | 0.251138（0.242633–0.259040） | 0（0–0） |
| `scene-edge`／reload-edge | 0.690568（0.649139–0.760966） | 0.153522（0.069973–0.169532） |
| `scene-follow`／map-follow | 5.318525（4.376586–5.360738） | 0.120395（0.093485–0.158539） |

读档按钮到可操作仍包括读档、选中、绘制等待及进入：reload-node 为 2.051681（2.034804–2.056858）秒；reload-edge 为 2.007665（1.975799–2.068815）秒，均 n=3。

以下仅统计各场景首次打开，单位毫秒，均为中位数（范围）。帧 p95 是每轮 `opening_frames.p95` 的跨轮汇总，**不是打开时延 p95**。探针列取 `contribution_upper_bound_seconds`，是累计开销上界，不从时延中扣减。

| 场景 | 打开帧间隔 p95 | 进入至可操作探针上界 | 可操作至 Ready 探针上界 |
| --- | --- | --- | --- |
| `scene-node-first` | 1493.954（1175.251–1502.644） | 50.765（50.093–55.349） | 10.162（8.030–11.204） |
| `scene-node` | 1505.205（1136.863–1538.423） | 52.216（43.109–53.418） | 11.210（8.134–15.550） |
| `scene-edge` | 400.207（76.648–414.510） | 94.262（86.965–102.153） | 2.892（2.698–2.958） |
| `scene-follow` | 4179.200（589.808–4190.130） | 69.996（54.139–74.158） | 2.382（1.864–3.249） |

## 跟随入口和路线

`scene-follow` 三轮均打开 Train 1 的 map-follow，出现跟随姿态、`followed_picked=true`、铁路近处检查 2/2、拖动后的 yaw 变化及有效参考校验。业务探针数为 0，不据此声称验证了其他业务对象。

测量身份及同 SHA 调用链确认：`SceneBudget` 和 `FollowRoute` 先走选中入口并等 `FramePostDraw`，随后走 `RaiseObjectActivated`；该事件绑定到 `GameUi.OpenScene`，列车目标经 `FollowedVehicle.Of` 进入跟随场景。实际跟随呈现和拾取证据与此一致；trace 没有单独记录事件分发。

`scene-route-8x` 不写 `open`，使用独立的 route 记录。三轮均 `route_completed=true`、`preview_seconds=0`，经过节点 15 → 线路 23 → 节点 21 → 线路 23 → 节点 15；采样帧为 3635／3704／3701，判定末帧为 3629／3700／3696，其后保留 6／4／5 帧。没有以短预览代替完整路线。

| 路线指标 | 中位数 | 范围 |
| --- | ---: | --- |
| 整段采样，秒 | 73.276084 | 72.653165–73.591329 |
| 实际倍速（整段含打开，请求 8×） | 2.864964 | 2.852238–2.888610 |
| 首次可操作后帧 p95，毫秒 | 27.775 | 27.426–28.752 |
| 首次可操作后帧 p99，毫秒 | 34.391 | 33.973–37.200 |
| 首次可操作后最大帧，毫秒 | 210.573 | 206.473–217.784 |

帧窗口按报告 `assessment` 排除启动首次可操作之前的帧，包括之后的空间切换；实际倍速使用整段 timing。三轮路线完成不代表覆盖通过，`route-analysis.json` 仍有缺失和 unknown：

| 轮次 | displayed 中心缺失帧 | sampled 车身缺失帧 | 车身 unknown 帧（Scenes／displayed） |
| --- | ---: | ---: | --- |
| 1 | 9 | 1852 | 140／149 |
| 2 | 9 | 1932 | 107／116 |
| 3 | 6 | 1924 | 56／63 |

还存在历史不足、缺少范围、未来轨迹不足或未观察等窗口；32 m／64 m 的 displayed unknown 帧分别为 92／104、61／71、57／69。末尾补采样不能让全部未来窗口完整，不能将这些覆盖缺口报为通过。

## 性能判定与限制

逐轮 `assessment.conclusion` 均为待验收。以下次数来自原报告，不以汇总中位数替代逐轮判定：

| 项目 | 结果 |
| --- | --- |
| 打开时延 p95 | 30 项 unknown：每报告每种有效打开仅一次，不足要求的 30 次；跨轮每种也只有 3 次 |
| 打开最大值 | 13 项 failed：12 次首次打开均超过 1 秒，另 node 第 2 轮 node-to-edge 为 1.140610 秒 |
| 每帧场景准备最大值 | 15 项 failed：每轮均超过 4 ms |
| 路线帧 p95／p99／最大值及实际倍速 | 各 3 项 failed |
| 节点私有字节差量、渲染差量、离开后的存活堆差量 | 各 6 项 unknown：缺少进入前地图稳态或强制 GC 配对 |
| Linux 工作集 | 15 项 recorded，仅记录，未按 Windows 3 GiB 阈值判通过 |

路线 `assessment` 没有 unknown 项，但其覆盖分析仍存在上述 unknown 和观察受限窗口。没有重测其他存档、规模点、长测或完整混合负载；未做瓶颈剖析。这里只保存新计时定义下的三轮记录，性能失败、覆盖缺口、采样不足和高温限制均保留。

## 原始数据与复算

在 [run 41578912924](https://github.com/odradekk/train/actions/runs/41578912924) 下载 artifact `linux-measure-41578912924-1`。以下路径均相对 artifact，不含机器路径：

- `measure-20261103T043446386-f56daeb/export/export-summary.json`：构建身份和四步结果。
- `measure-20261103T043446386-f56daeb/scenarios/summary.json`：各进程结果。
- `measure-20261103T043446386-f56daeb/scenarios/<场景-轮次>/`：`report.json`、`identity.json`、`process.json`、`scene-operations.jsonl`、`scene-frames.jsonl`、`external-load.jsonl`；路线另有 `route-analysis.json`、`route-frames.jsonl` 和 `process.log`。
- `measure-20261103T043446386-f56daeb/reference/`：独立参考，未混入正式统计。
- `ci/environment.txt`：运行时版本。主机及账号信息不在本基线中记录。

```powershell
gh run download 41578912924 --name linux-measure-41578912924-1 --dir <artifact>
python scripts/measure/summarize.py <artifact>/measure-20261103T043446386-f56daeb/scenarios --output <summary.json>
```

打开时延取汇总的 `scenarios.<场景>.openings.A.by_operation.<操作>`；范围下限取原始有效 `open` 的最小值，上限为汇总 maximum。帧与探针附表从各轮首次 `open` 取值后排序；路线帧指标取 `assessment.items`，避免混入原始 timing 的启动大帧。所有表均先在各轮取得对应读数，再跨轮取中位数和最小–最大，不拼接不同窗口计算分位数。
