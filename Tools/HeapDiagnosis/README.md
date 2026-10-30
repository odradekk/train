# 临时 Linux 堆诊断

本工具只用于 #873 第一阶段取证，不属于产品或正式性能测量。目标框架为 `net10.0`，ClrMD 固定为 `4.2.751201`。

`scripts/measure/issue-873-heap.py` 负责启动进程、采集、分析和清理。Linux 控制器从目标进程已加载的运行时找到匹配的 DAC（运行时诊断库），传给下面的命令；不能拿另一个运行时的库代替。

固定版本 ClrMD 在 Linux Heap 转储中识别出的运行时版本，可能与同一 SDK 包内的 coreclr 和 DAC 文件版本不同。已用配套文件版本一致的布尔探针复现：默认 `CreateRuntime(dac)` 在加载 DAC 前报版本不匹配。Linux 分析因此只跳过这项版本预检查，仍加载控制器从目标 coreclr 旁取得的配套 DAC，并由严格预检验证完整分析。Windows 保留版本检查；不改用 Full 转储。

```text
dotnet Train.HeapDiagnosis.dll preflight <ready-marker> <release-marker>
dotnet Train.HeapDiagnosis.dll analyze-preflight <dump> <matching-dac> <output-directory>
dotnet Train.HeapDiagnosis.dll analyze-one <dump> <matching-dac> <output-directory>
```

`preflight` 原子发布包含 PID 数字的 ready 文件，主线程等待 release 文件，后台线程同时存活；120 秒后自行退出。控制器发现 ready 后采集转储，再原子发布 release，最后清理进程。fixture 保留三个 RecordingService：pending 为空、相同对局标识且 Task 已完成、不同对局标识且 Task 未完成。另外保留两个 ContentView、四个至少 1 MiB 的数组，以及每个视图各一条包含 10,000 个节点的 LinkedList。

`analyze-preflight` 严格检查三个派生状态的集合、视图和数组数量及根路径、20,000 个链表节点，以及对象分类的总数和总大小。任何问题都判失败，不使用耗时断言。成功写出 `preflight.json`，只证明诊断链可用。

`analyze-one` 每次进程只读一份转储，写出输出目录下的 `heap.json`。报告包含类型数量和大小、强根可达／无根／Free 统计、工厂和 ContentView 统计、ContentView 根链、大于等于 1 MiB 的 byte／char 数组根链及分类。控制器分别调用两次，每次各设 360 秒上限，再按 `Types` 字典计算类型差。一份失败仍继续另一份；报告有问题计数时，差分标记为 partial。

控制器在第二次 idle 门控后结束测量进程组和游戏，再分析已完整采集的转储；长测退出码只作记录。结束测量前，先把匹配的 DAC 复制到本任务临时目录，避免测量入口清理导出目录后找不到它。

强根遍历包含 ClrMD 的普通根、线程静态根及 dependent handle（条件依赖引用）。BFS 首次发现对象时，从父对象传下分类和最近的 ContentView，每个对象只归类、计数一次，不反复回溯父链。分类优先级是数组池、线程本地存储（TLS）、内容、根种类。共享对象归入第一次发现它的根路径，分组依赖枚举顺序，不代表独占保留大小。

只有宿主 `_content` 直接指向的视图标为 current，其他视图标为 non-current；后者可能是历史版本，不能单凭标签判定为上一局。`ContentViewFirstPathGroups` 为每个视图累计最近归属于它的对象数和总大小，包含视图自身。路径上嵌套多个视图时，只计入最近的视图；无根视图的计数和大小为零。这些数值不能证明移除某条引用就能回收这些对象。

只有报告视图和大数组时才回溯根路径，每条最多保留末端 128 步。`PathTruncated` 为 true 表示省略了靠近根的前段，此时显示的首项不一定是根。无根表示未发现枚举强根路径，不等于已经回收；Free 不计为无根对象。Free 大小也不能单独证明某次分配会因碎片失败。

只对 strong-root-reachable 的 RecordingService 读取 `_pending`。非空时读取 `Snapshot.Binding.Session` 和宿主 `_active.Binding.Session`，在内存中比较 Guid；从 `Work.m_stateFlags` 的 Task 完成位派生完成状态。输出 PendingIsNull、SnapshotBindingEqualsCurrent 和 WorkIsCompleted。pending 为空时，后两个状态是 null；字段读取失败时，对应状态也为 null，不伪造 false。宿主字段读取失败不阻止独立读取 pending 或 Task 状态。

