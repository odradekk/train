# 统一测试入口

`scripts/test.ps1` 是 PowerShell 7 测试入口。[Testing standard](implement-sop.md#testing-standard)决定改动需要哪些证据；[Windows CI](ci.md)使用同一入口。四模块的目标验证责任见[独立构建与契约验证](contracts/testing.md)，当前普通 C# 测试不证明真实工厂、Godot 隔离装配或人工验收通过。

## 选择

```powershell
# 默认快速层
pwsh -NoProfile -File scripts/test.ps1
# 交付前和常规 CI
pwsh -NoProfile -File scripts/test.ps1 -Layer fast,integration
# 快速、集成和引擎三层，不含导出
pwsh -NoProfile -File scripts/test.ps1 -Layer all
# 只选择 MSTest 项目
pwsh -NoProfile -File scripts/test.ps1 -Layer fast,integration -Project core,content
# 引擎层与两次 Windows 导出冒烟
pwsh -NoProfile -File scripts/test.ps1 -Layer engine -ExportWindows
```

`-Layer` 接受 `fast`、`integration`、`engine` 的一个或多个值；`all` 等于三者，默认是 `fast`。逗号多选和 PowerShell 数组都可用，例如 `& ./scripts/test.ps1 -Layer @('fast', 'integration')`。`-Project` 接受 `core`、`content`、`godot`、`contract-support` 的一个或多个值，只筛选四个 MSTest 项目；指定它后不执行契约结构、内容 Schema 或引擎检查。`-ExportWindows` 仅能与未指定 `-Project` 的引擎层一起使用。无效选择、整层实际执行零个用例、必需工具缺失或最低用例数不足均不能通过。最低用例数表位于 `scripts/test.ps1` 的 `$projects`，引擎层使用其中的 `godot` 行；删除用例或调整分层时必须同步更新。最低数为零的项目与层组合仍执行，只允许该组合没有用例。

快速层运行未标记 `integration` 的 MSTest；集成层运行真实文件读写和子进程用例。无 `-Project` 的集成层还检查项目边界、契约公开面、调用样例和内容示例 Schema。Schema 需要 Python 3 与 `jsonschema`（CI 固定为 Python 3.11、`jsonschema==4.25.1`）；它不验证跨文件引用或生产加载器行为。

引擎层在真实 Godot 中运行 `Tests/Engine/Train.Engine.Tests.csproj` 的当前 App 集成用例，需要 `GODOT`。独立测试程序集固定使用 Chickensoft.GoDotTest 2.0.47 和 GodotTestDriver 3.1.83；根项目仅在 Debug 编译 `Tests/Engine/Bootstrap/Runner.tscn` 的启动代码，加载外部测试 DLL 并生成 JUnit。Release、ExportDebug 和 ExportRelease 排除测试代码、启动场景和测试依赖。这些用例不证明目标四模块的隔离装配。显式导出选择另用 `scripts/checks/run-export-smoke.ps1` 检查同一次 Windows 导出的两次生产 `--smoke` 运行，需要 Windows 和匹配的导出模板。Benchmark、长测、额外平台和人工观察不在 `all` 中。

## 执行与边界

入口构建一次 `Train.sln`，MSTest 使用 Microsoft.Testing.Platform 和 `--no-build`。项目边界检查跳过重复解决方案构建，仍构建不在解决方案中的调用样例和检查工具。Core/Content 的隔离重建已暂停，由 [#406](https://github.com/odradekk/train/issues/406)恢复；当前通过不证明隔离构建。

各层测试执行预算为快速 30 秒、集成 180 秒、引擎 300 秒。构建准备上限为 300 秒，Godot 导入、导出各为 120 秒。超时导致失败并保留可用转储，不通过放宽预算掩盖超时。必要的 Godot 导入只做一次；两次冒烟共享一次 Windows 发布导出。同一层内选中的 MSTest 项目并行运行，每个项目保留各自的最低用例数、TRX、超时和挂起转储；其他检查串行执行。同一工作树不允许并发运行入口。

引擎内容用例通过真实内容源和物理夹具验证加载与重载，App 只负责输入生成；日志断言使用类型化观测，菜单用例通过信号并替换退出操作，仿真用例推进真实帧并使用控制台操作。引擎结果要求退出码、JUnit 结果及最低用例数均满足入口规则；实际执行、通过、失败和跳过数写入摘要，不以帧数或超时退出证明通过。

两次导出 smoke 分别使用空用户目录和有效 Mod 用户目录，都必须退出 0 并提供 JSONL 证据。生产 smoke 验证内容重载提交、核心粮食和 PNG 加载，不接收测试专用预期参数。脚本核对 Mod 运行的 Info 事件：`content_mods_scan_summary` 的 `accepted_count=1`、`isolated_count=0`；`content_reload_summary` 为 committed，accepted 数比空目录运行增加 1。

检查输入、临时文件、运行日志、导出产物和测试用户数据写入运行目录，`TEMP`、`TMP`、`TMPDIR` 指向其中的 `tmp`。Godot 原生日志通过 `--log-file` 定向；导入和导出用 `--recovery-mode` 跳过编辑器插件。导入、引擎测试和导出 smoke 将 Windows 的 `APPDATA`、`LOCALAPPDATA`，或非 Windows 的 `XDG_CONFIG_HOME`、`XDG_DATA_HOME`、`XDG_CACHE_HOME` 指向运行目录；只有导出步骤保留真实 APPDATA 以访问模板。导出是环境隔离的例外，不能由导出通过推断真实 Godot 目录未变化；具体路径影响作为单次验证证据记录在 Issue。引擎测试与两次导出 smoke 使用隔离用户目录，运行结束后清理目录；JSONL 日志先复制到运行证据目录。入口比较运行前后的快照：在 `bin/`、`obj/`、`.godot/`、`.git/` 和本次运行目录之外新增或删除文件、Git 状态或 HEAD 变化、真实 `Godot/app_userdata/Train` 下文件变化，均导致整次失败。运行期间不要修改工作树；保留已有 `override.cfg`。

## 结果与保留

每次运行目录为 `TestResults/<选择层>-<yyyyMMddTHHmmssfff>-<7位提交>/`，目录名中的时间为 UTC，只产生一份 `summary.json` 摘要。摘要记录提交、工作区状态、选择、构建、各项目与各层的执行／通过／失败／跳过数（引擎用例来自 JUnit）、用时和结论。跳过数记入摘要，整次运行仍可以通过，但跳过的用例不算已验证；脏工作区结果不能证明所记录的干净提交通过。

通过后只保留摘要；失败保留 TRX、JUnit、日志、输入和转储等全部证据。入口只保留最近 10 个符合命名规则的运行目录；入口持有该工作树的命名互斥锁时，没有 `summary.json` 的运行目录一律视为崩溃遗留并清理。未取得互斥锁则拒绝同工作树新运行。`TestResults/` 下其他目录不清理。结果只有通过或失败；必需检查失败、缺少环境或未完成时返回退出码 1；人工、目标硬件和尚未实现的验证另行报告。
