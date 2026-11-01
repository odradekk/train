#!/usr/bin/env bash
# 逐个场景加载，报告页面上出现的脚本错误：./check.sh（不带参数检查全部场景，也可以传 _selftest 等自检场景）
EDGE="/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe"
DIR="$(cd "$(dirname "$0")" && pwd -W)"
list=${*:-$(grep -oE "^  '?[a-z-]+'?: \[" "$DIR/js/scenarios.js" | grep -oE "[a-z-]+")}
for s in $list; do
  "$EDGE" --headless=new --disable-gpu --virtual-time-budget=1200 --dump-dom "file:///$DIR/index.html#$s" 2>/dev/null | grep -o "STEPERR[^<]*\|JS 错误：[^$][^<]*\|自检通过" | sed "s/^/$s: /"
done
echo "checked: $(echo $list | wc -w) scenarios"
