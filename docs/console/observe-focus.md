# `observe focus`：聚焦选中对象

[返回命令索引](README.md) · [Playground](README.md#playground具名固定配置)

```text
observe focus
```

参数：无。把镜头平滑转向当前选中对象的中心（车辆取显示车体中心，轨段取定位线中点，世界格取格心），保持当前观察方向，距离限制在 4–80 格。聚焦点只在执行时取一次，之后不随车辆移动；要持续跟随车辆用 [observe follow](observe-follow.md)。

输出 `Focused mode focus target <对象> ...`。没有选择时输出 `NoSelection ...`，镜头与模式不变。聚焦中右键拖动绕目标旋转（松开被截获时同 [observe free](observe-free.md) 结束拖动）、滚轮改变距离。目标被彻底摧毁，或所聚焦格中的原体素被正式修改移除（即使随后以同定义重建）时，镜头停在原位转为自由观察，并在 [observe](observe.md) 与面板中提示 `target-lost`。

Playground 画面快捷键 `F`、右侧面板“聚焦选中”按钮与本命令等价。本命令不暂停、不推进、不修改会话。
