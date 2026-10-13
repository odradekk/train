# `help`：列出基础命令

[返回命令索引](README.md)

```text
help
```

`help` 输出当前宿主可用的顶层命令名称。Debug 游戏宿主列出 `help, clear, quit, reload, loglevel, sim, lighting`；玩家构建列出 `help, clear, quit, loglevel`。编辑器内运行和 debug 模板导出均为开发构建，release 模板导出为玩家构建。

语法：无参数，附带 token 会忽略；例如 `help extra` 仍只列出命令名称。
