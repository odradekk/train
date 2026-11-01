#!/usr/bin/env bash
# 用无头 Edge 截取指定场景：./shoot.sh 前缀 场景1 场景2 ...
EDGE="/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe"
DIR="$(cd "$(dirname "$0")" && pwd -W)"
prefix=$1; shift
for s in "$@"; do
  "$EDGE" --headless=new --disable-gpu --hide-scrollbars --window-size=1920,1080 --virtual-time-budget=1500 \
    --screenshot="$DIR/shots/$prefix-$s.png" "file:///$DIR/index.html#$s" >/dev/null 2>&1
done