游戏转储的堆校验问题、坏对象、缺失目标、坏引用和字段异常按问题类别及类型累计到 `Issues`，继续统计能读到的部分。若枚举器异常终止，该次枚举剩余内容不可恢复；此前数据和其他枚举仍保留。因此总数只涵盖成功读取的对象，出现问题时可达性和类型数都可能不完整，不能当作完整堆结论。preflight 对任何 `Issues` 保持严格失败。

报告不写对象地址、字符串内容、原始字段值、文件路径或环境变量。问题计数只包含固定类别、类型名和堆校验问题种类。致命异常输出异常类型、HResult，以及 `new StackTrace(error)` 中的声明类型和方法名；不输出 Message、文件名或行号。ClrMD `4.2.751201` 的 `ClrDiagnosticsException` 没有 Kind 属性，因此明确输出 `kind: null`、`kind_available: false`。控制器把这些字段保存到 `acquisition.json`。严格预检保留原始异常栈；游戏堆中的可恢复读取问题仍只计入 `Issues`。参数错误只写固定文字。Linux 的诊断通道、DAC 加载及字段读取须由同一次 GitHub Actions 任务的 preflight 验证，本机编译通过不能代替这项验证。

控制器的转储门控位于每轮 `reset-map-idle` GC 之后。原 `reset-map` GC 后先读录制状态，再异步等待录制和存档空闲，最多等待 60 秒；超时也保留状态、等待时长和后续 idle GC。第二次 idle 门控之后结束测量，不等待游戏正常退出。

第一份游戏转储失败或超时时，控制器记录失败，向第一轮写入 `continue`，跳过后续转储，继续等待第二轮 idle 门控。到达门控只能证明已走完该边界；`gc_data_succeeded` 还要求输出包含同一游戏 PID 的两轮原 GC 和 idle GC。任何采集失败均保留 `captures_succeeded: false`，不能算完整采集成功。已经完整采集的转储仍各自分析；不分析失败采集留下的部分文件。

准备与采集共用从控制器启动计时的 40 分钟硬截止，包含准备、两段负载、两次最多 60 秒空闲等待，以及两份各最多 300 秒游戏 Heap 采集。两份分析各自最多 360 秒，共 12 分钟，主体预算合计 52 分钟。另留 2 分钟收尾，workflow 步骤上限 54 分钟、job 上限 60 分钟，为 checkout、SDK 准备、删除、扫描和上传留出余量。硬截止保证预算内停止等待，不能保证负载变慢时仍采齐第二轮；未到达门控或记录不齐均明确报告失败或 partial。

每次采集从开始起每 5 秒记录 UTC、经过秒数和 `dump_bytes`（文件不存在为 0），结束时补末次读数。失败或超时先读取采集器的 createdump 后代 PID/状态字母和游戏线程状态计数，分别标明 game 与 collector 祖先，再终止采集器。失败后通过 `TRAIN_HEAP_DIAGNOSIS` 验证游戏归属；再次核对直接父 PID 和进程名，只向该游戏直接拉起的 createdump 发 SIGKILL，最多等待 2 秒，然后向游戏发 SIGCONT，最后写 `continue`。`acquisition.json` 的 sample.collection_diagnostics 包含停止前状态、SIGKILL 发送结果、仍未退出的 createdump 和 SIGCONT 结果，sample.released 记录放行；`*-telemetry.json` 同时保存这些读数与脱敏后的 dotnet-dump 输出。原始日志只留在私有 scratch，scratch 路径替换为 `<scratch>`，其他绝对路径和当前身份替换为占位符，涉及环境或 sudo 的行不发布。最终发布仍必须通过 `publish-measurement.ps1` 的扫描，不上传原始日志或转储。

`heap-observations.jsonl`、`collections.jsonl`、`recording.jsonl` 与 `timeline.md` 一同输出。recording 保留本任务全部 PID 的有效行，包括 scene-reference，供比较录制耗时；仅时间线过滤游戏 PID。JSONL 坏行单独记入 source_errors，不覆盖原采集失败，GC 数据标记不完整。时间线只关联门控记录的同一游戏 PID，原 collections schema 保持不变，按 cycle、phase、at 与 observation.collection_at 精确配对，并核对来源文件修改时间属于本次运行；以 collection.at × 同一 observation 的 Stopwatch frequency 对齐录制 timestamp，比较原 GC、GC 后状态读数、idle 等待和 idle GC。录制操作按时间关联，不能把操作 id 当作 cycle。表中保留原 GC 到 idle GC 的边界差和 live 数据，并列出 started、capture-completed、write-completed、validate-completed、work-completed、cleared 事件。GC 后读到的 Task 状态不代表 GC 当时的状态；缺失时钟或记录保留 unknown，不用 scene-reference 或另一进程补齐。

