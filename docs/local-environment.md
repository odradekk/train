# Local validation and Windows builds

Run commands from the checkout under test. The current development host is Windows; the export target is `Windows Desktop` in `export_presets.cfg`. Which checks to run is decided by the [Testing standard](implement-sop.md#testing-standard).

## Windows development and validation

Use PowerShell 7 (`pwsh`), not Windows PowerShell 5 (`powershell.exe`). All .NET projects target `net10.0`; install the exact x64 SDK selected by `global.json` from Microsoft's [.NET 10 downloads](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). Roll-forward is disabled, so another .NET 10 SDK, including a preview, does not satisfy the requirement; other SDKs can stay installed alongside it.

Configure Godot for the current PowerShell session and persist the setting for new processes:

```powershell
$env:GODOT = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
$env:GODOT_EDITOR = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe'
[Environment]::SetEnvironmentVariable('GODOT', $env:GODOT, 'User')
[Environment]::SetEnvironmentVariable('GODOT_EDITOR', $env:GODOT_EDITOR, 'User')
& $env:GODOT --headless --version
dotnet --list-sdks
dotnet --version
$PSVersionTable.PSVersion.ToString()
```

`GODOT` must contain an executable path without arguments; prefer the console executable for command-line checks. Godot's C# tooling finds `dotnet` only through the Godot process's PATH; if Godot reports `.NET Sdk not found` or `Cannot find the dotnet executable`, check that this PATH contains `C:\Program Files\dotnet\`. Restart existing terminals and editors after installing the SDK or changing user environment variables. Report missing prerequisites as validation blockers.

The checkout may retain ignored settings from a historical acceptance environment. Before returning it to normal development, inspect `NuGet.Config` for an obsolete offline-only source and `override.cfg` for a custom acceptance user-data directory. Back up those local files outside the checkout before disabling them; preserve the associated acceptance evidence and saves. Normal dependency restore needs nuget.org access, and normal game data uses `%APPDATA%\Godot\app_userdata\Train`.

For a fresh checkout or after an SDK change, build and import before Godot checks. Resource import alone does not build the C# project. After building, agents import with the [isolated child-process recipe](#agent-手动启动-godot), whose example uses `--headless --path . --editor --import`. These are entry examples, not a mandatory sequence; unfiltered `dotnet test` runs the MSTest projects in the solution, not all repository correctness checks, which run through the [unified test entry](run-tests.md):

```powershell
dotnet build Train.csproj
pwsh -NoProfile -File scripts/test.ps1 -Layer fast
& $env:GODOT --path . --editor   # open the editor after a successful build/import
```

The graphical editor command above and VS Code's `Train (Godot)` launcher are user interaction entries using normal game data. Agents use isolated launches instead. Opening either during a complete test run can change the shared user directory and fail its snapshot check.

VS Code uses the repository's `build`, `test` and `export-windows` tasks. `test` selects the unified entry's `fast` layer; `export-windows` selects resource import, the engine test suite, Windows export, both production smoke runs and the asset export probe. With the C# extension installed, the `Train (Godot)` launch configuration builds the solution and starts the game. `GODOT_EDITOR` points to the main Godot executable for debugging; `GODOT` points to its console wrapper for validation output. This follows the [Godot C# editor setup](https://docs.godotengine.org/en/4.7/tutorials/scripting/c_sharp/c_sharp_basics.html#visual-studio-code).

当前格式内容示例的 Schema 检查还需要可通过 `python` 调用的 Python 3 与 `jsonschema`。CI 使用 Python 3.11 和 `jsonschema==4.25.1`；本地可用 `python -m pip install jsonschema==4.25.1` 安装，并用 `python -c "import jsonschema, referencing"` 验证。未指定 `-Project` 的 `-Layer integration` 和 `-Layer all` 包含此检查；缺少工具或包时入口判失败并返回退出码 1。

## Agent 手动启动 Godot

优先使用已有测试、导出或测量脚本，它们会隔离用户目录。手动导入、运行场景或单个引擎用例时，在待验证的源码目录执行下面的 PowerShell 7 片段，将 `<run>` 替换为新的运行名称，使用被 Git 忽略的目录，只替换子进程内最后一条 Godot 调用及必要的测试环境变量：

```powershell
pwsh -NoProfile -Command {
    $ErrorActionPreference = 'Stop'
    $run = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath('TestResults/manual-godot/<run>')
    $paths = if ($IsWindows) {
        @{ APPDATA = 'appdata'; LOCALAPPDATA = 'localappdata' }
    } else {
        @{ XDG_CONFIG_HOME = 'config'; XDG_DATA_HOME = 'data'; XDG_CACHE_HOME = 'cache' }
    }
    foreach ($name in $paths.Keys) {
        $directory = Join-Path $run $paths[$name]
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
        [Environment]::SetEnvironmentVariable($name, $directory, 'Process')
    }
    & $env:GODOT --headless --path . --editor --import --log-file (Join-Path $run 'godot.log')
    exit $LASTEXITCODE
}
```

重定向只存在于这个子进程及其后代，不会改变调用终端。不要在调用者会话中持久设置 `APPDATA`、`LOCALAPPDATA` 或 XDG 目录：后续 `scripts/test.ps1` 和导出脚本需要从原用户目录读取已安装模板。`--headless`、`--recovery-mode` 和单独指定 `--log-file` 都不能代替用户目录隔离。

Windows 的前后对照导出直接调用 `pwsh -NoProfile -File scripts/export-measurement.ps1 -SourceDirectory '<clean-source-checkout>' -OutputDirectory '<new-output-directory>'`；省略 `-SourceDirectory` 时使用脚本所在的工作树。脚本在自己的进程中隔离 `APPDATA`／`LOCALAPPDATA`，从原目录只读复制发布模板，并在结束时恢复环境。`export-summary.json` 的 `source_directory` 和 `build_commit` 标识实际源码；导出不带 `--recovery-mode`，让生产导出钩子执行，MCP 插件也会在隔离目录生成自己的文件。

MCP 编辑器由协调者按 [MCP 启动方式](godot-mcp.md#启动服务)启动。运行证据留在本次目录，清理只处理自己创建的目录和进程；不得在真实用户目录中做污染实验。

## Windows export and runtime validation

Export requires the project's .NET SDK and matching Godot .NET Windows export templates, on this machine under `%APPDATA%\Godot\export_templates\4.7.2.stable.mono`. 在 Windows 通过统一入口选择引擎层、Windows 导出冒烟（两次运行）及资产导出验证：

```powershell
pwsh -NoProfile -File scripts/test.ps1 -Layer engine -ExportWindows
```

Use an absolute executable path for Windows runtime checks; the process launcher may fail to resolve a relative path containing forward slashes even after export succeeds. Passing the output path on the command line keeps machine-specific paths out of `export_presets.cfg`. Keep the complete export directory, including the PCK and .NET output, together when running or copying the build.

统一入口将主游戏和资产探针导出的 `APPDATA` 重定向到各自运行目录，只从真实模板目录复制 `4.7.2.stable.mono/windows_release_x86_64.exe`，不创建模板 junction；真实模板目录只读，复制的模板在导出 `finally` 中删除，前序步骤失败时也清理。Windows 发布版本导出到本次运行目录的 `exports/windows/train.exe`，空用户目录与有效 Mod 用户目录的两次 `--smoke` 运行共用该文件。资产导出探针的范围、产物保留和环境隔离见[统一测试入口](run-tests.md#选择)。

统一入口和测量导出入口在主游戏导出后共用程序集优化检查：读取 DLL 元数据并跳过原生 DLL，任何托管 DLL 带 `DisableOptimizations`，或缺少 Train／Core／Content／Scenes 程序集，均判失败；结果分别保留在独立检查日志和测量导出步骤中。

需要直接验证已有构建时，通过 `-ExecutablePath` 传入绝对路径：

```powershell
pwsh -NoProfile -File scripts/checks/run-export-smoke.ps1 -ExecutablePath 'D:/train-builds/train.exe' -OutputDirectory 'D:/train-checks/smoke'
```

直接调用只验证已有二进制，不证明它来自当前工作树；对应的构建提交和导出记录需要另外保留。

Verify Windows runtime behavior on Windows and record the build's tested commit, runtime results and logs. If Windows execution is unavailable, report runtime as unverified even when export succeeds.

## Linux 验证与自托管 runner

Linux 同样使用 `global.json` 固定的 .NET SDK 10.0.112、PowerShell 7.6.6 和 Godot 4.7.2 stable .NET **官方 Linux x86_64 构建**：从[官方发布](https://github.com/godotengine/godot/releases/tag/4.7.2-stable)下载 `Godot_v4.7.2-stable_mono_linux_x86_64.zip`，按同一发布的 `SHA512-SUMS.txt` 核对后安装；版本输出应含 `official`。Schema 使用 Python 3 与 `jsonschema==4.25.1`。在仓库目录核对 `dotnet --version`、`pwsh --version`、`"$GODOT" --headless --version`、`python3 --version` 和 `python -c "import jsonschema, referencing"`。当前 Schema 入口调用 `python`，因此提供 python3 的虚拟环境也须在 PATH 上提供 `python`。

局域网夜间 runner `lan-linux-nightly` 由 systemd 服务运行。主机提供 PowerShell、Godot 和 Python：`.env` 提供不含参数的 `GODOT` 可执行文件路径，`.path` 将 pwsh 与 Python 虚拟环境加入服务 PATH。SDK 由 job 按 `global.json` 准备，其安装目录和 `DOTNET_ROOT` 由工作流设置，见[Linux CI](ci.md#linux-夜间运行)。主机工具的安装位置不写入 workflow；变更主机环境后重启 runner 服务，让新进程取得配置，并在 Actions 的环境记录中核对实际版本及 SDK 来源。

```bash
dotnet tool restore
pwsh -NoProfile -File scripts/test.ps1 -All -Layer all,stage
```

Linux 执行全部快速、集成、headless engine 和非 Windows 阶段检查，平台范围见[统一测试入口](run-tests.md#阶段检查与候选基准)。当前检查不需要渲染上下文，因此不使用 xvfb；headless Godot 使用 Dummy 音频。上述配置和命令是运行要求，执行证据以各次摘要、日志和 Issue 评论为准。

For Windows cross-export, verify a writable Windows mount and choose a dedicated output directory. For example, if `/mnt/windows-d` is mounted:

```bash
windows_build_dir='/mnt/windows-d/train-builds/local'
mkdir -p "$windows_build_dir"
"$GODOT" --headless --path . --export-release "Windows Desktop" "$windows_build_dir/train.exe"
```

A mounted Windows filesystem provides file access, not a Windows execution environment, so runtime validation still requires Windows.

Historical records in `docs/research/` and `archive/mvp/` retain the OS, paths, commands and results used at the time.
