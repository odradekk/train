# `loglevel`：查询或设置日志等级

[返回命令索引](README.md)

```text
loglevel
loglevel debug
loglevel info
loglevel warning
loglevel error
```

| 参数 | 是否必填 | 含义 |
| --- | --- | --- |
| 等级 | 否 | 不填时查询当前等级；填写时只允许 `debug`、`info`、`warning`、`error` 中的一个 |

过滤等级从低到高为 `debug` → `info` → `warning` → `error`。设置后，只接收该等级及更高等级的新日志；这影响应用日志输出，不只是控制台显示，也不会移除缓冲区中已有日志。

例如，排查问题时执行 `loglevel debug`，完成后执行 `loglevel info`。`warn`、`trace`、数字或多个参数均不合法，会提示 `Usage: loglevel [debug|info|warning|error]`，原等级不变。

Debug 构建默认使用 `debug`，其他构建默认使用 `info`；启动环境变量 `TRAIN_LOG_LEVEL` 可以覆盖默认值。控制台设置仅对当前进程生效，不会写回配置或环境变量。

注意：查询和设置提示本身是 `info` 级别，并在修改等级之前记录。因此处于 `warning` 或 `error` 时，执行 `loglevel info` 可能没有回显，但设置仍会生效；再执行一次 `loglevel` 即可确认。