## 三轮无转储观测

无转储观测使用 `--no-dumps`；它与 `--preflight-only` 互斥。该模式不构建堆分析器、不安装 dotnet-dump、不预检、不采集或分析转储，也不执行内存或磁盘容量门槛（等待同步点时也不检查剩余内存）。仍通过原 `run-measurement.ps1` 跑 scene-reference → scene-soak，Repeat 为 1、DurationSeconds 为 9000。控制器只设置原有的 `TRAIN_HEAP_DIAGNOSIS`，不新增游戏开关。

三次 idle 同步点到达后均先写 `continue`；第三次写完后停止自有测量进程和游戏。成功必须有同一游戏 PID 的三轮共六条原 GC／idle GC 与对应状态，且来源配对完整、无坏行。仅到达三个同步点不算数据成功。`acquisition.json` 写出 `no_dumps`、`expected_cycles`、`reached_cycles` 和 `gc_data_succeeded`；不适用的 `captures_succeeded` 为 null，`analyses` 为空，不伪造转储成功。

无转储模式从控制器启动起最多等待 50 分钟，之后不再分析，另留 4 分钟供控制器停止进程和收集文本；控制器步骤上限 54 分钟。publish 扫描在该步骤之后，job 上限 60 分钟，余下 6 分钟同时容纳前后准备、清理、扫描、日志回显和上传。原转储模式保留两轮、40 分钟采集硬截止、每份最多 300 秒采集与各 360 秒分析的预算。硬截止限制等待时间，不能保证采齐；缺失、坏行或提前退出明确失败。

两种模式在等待 ready 时持续读取私有 `heap-observations.jsonl`，只消费以换行结束的完整行。每次原 GC、idle GC、同步点、失败和最终摘要都立即向 stdout 写一行并 flush。stdout 只含固定 phase、cycle、相对秒数、live 字节、三个录制状态、失败类型或成功布尔值，不回显原日志、异常 Message、路径或身份。同步点状态取自同 cycle 最近的 idle 读数。即使尚未到第一个 idle 同步点，也从自有 observation 保留原 GC 和游戏 PID；写到一半的尾行不会在等待期间误报坏行，最终仍未写完则记为不完整来源。

时间线按 `expected_cycles` 展示各轮事件，并汇总六次 GC live、原与 idle 各自相对首轮的增量，以及每轮从原 GC 到 idle GC 的下降量。recording 原输出保留本任务各 PID，时间线仅使用长测游戏 PID。控制器拆成具体职责：`issue_873_processes.py` 管自有进程生命周期，`issue_873_progress.py` 管完整行和安全进度，`issue_873_no_dumps.py` 管三轮无转储流程，`issue_873_diagnostics.py` 管来源、时间线及原堆诊断辅助；主入口保留转储模式。

## EventPipe GC 图

当前 workflow 使用 `--gcdump`：标准存档的三次读档仍各保留原 GC 和 idle GC，仅在第 1、3 次 idle 门控各采一份，第 2 次直接放行。首份失败或超时后跳过后续采集，继续收齐三轮数据；已完整采到的一份仍可单独分析。失败的部分文件不分析。原 `--no-dumps` 和 ClrMD 模式保留，本轮不改产品，也不把 idle 读数替换为正式指标。

工具固定为 `dotnet-gcdump 10.0.750501`，安装到本任务 scratch/tools。分析器仅在本临时项目内固定 TraceEvent `3.1.23` 和 NETCore.Client `0.2.750501`；GCHeapDump/MemoryGraph 由同版本 dotnet-gcdump 包公开，因此 PackageDownload 也固定该包，引用 tools/net8.0/any/dotnet-gcdump.dll，不进入游戏程序集。

```text
python scripts/measure/issue-873-heap.py --gcdump-preflight-only --scratch <outside-checkout> --output <anonymous-output>
python scripts/measure/issue-873-heap.py --gcdump --scratch <job-scratch> --output <anonymous-output> --cache <measurement-cache>
dotnet Train.HeapDiagnosis.dll analyze-gc-one <gcdump> <idle-live-bytes> <output>
dotnet Train.HeapDiagnosis.dll analyze-gc-pair <before.gcdump> <after.gcdump> <before-live> <after-live> <output>
```

