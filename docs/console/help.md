# `help`：查看命令帮助

[返回命令索引](README.md)

```text
help
help <命令>
```

`help` 输出当前宿主可用的顶层命令名称。Debug 游戏宿主列出 `help, clear, quit, reload, loglevel, sim, train, lighting, dev`；玩家构建列出 `help, clear, quit, loglevel`。编辑器内运行和 debug 模板导出均为开发构建，release 模板导出为玩家构建。

`help <命令>` 显示该命令的中文说明和用法，例如 `help sim`。未知命令会从当前可用命令中给出最接近的建议；参数错误会显示用法。
