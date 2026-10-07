# `help`：列出基础命令

[返回命令索引](README.md)

```text
help
```

`help` 输出当前构建可用的基础顶层命令名称。Debug 构建列出 `help, clear, quit, reload, loglevel`；发布构建不列出 `reload`。

语法：无参数，附带 token 会忽略；例如 `help extra` 仍只列出命令名称。
