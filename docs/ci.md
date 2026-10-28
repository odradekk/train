# CI：Windows 检查与 Linux 夜间阶段检查

四模块契约验证的目标增量见[CI 与交付时机](contracts/testing.md#ci-与交付时机)。以下描述工作流的实际选择；各次结构检查结果以统一入口的摘要为准。

`.github/workflows/windows-checks.yml` 在发往 `master` 的 PR 创建、更新和重新打开时运行受影响项目的快速与集成层，并在其后独立运行非阻断引擎层：

```powershell
pwsh -NoProfile -File scripts/test.ps1 -Layer fast,integration
pwsh -NoProfile -File scripts/test.ps1 -Layer engine
```

工作流检出完整历史并执行 `dotnet tool restore`，恢复固定的 dotnet-affected。入口按默认基准 `origin/master` 的共同祖先及实际改动选择 MSTest 项目和独立检查，规则见[按改动选择](run-tests.md#按改动选择)。选中 MSTest 或契约检查时构建一次 `Train.sln`，使用 SDK 的 `dotnet test --test-modules` 运行所选项目的已构建测试程序集；Schema 单独运行时不构建。Schema 由工作流准备 Python 3.11 与 `jsonschema==4.25.1`。常规两个步骤分别生成运行目录和摘要。引擎步骤始终调用统一入口；引擎被选中时构建一次解决方案并运行当前 App 的真实 Godot 集成用例，未选中时明确报告、写摘要并成功退出，不构建或运行测试。该步骤使用 `continue-on-error: true`，暂不阻断 PR；本地失败仍返回 1，摘要保留实际 JUnit 用例计数。引擎是否受影响由统一入口按项目依赖与引擎路径规则选择，选中后执行全部引擎用例；用户于 2026-10-13 决定该步骤保持非阻断，不再计划转为阻断。Core/Content/Scenes/Godot 隔离重建按项目文件、构建配置、解决方案、边界矩阵和检查工具变更进入默认选择，`-All` 也会执行；选中后在集成队列结束后独立运行，使用 1290 秒上限，队列失败也执行且不计入集成层耗时；收到停止请求时不启动尚未开始的独立隔离检查。普通 .NET 宿主用例不提供真实引擎证据。

测试前先运行 `python .agents/scripts/check_file_size.py --all`，检查全部跟踪文件不超过 5,000 token，规则和例外见 AGENTS.md 的 Agent workflow 节；它失败时本次检查失败。

YAML 只传递明确选择并发布入口摘要，不复制游戏断言或推断路径影响。CI 不改变仓库设置、required checks 或分支保护。具体用例数、跳过数和耗时以每次摘要为准；CI 通过不表示人工、性能或全仓目标责任都已验证。

手动触发的可选 `commit` 输入为完整 40 位提交 SHA，所有选择均检出该提交，留空则检出触发的 ref；固定提交的运行与身份核对方法见[远端检查](run-tests.md#远端检查)。

## 显式较重运行

合并前可在 PR 上添加 `ci:correctness` 标签，请求对**添加标签时的 PR head**执行一次：

```powershell
pwsh -NoProfile -File scripts/test.ps1 -All -Layer all
```

标签只响应 `labeled` 事件。保留标签后继续推送仍跑常规快速、集成和非阻断引擎步骤；要重新请求，先移除再添加。仓库已有该标签；若被删除，可先用下面的 `gh label create` 重建，再请求运行：

```powershell
gh label create ci:correctness --repo odradekk/train --description '显式请求一次 Windows 正确性回归' --color 1d76db
gh pr edit <PR号> --repo odradekk/train --add-label ci:correctness
# 同一 PR 再次请求：先 --remove-label ci:correctness，再 --add-label ci:correctness
```

手动运行可选择 ref 和 `routine` 或 `correctness`。Windows job 只接受上述 PR 事件和这两种手动选择，不在 master push 或 schedule 时运行。触发和查看命令见[远端检查](run-tests.md#远端检查)。

`correctness` 运行七个现有 MSTest 项目、结构与公开面、Core/Content/Scenes/Godot 隔离、内容示例 Schema 及当前 App 的非导出真实 Godot 集成用例。入口复用一次游戏构建和导入并运行独立引擎测试程序集；集成层 MSTest、structure、verify 与可选 Schema 共用最多 4 个并发任务和 2070 秒截止时间；选中的 isolate 在该队列结束后独立运行，使用 1290 秒上限，队列失败也执行且不计入集成层耗时；收到停止请求时不启动尚未开始的独立隔离检查。导出冒烟对同一次 Windows 导出运行两次，用 `-Layer engine -ExportWindows` 另外显式选择；Benchmark、长测、人工和额外平台也不属于此选择。

GitHub 要求 `workflow_dispatch` 文件已存在于默认分支，所以新工作流合并前使用 PR 标签取得远端证据。见 [GitHub 触发文档](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#workflow_dispatch)。

两种 Windows 执行选择分别生成 `Windows routine`、`Windows correctness` 检查。其他标签的 run 名为 `Windows unrelated-label`，job 在分配 runner 前跳过；GitHub 的 Checks API 会保留该 job 名的未求值表达式，与已执行的常规或正确性检查不同名。

在 `master` 上手动运行 `routine` 时，默认基准与 HEAD 相同，快速与集成步骤得到空选择并成功退出，不构建；独立引擎步骤也报告未选择并成功退出，不下载 Godot、不构建或运行测试。需要全仓回归时选择 `correctness`。

## Linux 夜间运行

同一 `windows-checks.yml` 在每日 UTC 20:23（北京时间 04:23）的 schedule、master push 和手动 `nightly` 时，调用 `scripts/test.ps1 -All -Layer all,stage`。它执行全部快速、集成、无界面引擎和 Linux 阶段检查；`all` 本身仍只含前三层。阶段平台范围见[阶段检查与候选基准](run-tests.md#阶段检查与候选基准)。

手动选择 `long-example` 时，`example` choice 输入接受 `starting-trade`、`lease-expiry`、`rail-repair`、`conquest`、`clearance`、`operating-failure`、`long-consist`、`standard-world`、`busy-combat` 和 `crowded-station`，通过环境变量传给[长示例脚本](../scripts/checks/run-long-example.ps1)。这条路径构建一次 Release CLI，并行运行 replay、save → resume、make → check 三组，再比较恢复后缀；不运行全量检查或恢复 dotnet-affected。每条命令完成即写入记录、日志和 `TestResults/long-example-<时间>-<7位提交>/summary.json`，失败依赖保持 `not_started`，任何失败或未执行使脚本非零退出。示例检查的平台分工见[示例手册](../examples/README.md#重制与长验证)。

手动选择 `measure` 时，[测量运行器](../scripts/measure/run-measurement.ps1)在 Linux 测试机器的真实窗口中运行指定场景。输入如下；`commit` 仍指定被测提交：

| 输入 | 含义 | 默认值 |
| --- | --- | --- |
| `measure_scenarios` | 逗号分隔的场景列表，由 `measure.ps1` 校验 | `scene-reference` |
| `measure_input_set` | `example` 使用示例；`scene-scale` 用被测 checkout 的已提交配方生成 1×、10×、30×、100× 场景输入 | `example` |
| `measure_input` | 仅 `example` 使用，须为被测 checkout 中 `examples/` 下已存在的示例存档 | `examples/standard-world/save.json` |
| `measure_repeat` | 每个所选场景的轮数 | `3` |
| `measure_node_ready_hold` | 仅 `measure`：参考中指定节点和首开在 Ready 后保持 60 秒；路线忽略 | `false` |
| `measure_seconds` | 每轮时长，秒；`0` 使用场景默认值 | `0` |

运行器只导出一次 Release。`example` 先跑一次 `scene-reference`，把参考文件通过 `ReferencePath` 传给每个所选场景；这次准备不计入指定轮数。因此默认选择还会再跑 3 次 `scene-reference`。

`scene-scale` 调用被测 checkout 的 `scripts/measure/scene-scale.py generate`，构建 Release CLI 和 SceneScale 工具，对四档执行 bounds、make 和 stats，生成存档及统计。运行器固定只对 1×、10× 执行参考准备和所选场景，轮数取 `measure_repeat`；30×、100× 的打开内存需求超出测试机容量，只生成存档及统计。该输入集忽略 `measure_input`，只使用仓库内的固定配方；正式打开测量选择 `scene-node-first`。runner 不运行 ready 或 local；近景核对在本机独立运行 `scene-scale.py verify`，只核对 1×、10×，两档都做 10 秒复核。

手动选择 `window-soak` 时，同一运行器固定使用 `example`、`scene-soak`、`examples/standard-world/save.json`、Repeat 1，时长取 `soak_seconds`（默认 7200 秒）。这些选择均只报告测量结果，不强制性能预算。结果写入 `TestResults/measure-<UTC时间戳>-<被测提交短SHA>/`。`example` 的 `reference/` 是准备运行，`scenarios/` 是所选场景；`scene-scale` 在 `scale-1x/`、`scale-10x/` 分开保存各档的 `reference/` 和 `scenarios/`。`scale-inputs/` 保留并上传生成器根目录的 JSON（含 `scale-manifest.json`）、`.log` 文件及 `saves/`，不保留临时内容树 `inputs/`。`export/` 共用同一次导出日志。顶层 `summary.json` 记录 `input_set` 和各档阶段、成功状态。各档独立记录失败状态，某档参考或场景测量失败后继续其他档位，最后只要有失败就以非零退出。生成失败时保留已产生的清单和日志，并以非零退出。每个场景目录中的 `summary.json.runs` 仍供离线汇总使用，不能把不同档位混合统计。

规模路径的 [P2-R](https://github.com/odradekk/train/actions/runs/40626707062) 对 1×、10× 各跑参考和首开 1 轮；[F2](https://github.com/odradekk/train/actions/runs/40636009701) 各跑参考 1 轮、首开 30 轮。下表单位为秒，依据 job/step 时间及进程报告，不用 ZIP mtime 推断阶段。

| 阶段 | P2-R | F2 |
| --- | ---: | ---: |
| job | 348 | 2289（38分09秒） |
| Window measurement | 323 | 2232 |
| 导出 | 19.648 | 20.009 |
| 游戏进程 | 4个合计223.427 | 参考1×/10×：76.322/94.760；首开各30轮合计：704.349/1140.177 |
| Window 未分离余项 | 79.925 | 196.383 |
| publish / upload / cleanup | 3 / 5 / 1 | 18 / 22 / <1 |

未分离余项包含生成、准备及脚本开销，没有独立生成 wall 计时，不能当作纯生成耗时。这些记录供规模路径排期参考，不是时长上界或新阈值；本机近景核对另计。

这两种选择只能经 `workflow_dispatch` 触发，会在主机屏幕上打开游戏窗口，运行前须与协调者确认 runner 时段和主机条件。输入通过环境变量传入脚本。工作流从 `GITHUB_SHA` 归档 `scripts/` 到 `RUNNER_TEMP` 并保留目录结构，运行该版本的脚本；`export-measurement.ps1 -SourceDirectory` 指向被测 checkout，所以也能测量搬迁前的提交。运行器把官方 `Godot_v4.7.2-stable_mono_export_templates.tpz` 缓存在 runner 工具目录，每次按同一发布的 `SHA512-SUMS.txt` 重新核对，只解出 `linux_release.x86_64` 到 `RUNNER_TEMP`，再导出 Linux Release。游戏使用 runner 用户自己的桌面会话。主机须安装 gamescope；入口始终用 `gamescope -w 1920 -h 1080 -W 1920 -H 1080 --` 固定游戏输出为 1920×1080，找不到 gamescope 时失败。

所有 Linux 运行（nightly、post-merge、long-example、measure 和 window-soak）在 checkout 前先用 bash 掩码身份、工作区及工具目录，包括 pwsh 的查找目录和解析后的真实目录，遮盖后续控制台日志中的这些值。`Set up job` 中 runner 自身的 `Machine name` 行早于该步骤，按用户决定保持现状。

所有 Linux 运行的 `always()` 发布步骤均用工作流版本的 `scripts/measure/publish-measurement.ps1` 处理整个 `TestResults/` 的文本：把 HOME、PSHOME、Python 所在目录、GITHUB_WORKSPACE、RUNNER_TEMP、RUNNER_TOOL_CACHE 和 runner 根目录等已知绝对路径前缀换成占位符，再扫描本机主机名、用户名、home 路径、运行时 socket 路径和 sudo 字样；检查或测量失败时也执行。`tool-path` 类别独立采集运行时的 Python 目录、Godot 所在目录、PSHOME、DOTNET_ROOT、RUNNER_TOOL_CACHE、RUNNER_WORKSPACE 和 RUNNER_TEMP，按实际值精确检查残留，防止替换表漏项。所有 Linux artifact 只含文本证据和 PNG：PNG 不扫描内容，其余文件在发布前删除，日志只记录删除数量。删除类型包括二进制转储、`.gdignore` 和 `.html` 报告。所有文件名仍须通过身份检查。扫描未通过则步骤失败，不发布 step summary 或 artifact，报错只写相对文件名、行号和类别，不写命中的值；行号为 0 表示文件名命中或读取失败。输出不写 sudo 状态或 socket 路径，显示只记作 `x11 :N` 或 `wayland`。

以上事件／选择复用同一个 Linux job，只有它们能分配带 `[self-hosted, linux, nightly]` 标签的 runner；PR 和其他事件被白名单条件排除。这些路径不分配 Windows runner。运行名区分 `Linux post-merge`、`Linux nightly`、带示例名的 `Linux long-example`、`Linux measure` 和 `Linux window-soak`。目前只有一台匹配的 runner，任务自然排队，不设置额外并发组；runner 排队超过 24 小时会被 GitHub 取消。触发、查看和下载方法见[远端检查](run-tests.md#远端检查)。

全量检查 job 上限为 138 分钟（计算见[执行与边界](run-tests.md#执行与边界)）；长示例初始上限分别为 starting-trade 105 分钟、lease-expiry 485 分钟、crowded-station 175 分钟，其余 105 分钟。长示例按三组 Windows 最坏实测耗时的最大值加构建时间，再乘 1.5、向上取整到分钟；[初始计算记录](https://github.com/odradekk/train/issues/565#issuecomment-6232396762)列出 #299、#546 的命令数据和暂停处理。Linux 实测的 1.5 倍若超过既有上限，按[预算原则](contracts/testing.md#预算调整原则)上调。不设置单条 CLI 命令超时。`measure` 固定上限为 720 分钟，容纳两小时场景的 3 轮及导出、参考运行和退出余量。自托管 runner 已用于 `lease-expiry` 的 485 分钟长 job；所选场景总耗时超过 720 分钟时，仍由 job 时限终止。窗口长测的上限仍为 200 分钟：7200 秒循环、跑完最后一段（约 16 分钟）、导出与参考运行（缓存模板时约 5 分钟）合计约 150 分钟，另留余量；首次正式运行后按同一预算原则核对。

局域网自托管 runner `lan-linux-nightly` 由 systemd 服务运行，主机提供 PowerShell、Godot 和 Python；版本与环境配置见[Linux 验证与自托管 runner](local-environment.md#linux-验证与自托管-runner)。checkout 后，job 使用固定 SHA 的 `actions/setup-dotnet` 按 `global.json` 准备 SDK，安装到 `${{ runner.tool_cache }}/dotnet`，避开系统包管理器维护的目录。action 将该目录加入 PATH 并导出 `DOTNET_ROOT`，后续 CLI、Godot 和 `dotnet tool restore` 共用它；环境记录保留实际版本和 `DOTNET_ROOT`。workflow 不含主机安装路径，不安装额外 Godot，不使用 xvfb。job 仅授予 `contents: read`，checkout 完整历史与所选 SHA，`persist-credentials: false`。schedule 使用默认分支的工作流。

Linux 的所有必需步骤失败均传播到 job，不使用 `continue-on-error`。发布步骤使用 `always()`，先执行上述脱敏及残留检查，通过后才发布摘要；产物上传同样只在发布步骤成功时执行。按选择上传筛选后的整个 `TestResults/`，artifact 名为 `linux-nightly-<run_id>-<attempt>`、`linux-post-merge-<run_id>-<attempt>`、`linux-long-example-<示例>-<run_id>-<attempt>`、`linux-measure-<run_id>-<attempt>` 或 `linux-window-soak-<run_id>-<attempt>`，保留 14 天。上传之后始终清理：确认当前目录和 Git 仓库根目录均为 `GITHUB_WORKSPACE`，执行 `git clean -ffdx`，并在日志列出清理后的目录；不能在其他工作目录运行该清理命令。统一测试入口仍负责真实用户目录和临时目录的快照检查。强制终止 runner 或服务故障时，`always()` 不能保证上传或清理完成，应按实际日志报告。

## 环境与证据

- runner 为 `windows-2025` x64；系统镜像会更新，实际 ImageOS／ImageVersion 保留在产物中。
- SDK 由 `global.json` 固定为 10.0.112、禁止 roll-forward；`setup-dotnet` 在干净 runner 安装它。
- 测试使用 PowerShell 7.6.6 官方便携构建。bootstrap 使用 runner 自带 PowerShell，将便携版本置于后续步骤 PATH 首位；测试子进程显式调用便携可执行文件。
- `correctness` 始终下载 Godot 4.7.2 stable .NET Windows x64 官方 ZIP；常规选择在快速／集成步骤后读取其摘要，只有 `selection.engine` 明确为 `false` 时才跳过下载并说明原因，摘要缺失或损坏时照常下载并说明。下载后设置 `GODOT` 为 console 可执行文件。两个工具 ZIP 的 SHA256 固定在工作流；actions 固定到 commit SHA。常规选择的准备步骤同样使用 `continue-on-error`。不安装无关导出模板，不缓存跨运行 Godot／包／结果。
- PR 显式检出 head SHA；手动运行检出所选 ref 的 SHA。统一摘要记录实际提交、工作区状态、选择、构建、项目与层的用例数（含跳过）、耗时和结论；`TestResults/ci/environment.txt` 补充 Actions run URL、attempt 和 runner 镜像。引用 Core/Scenes 工程的 MSTest 不证明部署的 Godot 装配，真实引擎证据另列。

入口使用唯一 `TestResults/<选择层>-<yyyyMMddTHHmmssfff>-<7位提交>/`。普通运行通过只保留一份 `summary.json`；Windows 阶段运行另保留截图与导出验证证据，详见[结果与保留](run-tests.md#结果与保留)。失败保留 TRX、JUnit、日志、转储等全部证据。CI 读取该一级运行目录的摘要追加到 job summary，不生成另一份测试摘要。Windows 的整个 `TestResults/` 上传为 `windows-<selection>-<run_id>-<attempt>`，保留 14 天，重跑使用新的 attempt 名称。`include-hidden-files` 保留验证产物中的隐藏文件。

摘要发布和上传步骤使用 `always()`；失败后仍执行，取消时尽可能收集已产生文件。强制终止 runner 或上传服务不可用时不能保证上传。入口非零状态直接传递给步骤；快速、集成和显式 correctness 失败会令 job 失败，常规选择的 Godot 准备和独立引擎步骤均暂用 `continue-on-error`。Godot 下载失败时准备步骤的失败可见，引擎被选中时引擎步骤也报告缺环境；若其他必需步骤成功，常规 job 仍为绿色。后续上传不改变这些规则。工具准备失败仍尝试入口，让所选检查报告缺环境；若连 PowerShell 都未准备好，只保留已产生的环境记录与 Actions 原始日志，不宣称执行通过。

Windows job 上限为 115 分钟，为工具下载、构建与检查预留时间，并结束卡住的任务；入口预算合计与 job 上限的关系见[执行与边界](run-tests.md#执行与边界)，Linux 上限见[Linux runner](#linux-夜间运行)。当前检查的实际耗时以各次运行摘要为准。

测试预算和单项等待上限都按本机多 pair 常态负载下实测的最坏值留出余量（通常乘 1.5 后取整），项目增长后按新的实测上调，不按安静时的当前耗时压缩；各项依据见[执行与边界](run-tests.md#执行与边界)。

私有仓库的 Windows 托管运行消耗付费 Actions 分钟，包含全层的 correctness 路径要求显式请求。测试证据与复用规则见 [Testing standard](implement-sop.md#testing-standard)。
