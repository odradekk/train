# 临时 Linux 堆诊断

本工具只用于 #873 第一阶段取证，不属于产品或正式性能测量。目标框架为 `net10.0`，ClrMD 固定为 `4.2.751201`。

`scripts/measure/issue-873-heap.py` 负责启动进程、采集、分析和清理。Linux 控制器从目标进程已加载的运行时找到匹配的 DAC（运行时诊断库），传给下面的命令；不能拿另一个运行时的库代替。

```text
dotnet Train.HeapDiagnosis.dll preflight <ready-marker> <release-marker>
dotnet Train.HeapDiagnosis.dll analyze-preflight <dump> <matching-dac> <output-directory>
dotnet Train.HeapDiagnosis.dll analyze <first-dump> <second-dump> <matching-dac> <output-directory>
```

`preflight` 原子发布包含 PID 数字的 ready 文件，主线程等待 release 文件，后台线程同时存活；120 秒后自行退出。控制器发现 ready 后采集转储，再原子发布 release，最后清理进程。`analyze-preflight` 要求强根可达的测试 ContentView、两份大数组、相同对局标识和已完成 Task 都能读到，否则失败。成功写出 `preflight.json`，只证明诊断链可用。

`analyze` 完成两份转储的检查后写出 `heap.json`，包含类型差、强根可达／无根／Free 统计、工厂和 ContentView 统计、ContentView 根链、大于等于 1 MiB 的 byte／char 数组根链及分类。只有宿主 `_content` 直接指向的视图标为 current，其他视图标为 non-current；后者可能是历史版本，不能单凭该标签判定为上一局。

强根遍历包含 ClrMD 的普通根、线程静态根及 dependent handle（条件依赖引用）。每个对象只标记一次，共享对象归入第一次发现它的根路径，分类优先级是数组池、线程本地存储（TLS）、内容、根种类。分组依赖枚举顺序，不代表独占保留大小。无根表示未发现枚举强根路径，不等于已经回收；Free 不计为无根对象。Free 大小也不能单独证明某次分配会因碎片失败。

`ContentViewFirstPathGroups` 沿每个对象的首次父路径，寻找最近的 ContentView 祖先，累计对象数和总大小，包含视图自身。路径上嵌套多个视图时，只计入最近的视图；无根视图的计数和大小为零。共享对象仍只计一次，这些数值不能证明移除某条引用就能回收这些对象。

只对 strong-root-reachable 的 RecordingService 读取 `_pending`。非空时读取 `Snapshot.Binding.Session` 和宿主 `_active.Binding.Session`，在内存中比较 Guid；从 `Work.m_stateFlags` 的 Task 完成位派生完成状态。输出只有 PendingIsNull、SnapshotBindingEqualsCurrent 和 WorkIsCompleted。pending 为空时，后两个状态是 null。字段缺失、读取失败、DAC 不匹配、无法遍历或堆校验失败会使整次命令失败，不会当作 false。

报告不写对象地址、字符串内容、原始字段值、路径或环境变量。异常只向 stderr 写异常类型，参数错误只写固定文字。两份转储必须使用同一运行时且有匹配 DAC；Linux 的诊断通道、DAC 加载及字段读取须由同一次 GitHub Actions 任务的 preflight 验证。本机编译通过不能代替这项验证。
