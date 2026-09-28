# `observe`：查询 Playground 观察状态与选中对象

[返回命令索引](README.md) · [Playground](README.md#playground具名固定配置) · [结果与错误处理](README.md#结果与错误处理)

```text
observe
```

参数：无。只在 Playground 中可用；其他场景输出 `Enter the Playground before using observe commands.`（`warning`）。

输出按行分为：

- `mode free|focus|follow[ target <对象>] selection <对象|none> transitioning true|false`：当前镜头模式、聚焦/跟随目标、选中对象，以及是否仍在平滑过渡。
- `camera <x,y,z> look <x,y,z>`：当前镜头位置与注视点。
- `notice ...`：最近一次失效提示。`selection-lost <对象>` 表示选中对象已不存在；`target-lost <对象> mode <focus|follow> camera free-in-place` 表示聚焦或跟随目标已彻底销毁（普通体素格被正式修改移除后即算销毁，同定义重建的是新体素），引用已清除，镜头停在原位并转为自由观察。整个世界被 [`voxel reset`](voxel-reset.md) / [`composite reset`](composite-reset.md) 或成功的 [`voxel load`](voxel-load.md) 替换时，旧选择与目标一律清除（即使新世界复用了同一车辆 ID 或同一格），两种提示末尾追加 `world-replaced reset|load` 说明原因；加载失败时观察状态不变。
- 选中对象存在时，追加该对象的检查行：`identity`（身份）、`owner`（所属编组 / 线路 / 世界）、`durability`（耐久）、`eligibility`（运行资格）、`failures`（正式状态中已有的失败原因，无则 `none`）。

对象写作 `vehicle <id>`、`segment <id>` 或 `cell <x>,<y>,<z>`。车辆的 `owner` 为 `assembly <id> position <i>/<n>[ active-locomotive]` 或 `no assembly`；`eligibility` 为运行条件加 `start <状态>`（按与 `rail start` 相同的预检，只读不入队）或 `running`；`failures` 列出停车原因、启动拒绝原因、缺活动车头、构造或身份问题与会话故障。轨段给出占格完整度与可通行区间，世界格给出耐久、所属轨段或脱落 RuntimeId 与碰撞。

本命令只读：不暂停、不推进、不改世界、车辆、铁路或存档。观察状态不写入存档，也不进入命令编号或模拟步。

相关：[observe list](observe-list.md) · [observe select](observe-select.md) · [observe focus](observe-focus.md) · [observe follow](observe-follow.md) · [observe free](observe-free.md)
