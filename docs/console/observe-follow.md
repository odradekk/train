# `observe follow`：跟随选中车辆

[返回命令索引](README.md) · [世界编辑器](README.md#世界编辑器具名固定配置)

```text
observe follow
```

参数：无。选中对象必须是车辆。镜头平滑过渡后持续跟随该车：注视点是车体的显示中心，观察方向 = 车体显示朝向 + 执行时的相对偏移。车体朝向由该车与 [`rail query display`](rail-query.md) 同一显示姿态下的前/后接触锚点推出，不用速度方向、不读跳动的权威步位置；暂停时镜头不动，运行时随插值连续移动。右键拖动改变相对偏移（松开被截获时同 [observe free](observe-free.md) 结束拖动），滚轮改变距离（4–80 格）。

输出 `Following mode follow target vehicle <id> ...`。没有选择为 `NoSelection ...`；选中轨段或世界格为 `NotVehicle ...`，模式不变。

编组拆分或活动车头变化时，只要该车仍在会话中就继续跟随同一辆车，不换到前车或活动车头。该车被彻底摧毁时清除选择与跟随引用，镜头停在原位转为自由观察，提示 `target-lost vehicle <id> mode follow camera free-in-place`，不跳到其他车辆。整个世界被重置或成功读档替换时同样清除（即使新世界中同一 ID 的车辆存在），提示末尾追加 `world-replaced reset|load`；加载失败时继续跟随。

世界编辑器画面快捷键 `V`、右侧面板“跟随选中”按钮与本命令等价。世界编辑器镜头不读 [`rail follow`](rail-follow.md) 的会话跟随状态；后者仍服务切片 4 场景。本命令不暂停、不推进、不修改会话，也不写入存档。
