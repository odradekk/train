# Local validation and Windows builds

Run commands from the checkout under test. The current development host is Windows; the export target is `Windows Desktop` in `export_presets.cfg`. Which checks to run is decided by the [Testing standard](implement-sop.md#testing-standard).

## Windows development and validation

Use PowerShell 7 (`pwsh`), not Windows PowerShell 5 (`powershell.exe`). Both projects target `net10.0`; install the exact x64 SDK selected by `global.json` from Microsoft's [.NET 10 downloads](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). Roll-forward is disabled, so another .NET 10 SDK, including a preview, does not satisfy the requirement; other SDKs can stay installed alongside it.

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

`GODOT` must contain an executable path without arguments; prefer the console executable for command-line checks. Godot's C# tooling finds `dotnet` only through the Godot process's PATH; if Godot reports `.NET Sdk not found` or `Cannot find the dotnet executable`, check that this PATH contains `C:\Program Files\dotnet\`. On this host, Paseo adds a PATH entry inside the `app.asar` file, and Git Bash drops that entry and every later one when it passes PATH to Windows programs; `~/.bashrc` rewrites the entry to `app.asar.unpacked`. Restart existing terminals and editors after installing the SDK or changing user environment variables. Report missing prerequisites as validation blockers.

The checkout may retain ignored settings from a historical acceptance environment. Before returning it to normal development, inspect `NuGet.Config` for an obsolete offline-only source and `override.cfg` for a custom acceptance user-data directory. Back up those local files outside the checkout before disabling them; preserve the associated acceptance evidence and saves. Normal dependency restore needs nuget.org access, and normal game data uses `%APPDATA%\Godot\app_userdata\Train`.

For a fresh checkout or after an SDK change, build and import before Godot checks. Resource import alone does not build the C# project. These are entry examples, not a mandatory sequence; unfiltered `dotnet test` runs only the MSTest project, not all repository correctness checks, which run through the [unified test entry](run-tests.md):

```powershell
dotnet build Train.csproj
& $env:GODOT --headless --path . --editor --import
dotnet test Train.Tests/Train.Tests.csproj
& $env:GODOT --path . --editor   # open the editor after a successful build/import
```

VS Code uses the repository's `build`, `test` and `export-windows` tasks. With the C# extension installed, the `Train (Godot)` launch configuration builds the solution and starts the game. `GODOT_EDITOR` points to the main Godot executable for debugging; `GODOT` points to its console wrapper for validation output. This follows the [Godot C# editor setup](https://docs.godotengine.org/en/4.7/tutorials/scripting/c_sharp/c_sharp_basics.html#visual-studio-code).

The four `scripts/run-godot-*.tests.ps1` harnesses reuse the current PowerShell 7 host and select fake native programs for the host OS; their Windows GUI-process capture cases run only on Windows and are skipped on Fedora. Report harness results separately from real Godot checks.

## Windows export and runtime validation

Export requires the project's .NET SDK and matching Godot .NET Windows export templates, on this machine under `%APPDATA%\Godot\export_templates\4.7.2.stable.mono`. Use a dedicated output directory; on Windows the content export smoke validates both export and the resulting executable:

```powershell
$windowsBuild = Join-Path (Get-Location).Path 'builds/windows-content-smoke/train.exe'
pwsh -NoProfile -File scripts/run-godot-content-export-smoke.ps1 -OutputPath $windowsBuild
$env:TRAIN_MODS_EXPORT_BIN = $windowsBuild
pwsh -NoProfile -File scripts/run-godot-mods-export-smoke.ps1
Remove-Item Env:TRAIN_MODS_EXPORT_BIN
```

Use an absolute executable path for Windows runtime checks; the process launcher may fail to resolve a relative path containing forward slashes even after export succeeds. Passing the output path on the command line keeps machine-specific paths out of `export_presets.cfg`. Keep the complete export directory, including the PCK and .NET output, together when running or copying the build.

For the content/mods export smoke scripts, `-OutputPath` selects a fresh export destination and `-ExportOnly` stops after verifying that export completed and the executable exists. To run against an existing native build for the current host, set `TRAIN_CONTENT_EXPORT_BIN` or `TRAIN_MODS_EXPORT_BIN` to its executable and run the script without `-ExportOnly`; `-ExportOnly` cannot validate such a binary.

Verify Windows runtime behavior on Windows and record the build's tested commit, runtime results and logs. If Windows execution is unavailable, report runtime as unverified even when export succeeds.

## Fedora validation and cross-export

On Fedora, install the SDK required by `global.json`, PowerShell 7 and Godot .NET, and check `dotnet --version`, `pwsh --version` and `godot-dotnet --headless --version`. Set the executable paths, then run the applicable commands above with `"$GODOT"` in place of `& $env:GODOT`:

```bash
export GODOT="$(command -v godot-dotnet)"
export GODOT_EDITOR="$GODOT"
```

For Windows cross-export, verify a writable Windows mount and choose a dedicated output directory. For example, if `/mnt/windows-d` is mounted:

```bash
windows_build_dir='/mnt/windows-d/train-builds/local'
mkdir -p "$windows_build_dir"
pwsh -NoProfile -File scripts/run-godot-content-export-smoke.ps1 -ExportOnly -OutputPath "$windows_build_dir/train.exe"
```

A mounted Windows filesystem provides file access, not a Windows execution environment, so runtime validation still requires Windows.

Historical records in `docs/validation/`, `docs/research/` and `archive/mvp/` retain the OS, paths, commands and results used at the time. New validation records must describe the environment actually used.
