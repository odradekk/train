# 重负载场景打开和准备耗时：2026-11-02

这是 2026-10-30 至 2026-11-02 的 Linux runner 历史观测，补充 #824 的重负载缺口；不是改前改后交替对照，也不代表性能达标。规则见[性能测量](../../../App/Measurement/AGENTS.md)，范围与决定见 [#866](https://github.com/odradekk/train/issues/866)。旧基线保留。

## 构建、输入与环境

测量提交固定为 `29a612b30fd242e28445dbc83c469f669b7f4d8d`，ExportRelease。18 次导出均成功，脚本在导出前后检查工作树干净且 HEAD 不变；本次基线和之后合并的产品代码不属于测量身份。内置内容取该提交，`builtin/` 最近内容提交为 `55de26aba3457ae6d6e463672880c4de53ebb0d5`。

输入均为 `examples/<存档>/save.json`，参考与场景使用同一组目标：

| 存档 | 目标 ID：列车／节点／线路 | 存档最近提交 | 输入 SHA256 |
|---|---|---|---|
| crowded-station | 21／2／3 | `a74a2bea7b8a460c62deb306be1a21c306f7b701` | `20ec048fe3f1f01f24da1e5c954a10c8a3cd922972c27bfc2f21e31aa67ca31d` |
| busy-combat | 21／2／3 | `ed4fb73d053f17af40133f21e964888ef89f8935` | `51ede426748a9c0ec456c55a5f0d97ce2b52c65d9c5ec6da935bfc697fceaed3` |
| long-consist | 21／15／23 | `7571536de5fc12ac550db2a3e48afa1113ed426b` | `a16c6515c21acac2740eb43b973f67d1dea140900cd9e18b09c020516ba8db2d` |

runner `lan-linux-nightly`：Arch Linux，Intel Core Ultra 7 258V、8 逻辑处理器，Intel Graphics（LNL）核显，RAM 33,137,618,944 B；交流电、balanced。Godot 4.7.2 official mono，.NET 运行时 10.0.12、SDK 10.0.112，PowerShell 7.6.6；gamescope 3.17.0／X11，Forward+／Vulkan API 1.4.354，GPU 驱动 unknown。窗口与视口均 1920×1080、59.96 Hz，VSync Enabled、maximum_fps=0、MSAA 4×，SDFGI/TAA 关闭。330 份身份与报告均核对一致。

各轮使用新进程及隔离用户目录；脚本声明 Mesa 着色器缓存初始为空，未另测缓存状态，OS 文件缓存不清空。输入录制开启，保留最近 3 个可重放起点；普通场景自动存档 3600 秒，长测 300 秒。长测及 8× 路线关闭遇敌降速；其余保留 SlowToOne。

本文件正式场景的外部采样共 270 个文件、10497 条记录，总 CPU 有效 10227 条、范围 1.101591–100.000000%（含游戏）；每文件首条 CPU 差分为空。MemAvailable 20985585664–26296410112 B。采样的非游戏进程仅 dotnet，按每文件同 PID 的末次减首次累计 CPU 得到合计 0.00 秒，每文件 0.00–0.00 秒。游戏按可执行文件名和唯一 PID 识别。

交流电记录无缺失或断电；各 job 串行，但没有 CPU 预检证据，进程采样也不覆盖整机，不能确认整机空闲。最高温度 **96°C，不能排除降频**。空闲抑制未启用。长测同时用于帧与内存两份基线，外部采样数不能相加。环境来源为每轮 `identity.json`、`report.json`、`external-load.jsonl` 及每 job 的 `ci/environment.txt`、`export/export-summary.json`。

## 打开时间

