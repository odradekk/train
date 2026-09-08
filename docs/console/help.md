# `help`：列出命令

[返回命令索引](README.md)

```text
help
```

参数：无。输出 `Available commands: ...`，列出当前构建的顶层命令名称，不提供逐项参数帮助。

非 Debug 构建的列表不包含 `reload`。包含 PR #47 的构建会列出 `voxel`，但这不表示当前已进入可执行体素命令的场景。`voxel` 的碰撞查询是子命令 `voxel collide`，见 [voxel-collide.md](voxel-collide.md)，不会出现在 `help` 的顶层列表里。
