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

For a fresh checkout or after an SDK change, build and import before Godot checks. Resource import alone does not build the C# project. These are entry examples, not a mandatory sequence; unfiltered `dotnet test` runs the MSTest projects in the solution, not all repository correctness checks, which run through the [unified test entry](run-tests.md):

```powershell
dotnet build Train.csproj
& $env:GODOT --headless --path . --editor --import
pwsh -NoProfile -File scripts/test.ps1 -Layer fast
& $env:GODOT --path . --editor   # open the editor after a successful build/import
```

VS Code uses the repository's `build`, `test` and `export-windows` tasks. `test` selects the unified entry's `fast` layer; `export-windows` selects resource import, all four engine checks, Windows export and both export runtime checks. With the C# extension installed, the `Train (Godot)` launch configuration builds the solution and starts the game. `GODOT_EDITOR` points to the main Godot executable for debugging; `GODOT` points to its console wrapper for validation output. This follows the [Godot C# editor setup](https://docs.godotengine.org/en/4.7/tutorials/scripting/c_sharp/c_sharp_basics.html#visual-studio-code).

`scripts/checks/run-app-check.tests.ps1` 使用当前 PowerShell 7 和同目录的测试进程夹具，验证自检脚本的参数、标记及退出码；Windows GUI 进程捕获用例只在 Windows 执行。这些脚本测试与真实 Godot 检查分别报告。

当前格式内容示例的 Schema 检查还需要可通过 `python` 调用的 Python 3 与 `jsonschema`。CI 使用 Python 3.11 和 `jsonschema==4.25.1`；本地可用 `python -m pip install jsonschema==4.25.1` 安装，并用 `python -c "import jsonschema, referencing"` 验证。未指定 `-Project` 的 `-Layer integration` 和 `-Layer all` 包含此检查；缺少工具或包时入口判失败并返回退出码 1。

## Windows export and runtime validation

Export requires the project's .NET SDK and matching Godot .NET Windows export templates, on this machine under `%APPDATA%\Godot\export_templates\4.7.2.stable.mono`. 在 Windows 通过统一入口选择两项导出检查：

```powershell
pwsh -NoProfile -File scripts/test.ps1 -Layer engine -ExportWindows
```

Use an absolute executable path for Windows runtime checks; the process launcher may fail to resolve a relative path containing forward slashes even after export succeeds. Passing the output path on the command line keeps machine-specific paths out of `export_presets.cfg`. Keep the complete export directory, including the PCK and .NET output, together when running or copying the build.

统一入口将 Windows 发布版本导出到本次运行目录的 `exports/windows/train.exe`，两项检查共用该文件，运行时各用独立用户目录。通过后导出产物随运行目录内其他证据删除，只保留摘要；失败时保留完整导出目录。导入和 App 检查隔离 Windows 的 APPDATA、LOCALAPPDATA，或非 Windows 的 XDG_CONFIG_HOME、XDG_DATA_HOME、XDG_CACHE_HOME；只有导出步骤保留真实 APPDATA 以访问模板，通过 `--recovery-mode` 跳过编辑器插件，并把 Godot 原生日志定向本次运行目录。运行时检查也显式定向原生日志；正常用户目录快照变化时，统一入口返回非零。摘要分别记录导出准备与两个运行时检查的结果。

需要直接验证已有构建时，通过 `-ExecutablePath` 传入绝对路径：

```powershell
pwsh -NoProfile -File scripts/checks/run-app-check.ps1 -Check content-export -ExecutablePath 'D:/train-builds/train.exe' -OutputDirectory 'D:/train-checks/content'
pwsh -NoProfile -File scripts/checks/run-app-check.ps1 -Check mods-export -ExecutablePath 'D:/train-builds/train.exe' -OutputDirectory 'D:/train-checks/mods'
```

直接调用只验证已有二进制，不证明它来自当前工作树；对应的构建提交和导出记录需要另外保留。

Verify Windows runtime behavior on Windows and record the build's tested commit, runtime results and logs. If Windows execution is unavailable, report runtime as unverified even when export succeeds.

## Fedora validation and cross-export

入口尚未在 Linux 上实际运行，Linux 运行由 [#407](https://github.com/odradekk/train/issues/407)建立。以下是环境配置说明，不是执行证据。

On Fedora, install the SDK required by `global.json`, PowerShell 7 and Godot .NET, and check `dotnet --version`, `pwsh --version` and `godot-dotnet --headless --version`. Set the executable paths, then run the applicable commands above with `"$GODOT"` in place of `& $env:GODOT`:

```bash
export GODOT="$(command -v godot-dotnet)"
export GODOT_EDITOR="$GODOT"
```

For Windows cross-export, verify a writable Windows mount and choose a dedicated output directory. For example, if `/mnt/windows-d` is mounted:

```bash
windows_build_dir='/mnt/windows-d/train-builds/local'
mkdir -p "$windows_build_dir"
"$GODOT" --headless --path . --export-release "Windows Desktop" "$windows_build_dir/train.exe"
```

A mounted Windows filesystem provides file access, not a Windows execution environment, so runtime validation still requires Windows.

Historical records in `docs/research/` and `archive/mvp/` retain the OS, paths, commands and results used at the time.