Linux 正式运行先执行同 job 的小进程预检：两份图分别严格核对已知视图、大数组、链节点及路径，再验证双图分析；失败不启动游戏。预检另写 ready.live，PID ready 格式不变。开发机的小进程预检不启动游戏。所有原始图和原始工具日志仅放工作树外的 scratch；Linux scratch 必须在 RUNNER_TEMP。控制器 finally 删除所有 gcdump，workflow always 删除整个私有目录，只上传扫描后的 JSON/JSONL/Markdown。

单图写 gc-heap.json，含类型数量/大小、根类别、ContentView 路径、完整性和与传入 idle live 的字节差。双图串行读取，释放第一份图后读第二份，写 snapshot-1、snapshot-3 和 gc-difference.json；差分按字节增量降序取 30 项，附第二份图中该类型从根出发的第一条路径。根类别按 BFS 第一条根路径互斥归属，不能当独占保留大小；路径最多保留末端 128 步，截断明确标记。根类别另记直接目标数量和字节。图中的类型大小 bucket 按该版本工具的六种固定后缀归并回类型名。来源模块、地址、原始根标签、CollectionLog 和字符串内容不输出。

图不含普通实例字段元数据，字段名为 null。只有强根可达的 HostCoordinator 恰有一个、它直接引用的 ContentView 目标也恰有一个时，才根据已审源码中唯一直接 IContentView 字段 _content 推断 current；报告标为 current_inferred_from_unique_host_direct_edge，路径恰经过该边时可标 _content/source-verified-inference。其他视图为 non-current，不代表旧局；条件不满足则 current/non-current 为 null、全部列 unknown，不按视图顺序猜测。

完整性比较来自两个相近时点：idle GC 的 live 和随后 gcdump 额外 Gen2 GC 的对象图，不声称是同一次 GC。图若采样，类型数量、大小和差分明确为未缩放的原始采样图统计；另列 graph.TotalSize × AverageSizeMultiplier 的堆大小估计，不能把估计写成完整对象统计。倍率语义来自固定工具源码 GCHeapDump.cs。真实地址的 undefined 节点、无效边、损坏/未转储区域和合成零地址节点分开记录。

固定工具在 .NET 10 下会警告不支持非丢失缓冲、回退到有损模式。能力警告与实际丢事件计数分开；工具没有报告实际计数时为 unknown/null，成功退出也不证明完整。报告保留采样倍率、对象字节总量、与 idle live 的差和可得的损坏信息，不凭空设“差多少就完整”的阈值。真实游戏规模的合理性由开发机窗口验证，再申请 runner。

准备、Linux 小预检、等待三轮和两次各最多 180 秒采集共用从控制器启动起 44 分钟截止；结束自有测量进程和游戏后分析最多 360 秒，另留 2 分钟收尾，控制器不超过 52 分钟。workflow 步骤 54 分钟、job 60 分钟。每个门控在 finally 写 continue；采集和分析失败分别记录，GC 文本不因此丢失。扫描后日志回显差分和两份图的摘要、根类别及 ContentView 路径，完整类型表保留在 artifact，上传失败重试一次。

分析器先单独 `dotnet restore`，仅遇到 `NU1301` 时重试一次，再以 `--no-restore` 构建。工具程序集的 HintPath 显式加路径分隔符，不依赖 `NuGetPackageRoot` 末尾带斜线。子进程失败时保留最多 20 条带错误代码的错误行，脱敏后同时写入 `acquisition.json` 的 `build_errors` 和 failure 进度记录；重试前的失败单独保存在 `restore_failures`。编译错误和超时不重试。

Windows 真实游戏验证仅在协调者放行的开发机窗口执行：

```text
python scripts/measure/issue-873-heap.py --gcdump --first-idle-only --executable <clean-export/train.exe> --scratch <outside-checkout> --output <anonymous-output>
```

该显式模式先通过原 measure.ps1 跑一次参考，再跑 scene-soak 到首次 idle，采一份后结束，不等待 9000 秒。measure.ps1 隔离用户数据；停止只针对本次子进程树和门控 PID。准备/参考/首轮/采集共 540 秒截止，分析最多 360 秒，加收尾约 16 分钟以内，申请 20 分钟窗口；预期耗时须用实际结果复核。验证只证明诊断链，不作为性能验收。未获窗口前不得运行此命令。