| 存档 | Actions run | 场景与轮数 |
|---|---|---|
| crowded-station | [41035832048](https://github.com/odradekk/train/actions/runs/41035832048) | scene-node／scene-edge／scene-follow 各 30 个独立进程 |
| busy-combat | [41044422869](https://github.com/odradekk/train/actions/runs/41044422869) | scene-node／scene-edge／scene-follow 各 30 个独立进程 |
| long-consist | [41100635187](https://github.com/odradekk/train/actions/runs/41100635187) | scene-node／scene-edge／scene-follow 各 30 个独立进程 |

每类操作计划 30 次，参考和预演不计入。仅 `sample_valid=true`、时延有值且未受 [#881](https://github.com/odradekk/train/issues/881) 影响的记录入统计。离线逐操作匹配 `probe-state`：在 click—observation_ended 区间、`first_operable` 之前，若中心点命中车辆且 17 个探针只通过 16 个，三类时延都剔除，记 unknown（#881）。没有 `first_operable` 的记录保持未知，另核对操作窗口内的同类探针。nearest-rank 不插值，n=30 的 p50 为第 15 项、p95 为第 29 项。下表单位秒，每格为 **p50 [最小–最大]; p95**，范围上限就是最大值。N/A 表示该操作没有读档按钮起点。

| 存档 | 操作 | 原始 sample_valid 真／假 | 点击到可操作 | 可操作到 Ready | 读档按钮到可操作 |
|---|---|---:|---|---|---|
| crowded-station | 地图首开节点 | 30／0 | 4.770986 [4.505242–5.170614]; 5.135957 | 2.890677 [2.372669–3.208804]; 3.198520 | N/A |
| crowded-station | 节点重开 | 30／0 | 1.615389 [1.538020–1.855788]; 1.737172 | 0.339084 [0.180403–0.456895]; 0.455154 | N/A |
| crowded-station | 节点→线路 | 30／0 | 0.728797 [0.697700–0.842159]; 0.822050 | 0.049997 [0.031895–0.104010]; 0.059317 | N/A |
| crowded-station | 线路→节点 | 30／0 | 1.610366 [1.549853–1.825660]; 1.781814 | 0.291503 [0.151813–0.372971]; 0.363574 | N/A |
| crowded-station | 读档后首显节点 | 30／0 | 1.790911 [1.629753–1.976771]; 1.976144 | 1.359331 [0.618061–1.827675]; 1.708132 | 3.506654 [3.130837–3.839303]; 3.819172 |
| crowded-station | 地图首开线路 | 30／0 | 2.213114 [2.145860–2.336400]; 2.319340 | 0.104180 [0.024780–0.143488]; 0.139851 | N/A |
| crowded-station | 线路重开 | 30／0 | 0.186932 [0.175965–0.193438]; 0.193034 | 0.000000 [0.000000–0.000000]; 0.000000 | N/A |
| crowded-station | 读档后首显线路 | 30／0 | 0.596007 [0.524474–0.692854]; 0.680150 | 0.080176 [0.021841–0.118036]; 0.111315 | 2.333795 [2.210164–2.532908]; 2.525991 |
| crowded-station | 首次跟随 | 30／0 | 5.707664 [5.156512–6.266518]; 6.210614 | 3.372354 [2.797577–4.774761]; 4.673632 | N/A |
| busy-combat | 地图首开节点 | 30／0 | 5.031565 [4.684815–5.304520]; 5.249814 | 3.091762 [2.608963–3.943040]; 3.649626 | N/A |
| busy-combat | 节点重开 | 30／0 | 1.274549 [1.093710–1.600696]; 1.528675 | 1.741209 [1.457602–2.017815]; 1.967555 | N/A |
| busy-combat | 节点→线路 | 30／0 | 0.832023 [0.767883–1.009555]; 0.956695 | 0.037788 [0.000000–0.157157]; 0.138571 | N/A |
| busy-combat | 线路→节点 | 30／0 | 1.135432 [1.029634–1.405096]; 1.286043 | 0.923889 [0.770940–1.232223]; 1.205576 | N/A |
| busy-combat | 读档后首显节点 | 30／0 | 1.532617 [1.330831–1.782530]; 1.766073 | 1.990748 [1.836513–2.227812]; 2.113691 | 3.122710 [2.725217–3.375312]; 3.351698 |
| busy-combat | 地图首开线路 | 30／0 | 2.189841 [2.106191–2.309493]; 2.289888 | 0.088783 [0.028141–0.151135]; 0.148445 | N/A |
| busy-combat | 线路重开 | 30／0 | 0.189439 [0.174618–0.200116]; 0.197879 | 0.000000 [0.000000–0.000000]; 0.000000 | N/A |
| busy-combat | 读档后首显线路 | 30／0 | 0.627507 [0.522538–0.714368]; 0.705281 | 0.068629 [0.022566–0.118561]; 0.102059 | 2.255007 [2.118963–2.510138]; 2.467989 |
| busy-combat | 首次跟随 | 30／0 | 5.652882 [5.209861–6.542481]; 6.364050 | 2.266825 [1.576233–3.692413]; 3.682880 | N/A |
| long-consist | 地图首开节点 | 30／0 | 5.082601 [4.895594–5.657961]; 5.455749 | 1.145397 [0.566132–1.627162]; 1.411085 | N/A |
| long-consist | 节点重开 | 30／0 | 1.396789 [1.313310–1.481745]; 1.479854 | 0.255958 [0.166651–0.358546]; 0.306529 | N/A |
| long-consist | 节点→线路 | 30／0 | unknown（30，#881） | unknown（30，#881） | N/A |
| long-consist | 线路→节点 | 30／0 | 1.997739 [1.822059–2.331704]; 2.230654 | 0.249864 [0.183089–0.334998]; 0.326441 | N/A |
| long-consist | 读档后首显节点 | 30／0 | 1.829208 [1.654369–2.106048]; 2.100499 | 0.293502 [0.151698–0.791126]; 0.654374 | 3.377318 [3.033273–3.690416]; 3.571527 |
| long-consist | 地图首开线路 | 30／0 | unknown（30） | unknown（30） | N/A |
| long-consist | 线路重开 | 30／0 | unknown（30） | unknown（30） | N/A |
| long-consist | 读档后首显线路 | 30／0 | unknown（30） | unknown（30） | unknown（30） |
| long-consist | 首次跟随 | 30／0 | 5.643647 [4.869107–6.527083]; 6.321448 | 0.670227 [0.000000–1.155313]; 0.952211 | N/A |

系统核对 O-C／O-B／O-L 的全部 810 条打开记录，reference 不计入。crowded-station、busy-combat 各 9 类操作均未发现上述探针，每类仍为 30 个有效时延。long-consist 的节点→线路 30 次全部受影响：原始 `first_operable` 比 Ready 晚 1.673–2.840 秒，记下的是列车离开中心后的测量确认时间，不能当产品打开时延。该操作 30 条有数字的时延全部剔除。

long-consist 线路 23 的地图首开、重开、读档后首显各 30 条原已缺时延，操作窗口内也都出现同类探针；这 90 条没有 `first_operable`，不能确定它之前的时间边界，保持既有 unknown，不另加计。读档按钮时延缺失的是同 30 条。连同节点→线路，共 120 条操作因 #881 无可用时延；其余 5 类每类 30 条，未受该判据影响。表中的 sample_valid 只是原始标记，不代替字段可用性和此次剔除；原始文件与判定未改。

Ready 使用必要 64 m L0 的生成、接入、显示回执及非地形对象雾消退，不证明整个模板预取或远景已完成。long-consist 移动后参考过期的长测打开记录不与本表混算，详见[帧基线](2026-11-02-heavy-load-frame-time.md)。

## 每帧场景准备窗口

本次沿用 `29a612b3` 的 `SceneResourceTimings.cs:45`：`View.Interactive` 使下一绘制帧进入“可以操作后”窗口。它早于[设计 01](../../../docs/design/01-voxel-engine-and-scenes.md)普通打开的近处检查与拖动确认 `open.first_operable`，可能将此前的帧算入 4 ms 预算（[#862](https://github.com/odradekk/train/issues/862)、[#881](https://github.com/odradekk/train/issues/881)）。原始 assessment 不重判。

逐帧取 `after_operable=true`、未标记空间切换排除的 Prepare＋PrepareCandidate−计时内 GC 暂停，不含 Commit；每份报告先取最大值。285 份报告的净最大值、原始最大值、GC 最大值、总帧数和排除数均与报告精确匹配。下表净最大值单位 ms，为进程最大值的中位数（最小–最大）；最后一列统计最大帧是否早于所属操作的 `first_operable`。最大帧全部唯一，无并列或操作关联多义。

| 存档 | 场景 | 报告数 | 净最大值 ms | 早于／不早于／未知 |
|---|---|---:|---|---:|
| busy-combat | scene-edge | 30 | 10.743（8.700–18.947） | 23／7／0 |
| busy-combat | scene-follow | 30 | 11.571（9.679–12.883） | 0／30／0 |
| busy-combat | scene-node | 30 | 18.418（12.362–25.571） | 28／2／0 |
| busy-combat | scene-node-first | 3 | 7.651（7.477–7.700） | 0／3／0 |
| crowded-station | scene-edge | 30 | 10.257（8.385–14.440） | 22／8／0 |
| crowded-station | scene-follow | 30 | 18.506（12.765–21.226） | 0／30／0 |
| crowded-station | scene-node | 30 | 14.372（11.499–22.303） | 22／8／0 |
| crowded-station | scene-node-first | 3 | 11.889（11.191–11.903） | 0／3／0 |
| long-consist | scene-edge | 30 | 24.316（16.750–29.108） | 0／0／30 |
| long-consist | scene-follow | 30 | 13.500（10.626–15.728） | 1／29／0 |
| long-consist | scene-node | 30 | 22.094（16.088–25.918） | 30／0／0 |
| long-consist | scene-node-first | 3 | 7.849（7.823–10.111） | 0／3／0 |
| long-consist | scene-route-1x | 3 | 12.499（9.052–14.320） | 0／3／0 |
| long-consist | scene-route-8x | 3 | 12.403（12.001–12.699） | 0／3／0 |

合计早于 126、不早于 129、未知 30。未知全部是 long-consist／scene-edge：最大帧可唯一关联操作，但 `first_operable=null`。普通打开按 phase 和 click—observation_ended 的绝对时间区间关联。路线将 startup.first_operable_at 加 absolute_start_seconds 后再比较；其 startup 仅检查 Interactive 与非 Loading，不含普通打开的近处和拖动确认，不能视为同一产品可操作定义。

scene-map、scene-memory、scene-soak 各存档各 3 份，共 27 份没有此指标，记 N/A。地图及首次节点的 60 秒保持用来观察内存，不改变上述已记录窗口。

## 复算与原始证据

原始数据保存在 **#866 验证归档（工作树 `TestResults/` 的副本）**，`<归档>` 指该副本根目录。`<归档>/issue-866-formal/` 中 C／B／L 分别对应 crowded-station／busy-combat／long-consist；O-* 为 30 轮打开，H-* 为 60 秒保持（H-L 另含路线），S-* 为长测。各 job 的 artifacts 内 `scenarios/summary.json` 索引主进程；reference 不计轮数。

原始 sample_valid 汇总在 `issue-866-formal/opening-summary/`，准备窗口逐报告结果在 `issue-866-formal/preparation-audit/`；#881 离线剔除的逐操作证据、来源行号、交集计数及重算分布在 `issue-866-delivery/r2-opening-audit/`。以下命令扫描三份 O 和全部九轮 S，输出新文件，不修改原始判定：

```powershell
python "<归档>/issue-866-delivery/r2-opening-audit/audit.py" --archive "<归档>" --output "<新核对目录>"
```

每帧准备窗口仍按原始 `first_operable` 比较，保留上文计数；时延因 #881 被剔除不重判该窗口。
