# `observe list`：列出可观察对象

[返回命令索引](README.md) · [Playground](README.md#playground具名固定配置)

```text
observe list
```

参数：无。按车辆、轨段顺序每行输出一个对象：`vehicle <id> · 车辆 <id> <定义> · 编组 <id>|无编组 · <运行条件>`，`segment <id> · 轨段 <id> x <起点>..<终点>`。世界中没有车辆或轨段时输出 `objects none`。

列表与 Playground 右侧对象列表读同一份正式查询。世界格不列出，用 [observe select](observe-select.md) 的 `cell` 形式或在画面中点击选择。本命令只读，不改变选择与镜头。
