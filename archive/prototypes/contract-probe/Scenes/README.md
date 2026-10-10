# Scenes 契约运行原型（#292，可抛弃）

执行：`pwsh -NoProfile -File archive/prototypes/contract-probe/Scenes/run.ps1`。没有测试包、Godot 或真实实现依赖。runner 将显式输入复制到 `TestResults/run-tests/contract-probe-292/scenes/isolated-<uuid>/`，重新构建并记录输入 SHA-256、MSBuild 求值项目和程序集引用、加载程序集路径、命令及原始日志。检查所有第一方解析路径必须来自隔离副本，只允许三个契约；输出仅允许本程序及三个契约 DLL。证据不提交。

使用原样 `prototypes/scenes-contract/ConsumerExamples.cs` 的 `WorldConsumer`、`EditorConsumer` 和 `DesignIconConsumer`，没有改写这些消费者。`WorldScript`、`EditScript`、`ReadOnlyPreview` 为有限固定情景替身；公开输入接口通过只注册明确成员的 DispatchProxy 供给。代理仅实现公开接口，不反射生产内部类型、字段或源码。未知成员/操作/顺序抛出 `UNCONFIGURED`，对应自检实际执行。支持的编辑草稿仅为本文件固定 SceneModel/Assembly 值，固定内容结果带单格编号/占格定位及空嵌套对象投影，空嵌套副本明确记为不合法；结构比较覆盖该情景全部字段和递归集合，其他类别明确拒绝，不是设计计算器。

| 断言前缀 | 责任与固定情景 |
| --- | --- |
| `world.consumer` | 场景样例消费者：公开工厂打开；固定非空有序已提交事件原样交入 Observe 后读取帧，保留事件身份、位置和 Ordinal；同实际显示帧拾取；故意错误 Base 后请求完整替换；适配安装失败及 `Success(Readiness.Failed)` 不发布交互帧 |
| `world.producer` | 场景替身局部承诺：请求变更后旧命中明确拒绝；关闭后原帧及嵌套网格仍可保留 |
| `edit.consumer` | 编辑样例消费者：公开工厂打开；配置成功整笔修订；锁定预览拒绝；Evaluate 返回前改变草稿时保持原请求 stamp；保存失败不标记、候选释放；提交期间后续编辑保持脏状态 |
| `edit.producer` | 编辑替身局部承诺：绕过 UI 直接 Apply 仍拒绝锁；迟到/异会话/关闭评估和保存不污染；独立集合中相同内容可接受、根字段或嵌套子设计不同则拒绝；旧递归草稿保留 |
| `preview.consumer` | 图标样例：只调用 PreviewDesign，没有编辑入口；使用后释放预览 |
| `*.fake` | 替身自身：未配置 Present、Preview、内容工厂成员明确失败 |
| `async.coordinator` | 独立小协调情景：取消 A，再打开同 UUID 但不同绑定的 B，A 最后完成；B 身份/未保存标记不变，只释放 A。没有真实宿主代码，不能算 Godot 消费者通过 |

正确版本后，runner **只在隔离副本**将迟到结果接入判断改为始终接受，保持原断言；必须以 `ASSERT: async.coordinator...` 失败，工具或编译失败不算检出。恢复原源码，再运行同一检查通过。输入原版本、破坏源码、三组构建/运行日志分别保留。错误 Base 帧属于明确标注的违规提供者防御情景，不参与合规替身通过结论。

本原型不生成网格，不做拾取几何、布局编辑、设计合法性、撤销重做、真实保存、资源/GPU操作。编辑评估和保存的来源由消费代码传递固定快照，本原型只校验本情景；完整设计类别、内容世代变更、普通推进/删除对象、真实体素后台任务退出及真实宿主候选资源退役仍待对应真实实现验证。没有生产实现的结果不能由替身补成通过。EditorConsumer.Present 没有自己的 Base 回退，但显示适配器按既有契约必须拒绝 Base 失配，因此不能仅凭这个差异认定消费样例错误。
