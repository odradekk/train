# 内容示例检查：调用文档已有的 JSON Schema 校验器。
# 统一入口负责收集本脚本的退出码和日志。
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/../..").Path
$python = (Get-Command python -ErrorAction Stop).Source
# 固定本次 Python 标准输出为 UTF-8，覆盖 Windows runner 或调用方的旧编码。
$env:PYTHONIOENCODING = 'utf-8'
& $python -X utf8 (Join-Path $root 'docs/content/tools/validate_examples.py')
exit $LASTEXITCODE
