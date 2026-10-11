# 内容示例检查：调用文档已有的 JSON Schema 校验器。
# 统一入口负责收集本脚本的退出码和日志。
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/../..").Path
$python = (Get-Command python -ErrorAction Stop).Source
& $python (Join-Path $root 'docs/content/tools/validate_examples.py')
exit $LASTEXITCODE
