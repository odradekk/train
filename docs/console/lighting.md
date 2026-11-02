# `lighting`：检查光照（仅 Debug）

[返回命令索引](README.md)

```text
lighting
lighting daylight
lighting overcast
lighting night
lighting restore
```

无参数查询当前模式；其余形式切换检查光照。命令和参数不区分大小写，只接受一个已列出的参数，多余或未知参数报告用法。

| 参数 | 效果 |
| --- | --- |
| `daylight` | 中性日光，沿用游戏当前固定白天的环境、天空和太阳参数 |
| `overcast` | 阴天，关闭直射阳光，使用柔和均匀的环境光 |
| `night` | 夜间人工照明，暗天空，暖色工作灯围绕相机观察目标并随目标移动 |
| `restore` | 恢复游戏光照；本阶段固定白天，画面与 `daylight` 相同 |

这些是[资产检查条件](../art/05-lighting-and-readability.md#检查光照)，同步应用到世界视图、设计编辑器和创作模式的模板预览。只改变环境和灯光，不改变模拟、暂停、倍速、存档或时段，也不修改材质。模式在当前 App 生命周期内保留；返回地图、进入编辑器或模板预览仍保留，重新启动默认使用游戏光照。

世界视图存在时，即使尚未进入场景，也可以选择模式，进入后生效。没有世界视图时，提示先创建或读取存档后重试。默认 App 已通过真实工厂创建世界视图；底栏只在游戏会话中可打开，不能从主菜单、设计编辑器或创作模式输入命令。手动线路预览使用真实 Scenes 生成和 Godot 渲染、固定 Core/Content 输入，入口见[测试说明](../run-tests.md)。

构建许可与 `reload`、`sim` 一致，使用 `OS.IsDebugBuild()`：编辑器内运行和 debug 模板导出均可用；release 模板导出的玩家构建不注册该命令，`help` 不列出，输入时按未知命令处理。

查询和切换通过 `console` 分类的 `lighting_mode` 事件报告，`mode` 为 `game`、`daylight`、`overcast` 或 `night`。`game` 是恢复后的查询结果，不是输入参数。非法参数和没有视图分别记录 `lighting_usage` 和 `lighting_unavailable`。结果受日志等级过滤；查看模式前可执行 `loglevel info`。
