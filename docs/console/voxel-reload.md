# `voxel reload`：重新加载已保存版本

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数)

```text
voxel reload
voxel reload save-as
voxel reload discard
voxel reload cancel
```

语法：`voxel reload [save-as|discard|cancel]`。仅世界编辑器会话可用，并且当前世界必须已经成功保存过一次。尚无存档 UUID 时返回 `NotFound`，保留当前世界。

命令开始时记录当前存档 UUID，后续始终重新加载这个目标。有未保存变化时，不带选择只要求选择：`save-as` 把当前修改另存为新存档，再重新加载开始时记录的原存档；`discard` 放弃修改并重新加载原存档；`cancel` 保留当前世界。另存成功后，即使当前会话的存档 UUID 已变为副本，也不会改变本次重新加载的目标。

确认、另存和候选读取分帧完成。期间模拟推进和新的世界修改暂挂；取消、另存失败或读取失败后恢复原来的运行或暂停状态，不补算等待时间。切换已经开始时，另一条世界切换命令不会更换目标。

成功后，当前会话恢复为原存档的身份和内容，默认暂停。读取失败时当前世界保持不变；如果此前已经成功另存，副本和原存档都保留，可以从存档工作区继续打开。

此命令与 [`voxel reset`](voxel-reset.md) 不同：`reset` 按来源重新建立基底并保留当前存档身份，`reload` 则读取已保存文件。读取指定的其他 UUID 使用 [`voxel load`](voxel-load.md)。
