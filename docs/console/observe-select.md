# `observe select`：选择或清除观察对象

[返回命令索引](README.md) · [世界编辑器](README.md#世界编辑器具名固定配置)

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
| `cell <x> <y> <z>` | 三个十进制整数世界坐标；该格必须有体素。选择记住该格体素的定义和脱落零件的 RuntimeId。每条正式修改执行提交后都会核对：格子被移除（即使同一帧或同一步随后以同定义重建）、换成不同定义或不同 RuntimeId 的体素后选择及以它为目标的聚焦即失效，重建的新体素不会被自动选中；故障回滚的修改不影响选择。整个世界被重置或成功读档替换时，任何选择（含车辆、轨段）都会清除 |
| `none` | 清除选择 |

成功输出 `Selected <对象>`，随后同 [observe](observe.md) 输出当前模式与检查行；`none` 输出 `Cleared mode ...`。对象不存在输出 `UnknownObject <对象>`，原选择不变。ID 非正整数、坐标不是整数或参数个数不对时输出 `Usage: observe ...`。

选择只改变检查对象，不移动镜头、不改变当前聚焦/跟随目标；要让镜头处理它，再执行 [observe focus](observe-focus.md) 或 [observe follow](observe-follow.md)。世界编辑器画面中左键点击车辆或世界体素（控制台关闭、未点在面板上时）、或点击右侧对象列表，走同一选择入口。选中对象被摧毁后，下次查询或显示帧清除选择并提示 `selection-lost`。
