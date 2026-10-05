# Split the code into Godot-free core and scene libraries and rewrite the core

2026-10-05 经[代码结构与旧代码去留](https://github.com/odradekk/train/issues/213)确认，属于地图[代码重构：为首个切片“宏观贸易”建立实施工单](https://github.com/odradekk/train/issues/218)。

代码分成三个项目，依赖方向与 [ADR-0008](0008-simulate-the-world-in-a-voxel-free-core-and-build-voxel-scenes-as-presentation.md) 的分层一致：

- `Train.Core`（`Core/`）：内容层、核心模拟层、存档与录制，以及日志、控制台分发这类公用代码。项目名里的 Core 不等于核心模拟层，内容层也在这个项目里；核心模拟层只通过核心字段接口读取定义和设计属性，见 [02](../design/02-definitions-and-content.md)。
- `Train.Scenes`（`Scenes/`）：体素引擎层，引用 `Train.Core`。
- `Train`（仓库根目录的 Godot 项目）：渲染层、界面和启动，引用上面两个项目。

`Train.Core` 和 `Train.Scenes` 不引用 Godot。核心层看不到场景，两个库看不到 Godot，这两条由项目引用保证，不靠代码审查。

核心模拟层按新设计重写，不在旧代码上改造。旧代码以体素世界为模拟核心，模拟会话把体素世界、车辆、铁路、命令和存档放在一个对象里，而新设计取消了其中大部分；按提交 `6255461` 盘点，能原样或小改后使用的约占 14%。保留的是内容和 Mod 系统、体素定义、定步长时钟、行为注册、SplitMix64、日志、控制台分发和存档文件读写。其余旧代码在第一张实施工单中一次删除，删除前在当时的提交上打带注释的标签 `pre-core-rewrite`，新写时通过这个标签参照旧实现。旧代码不移入 `archive/`，那里保存的是已验证玩法的证据。

我们放弃了：

- 维持一个 Godot 项目，测试通过链接源文件编译。这样核心层不依赖场景只能靠审查。
- 内容层单独成为一个项目。02 已规定核心层只通过核心字段接口读取定义，这条在同一个项目里就能守住，多一个项目只多一份维护。
- 在旧代码上逐步改造，或在替代完成前保持旧功能可用。拆开旧会话比新写更费事，而且旧切片和世界编辑器在新设计里没有替代品。

## Consequences

- 命名空间等于“项目名.目录名”。`Core/` 的第一层目录按领域划分：`Content/`、`Simulation/`、`World/`、`Railway/`、`Trade/`、`Saves/`、`Recording/`、`Logging/`、`Console/`。Godot 项目分 `App/`、`Rendering/`、`Ui/`。`Train.Tests` 用项目引用测试两个库，分 `Core/`、`Scenes/` 两个子目录。更深的结构由实施工单决定。
- 首个切片中，核心模拟层在主线程上由 `App` 按定步长推进。查询返回不可变的值，不把可修改的内部对象交给外层；场景在后台线程构建，使用同样不可变的地点快照。以后如果要把核心层移到工作线程，外层不用改。
- 过渡期不要求旧功能可用，每次合并只要求能构建、保留的测试通过。
