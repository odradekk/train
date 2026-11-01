# 导航栏「设计」的快捷键改为 K

Status: accepted
Supersedes in part: 2026-10-31-docked-game-shell — 游戏画面快捷键里的「D 设计」

## Problem

Decision note: 2026-10-31-docked-game-shell 列出的游戏画面快捷键里，D 既打开「设计」，又是 WASD 平移地图里向右的那个键。一个键不能同时做两件事：原型里按 D 只会打开设计，宏观地图没法用键盘向右平移。

## Proposal

导航栏「设计」的快捷键改为 K，取「蓝图库」的「库」，和 J 取「聚居地」的「聚」一样用拼音首字母。WASD 只用来平移宏观地图。

这一条替换 Decision note: 2026-10-31-docked-game-shell「全局交互规则」一节快捷键里的「D 设计」，那一节的其他快捷键不变。

## Alternatives considered

**宏观地图改用方向键平移，D 留给设计。** 局部场景的镜头用 WASD 平移，宏观地图换成方向键以后，两处的操作就不一样了。

## Acceptance criteria

- 在游戏画面按 K 打开蓝图库工作区，按 D 让宏观地图向右平移。
- 导航栏「设计」的提示里写着 K。

## Risks

- K 不如 D 好记；所有快捷键都可以在设置里改。
