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
# 引擎层与两项 Windows 导出运行检查
pwsh -NoProfile -File scripts/test.ps1 -Layer engine -ExportWindows
```

`-Layer` 接受 `fast`、`integration`、`engine` 的一个或多个值；`all` 等于三者，默认是 `fast`。逗号多选和 PowerShell 数组都可用，例如 `& ./scripts/test.ps1 -Layer @('fast', 'integration')`。`-Project` 接受 `core`、`content`、`godot`、`contract-support` 的一个或多个值，只筛选四个 MSTest 项目；指定它后不执行契约结构、内容 Schema 或引擎检查。`-ExportWindows` 仅能与未指定 `-Project` 的引擎层一起使用。无效选择、零匹配、必需工具缺失或最低用例数不足均不能通过。

快速层运行未标记 `integration` 的 MSTest；集成层运行真实文件读写和子进程用例。无 `-Project` 的集成层还检查项目边界、契约公开面、调用样例和内容示例 Schema。Schema 需要 Python 3 与 `jsonschema`（CI 固定为 Python 3.11、`jsonschema==4.25.1`）；它不验证跨文件引用或生产加载器行为。

引擎层经 `scripts/checks/run-app-check.ps1` 执行 logging、content、main-menu 和 simulation-host 四项当前 App 检查，需要 `GODOT`。显式导出选择另外运行 content-export、mods-export，需要 Windows 和匹配的导出模板。Benchmark、长测、额外平台和人工观察不在 `all` 中。

## 执行与边界

入口构建一次 `Train.sln`，MSTest 使用 Microsoft.Testing.Platform 和 `--no-build`。项目边界检查跳过重复解决方案构建，仍构建不在解决方案中的调用样例和检查工具。Core/Content 的隔离重建暂时暂停，由 [#406](https://github.com/odradekk/train/issues/406)恢复；当前通过不证明隔离构建。

各层测试执行预算为快速 30 秒、集成 180 秒、引擎 300 秒。超时导致失败并保留可用转储，不通过放宽预算掩盖超时。必要的 Godot 导入只做一次；两项导出检查共享一次 Windows 发布导出。检查串行执行，同一工作树不允许并发运行入口。

content 检查自己在输出目录准备 `content-input/initial`、`content-input/added`，复制粮食体素和共用图片，生成市场表、聚居地类型及新增 `smoke-ore` 探针，验证同一重载器的新增与移除。导出过滤包含内容 JSON 与 PNG；PNG 使用 `importer=keep` 保留原始字节。通过必须同时满足退出码 0、行为标记和 `APP_CHECK_PASSED <名称>`，不以帧数或超时退出证明通过。

运行拥有的输入、临时文件、日志、导出和用户数据均写入运行目录，`TEMP`、`TMP`、`TMPDIR` 指向其中的 `tmp`。Godot 原生日志通过 `--log-file` 定向；导入和导出用 `--recovery-mode` 跳过编辑器插件，导出准备保留模板所在的 APPDATA。每项 App 检查使用独立用户目录。入口比较运行前后的工作区和真实用户数据目录，越界写入导致整次失败；保留已有 `override.cfg`。

## 结果与保留

每次运行目录为 `TestResults/<选择层>-<yyyyMMddTHHmmssfff>-<7位提交>/`，只产生一份 `summary.json`。摘要记录提交、工作区状态、选择、构建、各项目与各层的执行／通过／失败／跳过数、用时和结论。跳过不能作为通过；脏工作区结果不能证明所记录的干净提交通过。

通过后只保留摘要；失败保留 TRX、日志、输入和转储等全部证据。入口只保留最近 10 个符合命名规则的运行目录；没有最终摘要且所属 PID 和启动时间已不对应存活进程的目录视为崩溃遗留并清理。仍存活的运行阻止同工作树新运行。`TestResults/` 下其他目录不清理。必需检查失败、受阻或未完成时返回非零；人工、目标硬件和尚未实现的验证另行报告。
