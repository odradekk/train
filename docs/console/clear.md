# `clear`：清空控制台日志

[返回命令索引](README.md)

```text
clear
```

`clear` 只清空当前 `RingBufferLogSink` 中的内存日志，不删除持久日志，不重置内容或世界，也不改变日志等级。语法：无参数，附带 token 会忽略；例如 `clear extra` 与 `clear` 相同。
