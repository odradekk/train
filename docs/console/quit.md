# `quit`：退出游戏

[返回命令索引](README.md)

```text
quit
```

参数：无。立即请求退出游戏，不是返回主菜单，也不会弹出确认框。不会自动保存。若要在退出后恢复，先执行 [`voxel save`](voxel-save.md)，记下 UUID，退出并重新启动后再 [`voxel load`](voxel-load.md)。未保存的修改会丢失；已成功写入的 `user://saves/<UUID>/save.json` 不受 `quit` 影响。
