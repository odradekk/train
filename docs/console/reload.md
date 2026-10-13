# `reload`：重新加载内容

[返回命令索引](README.md)

```text
reload
```

`reload` 把请求交给宿主的内容重载回调。它没有路径、包名或定义 ID 参数；附带 token 会忽略。发布构建执行时会报告该命令仅在 Debug 构建可用。

默认 App 通过真实内容工厂与 `HostCoordinator` 重载。启动时发现并启用当时安装的有效 Mod，重载重新读取这组启用包；运行中新安装的 Mod 需重启后发现。旧内容加载路径已在 [#452](https://github.com/odradekk/train/issues/452) 退役，当前入口见[内容契约](../contracts/content.md)。

基础分发器只调用宿主回调；重载结果和失败原因由内容日志报告，命令不另行输出成功消息。Debug 构建也可以按 Ctrl+Shift+R 发起同样的重载；发布构建不处理该快捷键。

宿主后台准备固定候选，协调线程在帧开始接入结果。准备期间技术操作锁阻止修改操作，再次请求重载返回 `busy`，查询和取消仍可用。成功、失败或取消后恢复原运行设置，不补算等待期间的时间；冻结来源不会被解锁解除。

宿主的重放会话拒绝 `reload`，继续使用复现包捕获的原内容历史，不把当前安装目录中的内容接入重放。需要修改时先从 [`dev`](dev.md) 导出当前完整边界，再通过普通读档打开副本。

| 候选与核心结果 | 宿主处理 |
| --- | --- |
| 活动核心存在，且 `CoreInput` 变化 | 同步提交 `PublishContent`，只有 `Accepted` 才激活候选，并在分发事件前配对内容与聚居地模板副本 |
| `Rejected` 或 `NotAccepted` | 保留旧内容和旧世代，不激活候选 |
| 核心 `Faulted` | 保留最后完整边界并冻结，不激活候选；新故障只记一次 |
| 纯表现变化，不含 `CoreInput` | 发布新内容视图，不提交核心命令 |
| `ContentChange.None` 且已有内容 | 保留旧视图，不激活候选 |
| 菜单状态，没有活动核心 | 只激活内容，不创建核心会话或提交命令 |

日志沿用 `content` 分类和已有 `content_reload_summary`、`content_reload_failed` 事件 ID。摘要使用 `operation_id`、`status`、`changed`、活动 `generation`、`warning_count`、`error_count` 和可选 `code`，不伪造扫描数或定义数。核心拒绝问题保留在 `problems` 的 `code`、`field`、`subject` 中。准备报告的诊断用 `content_invalid`；失败诊断包含 `code`、`stage`、`field`、`source`，内部内容故障包含 `code`、`diagnostic_stack_trace`。核心故障按 `CoreFault`／`CoreDiagnostic` 记录，字段见 [`sim`](sim.md)。取消及迟到候选只清理，不输出发布成功摘要。

每条内容诊断只记一次 `content_invalid`；一次重载结束只记一次 `content_reload_failed`，并附结果摘要。捕获失败按重载失败报告，准备阶段异常和内容内部故障的状态为 `Faulted`。新局和读档准备失败分别使用 `application/host_new_game_failed`、`application/host_restore_failed`，不记成重载失败。
