# `observe select`：选择或清除观察对象

[返回命令索引](README.md) · [Playground](README.md#playground具名固定配置)

```text
observe select vehicle <id>
observe select segment <id>
observe select cell <x> <y> <z>
observe select none
```

| 参数 | 说明 |
| --- | --- |
| `vehicle <id>` | 当前会话中存在的车辆运行时 ID |
| `segment <id>` | 当前正式铁路中的轨段 ID |
| `cell <x> <y> <z>` | 三个十进制整数世界坐标；该格必须有体素。脱落零件会记住其 RuntimeId，格子被移除或换成别的体素后选择即失效 |
| `none` | 清除选择 |

成功输出 `Selected <对象>`，随后同 [observe](observe.md) 输出当前模式与检查行；`none` 输出 `Cleared mode ...`。对象不存在输出 `UnknownObject <对象>`，原选择不变。ID 非正整数、坐标不是整数或参数个数不对时输出 `Usage: observe ...`。

选择只改变检查对象，不移动镜头、不改变当前聚焦/跟随目标；要让镜头处理它，再执行 [observe focus](observe-focus.md) 或 [observe follow](observe-follow.md)。Playground 画面中左键点击车辆或世界体素（控制台关闭、未点在面板上时）、或点击右侧对象列表，走同一选择入口。选中对象被摧毁后，下次查询或显示帧清除选择并提示 `selection-lost`。
