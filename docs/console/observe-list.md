# `observe list`：列出可观察对象

[返回命令索引](README.md) · [世界编辑器](README.md#世界编辑器具名固定配置)

```text
observe list
```

参数：无。按车辆、轨段顺序每行输出一个对象：`vehicle <id> · 车辆 <id> <定义> · 编组 <id>|无编组 · <运行条件>`，`segment <id> · 轨段 <id> x <起点>..<终点>`。世界中没有车辆或轨段时输出 `objects none`。

本命令始终列出整体车辆和轨段。右侧对象列表在整体层级使用同一查询，在显式进入车辆零件层级后仅列出该车零件。世界格不列出，用 [observe select](observe-select.md) 的 `cell` 形式或在画面中点击选择。本命令只读，不改变选择与镜头。
