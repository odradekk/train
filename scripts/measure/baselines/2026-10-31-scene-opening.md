# 场景打开：2026-10-31

这是对象内立方体网格合并后的 Linux 历史基线，用于观察长期变化，整体性能仍待验收。只记录本次实际测量的首次节点打开和线路打开；其他操作继续参照 [2026-10-28 基线](2026-10-28-scene-opening.md)。本次为改后单侧运行，与 #860 的跨批比较不等同于交替对照。规则见 [性能测量](../../../App/Measurement/AGENTS.md)，完整结果见 [#861 job 1](https://github.com/odradekk/train/issues/861#issuecomment-6535398511) 和 [job 2](https://github.com/odradekk/train/issues/861#issuecomment-6536750002)。

## 构建与输入

| 批次 | Actions run | 输入与场景 | 正式轮数 |
| --- | --- | --- | ---: |
| job 1 | [41138104947](https://github.com/odradekk/train/actions/runs/41138104947) | scene-scale 的 1×、10×，`scene-node-first` | 各 30 |
| job 2 | [41146894002](https://github.com/odradekk/train/actions/runs/41146894002) | standard-world，`scene-node-first,scene-edge` | 各 30 |

两批均测量提交 `bf3e89b0fe0aaa71cf7425a16957b8d3bb16edce`，生成输入的代码也是此提交。ExportRelease 导出的 build、import、export、optimizations 均成功，导出脚本前后检查工作树干净且 HEAD 不变；规模 manifest 的 `source_dirty=false`。本文件在测量之后单独提交，不以文档提交代替测量身份。

标准输入为 `examples/standard-world/save.json`，最近内容提交 `056da9151b1cc2fe42429d8671a52c08eb59d306`，SHA256 为 `8555a359e2e675a64bebdcb278922a8a2fbdf887af320de393f49b6e8fabf42b`。源内置内容树 SHA256 为 `4a59aa45204643b137a2f282e25132dc2dbee096ccfd62237608d6f2b24910f7`。

规模输入以 `examples/starting-trade/recipe.json` 为底，最近内容提交 `ad09a7e177672847d31bb13d87b3c93e5ae68ea3`。源配方 SHA256 为 `8f91491057f10f0ee5e6508a802eb2252bcfa240e7698914117409ce73ab13f0`；生成脚本改写输出位置和检查点后的配方 SHA256 为 `a1d375670241df7cb4d2cbde15857cb6cce0595437a7df99738ebe4ddea7c9ad`。

| 规模 | 输入 SHA256 | 输出内容树 SHA256 |
| --- | --- | --- |
| 1× | `277d2f587fac8330452171f71651bcc00cbbfb7e6297d865368e6e472a94c157` | `4a59aa45204643b137a2f282e25132dc2dbee096ccfd62237608d6f2b24910f7` |
| 10× | `eb83427718f50dcc30735a60f6b1decd69eb0c081cb7c2ac7e19a4a07f72d82c` | `464e1c02d782109d9fc5d2fd2f4a542e5ac60abb5cd5df11a9ec40f49a904b7e` |

规模 manifest 的近点内容一致检查通过。半径 192 cells 外建筑投影体素数分别为 150,470 和 1,545,302，实际倍率 10.2698；两档各自去重后的 32³ 分块数为 254／2,582，六邻接暴露面数为 360,438／3,857,166。程序地形的对应数量未知。30×、100× 未做本次游戏打开测量。

## 机器与运行条件

Arch Linux；Intel Core Ultra 7 258V，8 逻辑处理器；Intel Graphics（LNL）核显；RAM 33,137,618,944 B（30.862 GiB）。交流电、电源模式 balanced。Godot 4.7.2，.NET 运行时 10.0.12、SDK 10.0.112；gamescope 3.17.0／X11，Forward+／Vulkan API 1.4.354，GPU 驱动版本 unknown。真实窗口及视口均为 1920×1080，刷新率 59.96 Hz，VSync Enabled，maximum_fps=0。

各样本为新进程及隔离用户目录；报告和脚本声明 Mesa 着色器缓存初始为空，未另外实测缓存状态，OS 文件缓存未清空。录制开启、保留 3 个起点，自动存档间隔 3600 秒，遇敌为默认 SlowToOne。每批先生成自己的参考；参考不计入正式轮数，无预热轮。`measure_seconds=0`、`measure_node_ready_hold=false`，Ready 后保持 5 秒。

只统计正式场景的外部负载，不含参考。CPU 包含游戏，每个文件首条 CPU 差分为空：

| 批次 | 外部记录／CPU 有效样本 | CPU 最小／p50／最大 % | MemAvailable 最小／p50／最大 B | 采样非游戏 CPU 增量 |
| --- | --- | --- | --- | --- |
| job 1 | 1456／1396 | 1.692866／52.340936／96.746988 | 20012666880／22775152640／25848602624 | dotnet 0.01 秒 |
| job 2 | 1726／1666 | 1.577670／35.054022／98.104265 | 22582878208／24055992320／25916317696 | dotnet 0.00 秒 |

两批串行，采样的非游戏进程列表只出现 dotnet，覆盖不全，不能确认整机空闲。两批最高温度均为 **96°C，不能排除降频**。保留这些负载限制，不能据此声称目标配置实机达标。环境依据为 `identity.json`、`report.json`、`ci/environment.txt` 与逐秒 `external-load.jsonl`。

## 打开时间

共 120 个独立正式进程、180 个有效操作；下表每行有效 n=30、无效 n=0，退出及清理成功、probe 有效，输入和同构建内容身份已核对。nearest-rank 不插值：p50 为排序第 15 项，p95 为第 29 项；不平均各轮分位数。范围为最小–最大，单位秒。

点击到可以操作；`map-node`、`map-edge` 分别为地图首次打开节点、线路，`edge-reopen` 为线路重开，`reload-edge` 为读档后首显线路。`reload-edge` 在本表从线路打开动作起算，从读档按钮起算的总等待另列于下文：

| 输入／操作 | p50 | 范围 | p95 | 最大 |
| --- | ---: | --- | ---: | ---: |
| 1×／map-node | 5.102584 | 4.863896–5.475504 | 5.401228 | 5.475504 |
| 10×／map-node | 5.360908 | 4.906051–6.138990 | 5.781085 | 6.138990 |
| standard-world／map-node | 4.376177 | 4.204252–4.784792 | 4.775445 | 4.784792 |
| standard-world／map-edge | 4.029311 | 3.796716–4.281569 | 4.225561 | 4.281569 |
| standard-world／edge-reopen | 0.340761 | 0.316966–0.420901 | 0.390923 | 0.420901 |
| standard-world／reload-edge | 0.916736 | 0.844695–1.071576 | 1.046716 | 1.071576 |

可以操作到实际显示 Ready。可以操作使用近点 32 m 和实际拖动检查；Ready 使用必要 64 m L0 的生成、接入及显示回执，并要求非地形对象的雾消退。它不表示整个预取范围或远景 L1／L2 已补齐。Ready 早于可以操作时差值记 0。

| 输入／操作 | p50 | 范围 | p95 | 最大 |
| --- | ---: | --- | ---: | ---: |
| 1×／map-node | 0.555589 | 0.488116–0.673055 | 0.671274 | 0.673055 |
| 10×／map-node | 1.937223 | 0.836192–4.131483 | 3.946213 | 4.131483 |
| standard-world／map-node | 0.245987 | 0.196541–0.383546 | 0.335936 | 0.383546 |
| standard-world／map-edge | 0.074855 | 0.058326–0.200132 | 0.178756 | 0.200132 |
| standard-world／edge-reopen | 0.000000 | 0.000000–0.000000 | 0.000000 | 0.000000 |
| standard-world／reload-edge | 0.170806 | 0.052388–0.224413 | 0.219724 | 0.224413 |

`reload-edge` 从读档按钮到可以操作（包括读档等待）也是 n=30：p50 **2.591995**、范围 **2.410571–2.783252**、p95 **2.685259**、最大 **2.783252** 秒。

10× Ready p95 低于本次 5 秒目标；标准世界四种操作最大均小于 #813 的约 0.49 秒。相对 #860，reload-edge p95 增加 22.048 ms、最大增加 19.485 ms；旧首次节点样本只有 3 个，不能把它当成同样精度的 30 轮对照。

## 范围与复算

本文件只更新上述六组打开操作。每帧准备、实际帧间隔、节点内存及 60 秒保持的独立结果见 [最终交付所在 Issue](https://github.com/odradekk/train/issues/861)。10× 打开短窗实际帧间隔 p95／p99 比 #860 长约 3–4 ms；GC 暂停事件次数、最大单次暂停时长、L1／L2 全部补齐时间变化均未知。开发机换页时的数据仅用于定位，没有进入本基线。

下载上述 Actions artifact，在仓库根目录用现有汇总入口复算；job 1 的场景目录在 `scale-1x/` 和 `scale-10x/` 下，job 2 直接在 `scenarios/`：

```powershell
python scripts/measure/summarize.py <正式场景目录> --output <新汇总.json>
```

读取 `scenarios.*.openings.A.by_operation` 的 `click_to_operable_seconds`、`operable_to_ready_seconds`、`load_button_to_operable_seconds`；范围下限取同批 `scene-operations.jsonl` 中 `kind=open`、`sample_valid=true` 的对应字段最小值，上限为汇总 `maximum`。参考、其他场景、旧批次和 60 秒内存批次均不混入。
