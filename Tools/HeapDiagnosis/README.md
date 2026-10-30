# 临时 Linux 堆诊断

本工具只用于 #873 第一阶段取证，不属于产品或正式性能测量。目标框架为 `net10.0`，ClrMD 固定为 `4.2.751201`。

`scripts/measure/issue-873-heap.py` 负责启动进程、采集、分析和清理。Linux 控制器从目标进程已加载的运行时找到匹配的 DAC（运行时诊断库），传给下面的命令；不能拿另一个运行时的库代替。

```text
dotnet Train.HeapDiagnosis.dll preflight <ready-marker> <release-marker>
dotnet Train.HeapDiagnosis.dll analyze-preflight <dump> <matching-dac> <output-directory>
dotnet Train.HeapDiagnosis.dll analyze-one <dump> <matching-dac> <output-directory>
```

`preflight` 原子发布包含 PID 数字的 ready 文件，主线程等待 release 文件，后台线程同时存活；120 秒后自行退出。控制器发现 ready 后采集转储，再原子发布 release，最后清理进程。fixture 保留三个 RecordingService：pending 为空、相同对局标识且 Task 已完成、不同对局标识且 Task 未完成。另外保留两个 ContentView、四个至少 1 MiB 的数组，以及每个视图各一条包含 10,000 个节点的 LinkedList。

`analyze-preflight` 严格检查三个派生状态的集合、视图和数组数量及根路径、20,000 个链表节点，以及对象分类的总数和总大小。任何问题都判失败，不使用耗时断言。成功写出 `preflight.json`，只证明诊断链可用。

`analyze-one` 每次进程只读一份转储，写出输出目录下的 `heap.json`。报告包含类型数量和大小、强根可达／无根／Free 统计、工厂和 ContentView 统计、ContentView 根链、大于等于 1 MiB 的 byte／char 数组根链及分类。控制器分别调用两次，每次各设 420 秒上限，再按 `Types` 字典计算类型差。一份失败仍继续另一份；报告有问题计数时，差分标记为 partial。

控制器在第二份采集完成后结束测量进程组和游戏，再分析转储；长测退出码只作记录。结束测量前，先把匹配的 DAC 复制到本任务临时目录，避免测量入口清理导出目录后找不到它。准备与采集最多 36 分钟，两次分析最多 14 分钟，为 52 分钟步骤上限留下收尾时间。

强根遍历包含 ClrMD 的普通根、线程静态根及 dependent handle（条件依赖引用）。BFS 首次发现对象时，从父对象传下分类和最近的 ContentView，每个对象只归类、计数一次，不反复回溯父链。分类优先级是数组池、线程本地存储（TLS）、内容、根种类。共享对象归入第一次发现它的根路径，分组依赖枚举顺序，不代表独占保留大小。

只有宿主 `_content` 直接指向的视图标为 current，其他视图标为 non-current；后者可能是历史版本，不能单凭标签判定为上一局。`ContentViewFirstPathGroups` 为每个视图累计最近归属于它的对象数和总大小，包含视图自身。路径上嵌套多个视图时，只计入最近的视图；无根视图的计数和大小为零。这些数值不能证明移除某条引用就能回收这些对象。

只有报告视图和大数组时才回溯根路径，每条最多保留末端 128 步。`PathTruncated` 为 true 表示省略了靠近根的前段，此时显示的首项不一定是根。无根表示未发现枚举强根路径，不等于已经回收；Free 不计为无根对象。Free 大小也不能单独证明某次分配会因碎片失败。

只对 strong-root-reachable 的 RecordingService 读取 `_pending`。非空时读取 `Snapshot.Binding.Session` 和宿主 `_active.Binding.Session`，在内存中比较 Guid；从 `Work.m_stateFlags` 的 Task 完成位派生完成状态。输出 PendingIsNull、SnapshotBindingEqualsCurrent 和 WorkIsCompleted。pending 为空时，后两个状态是 null；字段读取失败时，对应状态也为 null，不伪造 false。宿主字段读取失败不阻止独立读取 pending 或 Task 状态。

游戏转储的堆校验问题、坏对象、缺失目标、坏引用和字段异常按问题类别及类型累计到 `Issues`，继续统计能读到的部分。若枚举器异常终止，该次枚举剩余内容不可恢复；此前数据和其他枚举仍保留。因此总数只涵盖成功读取的对象，出现问题时可达性和类型数都可能不完整，不能当作完整堆结论。preflight 对任何 `Issues` 保持严格失败。

报告不写对象地址、字符串内容、原始字段值、文件路径或环境变量。问题计数只包含固定类别、类型名和堆校验问题种类。无法加载转储或 DAC 等致命异常只向 stderr 写异常类型，参数错误只写固定文字。Linux 的诊断通道、DAC 加载及字段读取须由同一次 GitHub Actions 任务的 preflight 验证，本机编译通过不能代替这项验证。
