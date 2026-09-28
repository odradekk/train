# `help`：列出命令

[返回命令索引](README.md)

```text
help
```

参数：无。输出 `Available commands: ...`，列出当前构建的顶层命令名称，不提供逐项参数帮助。

非 Debug 构建的列表不包含 `reload`。包含 PR #47 的构建会列出 `voxel`，包含切片 3 的构建会列出 `composite`，包含切片 4 的构建会列出 `rail`，包含 #124 的构建会列出 `observe`（仅 Playground 可执行），但这不表示当前已进入可执行这些命令的场景。`voxel`、`composite`、`rail` 与 `observe` 的子命令不会出现在 `help` 的顶层列表里。
