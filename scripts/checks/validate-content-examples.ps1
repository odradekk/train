$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/../..").Path
$python = (Get-Command python -ErrorAction Stop).Source
# 固定本次 Python 标准输出为 UTF-8，覆盖 Windows runner 或调用方的旧编码。
$env:PYTHONIOENCODING = 'utf-8'
& $python -X utf8 (Join-Path $root 'docs/content/tools/validate_examples.py')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
# 共享符号模块不在源码目录写入字节码，保持验证入口的工作区快照不变。
& $python -B -X utf8 (Join-Path $root 'scripts/content/generate_small_world.py') --check
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $python -X utf8 (Join-Path $root 'scripts/content/ui_symbols.py') --check
exit $LASTEXITCODE
