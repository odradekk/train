# `loglevel`：查询或设置日志等级

[返回命令索引](README.md)

```text
loglevel
loglevel debug
loglevel info
loglevel warning
loglevel error
```

不带参数时查询当前等级；带一个参数时，只接受 `debug`、`info`、`warning`、`error`。多于一个参数或其他 token 会报告中文用法，原等级不变。

过滤等级从低到高为 `debug`、`info`、`warning`、`error`。设置后只接收该等级及更高等级的新日志，已有缓冲日志不删除。启动环境变量 `TRAIN_LOG_LEVEL` 可以覆盖默认等级；控制台设置只影响当前进程。

查询和设置提示先按 `info` 记录，再改变等级。因此当前等级为 `warning` 或 `error` 时，执行 `loglevel info` 可能没有回显，但设置仍会生效；再次执行 `loglevel` 可确认。

底栏的输出级别筛选只改变已有日志的显示，不改变这里设置的接收等级，也不影响磁盘日志。已被接收等级过滤掉的新日志，不能靠切换显示筛选找回。
