# `voxel reset`：重置开发世界

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [朝向编码](README.md#朝向编码) · [结果与错误处理](README.md#结果与错误处理)

```text
voxel reset
```

参数：无，不能附加其他参数。立即丢弃当前场景的全部体素修改、已制造车辆和已提交铁路状态，按当前切片的正式生成入口和当前目录中的 `core:ferrous-ore` 重建基底，朝向恢复为 `px_py`，耐久取当前定义最大值，并把 `NextEntityId` 与事务计数恢复为初始值。没有撤销或确认步骤。切片 1 重建 512 格；切片 2 重建 1,088 格（地面加 `14..17` 交汇块）；切片 3 重建空的 32³ 世界；切片 4 重建空的 320×32×32 世界，不自动施工轨道。Seed 只保留为身份，不引入随机。已分配的存档 UUID 与显示名保持不变；磁盘上的上一份有效存档也不删除。要恢复保存过的世界，使用 [`voxel load`](voxel-load.md)，不要把 `reset` 当成读档。切片 3 清空车辆也可使用 [`composite reset`](composite-reset.md)，二者走同一会话重置入口。切片 4 再铺轨使用 [`rail construct`](rail-construct.md)。场景快捷键 `R` 在切片 4 会在重置后立即再施工，与本命令不同。在 [Playground](README.md#playground具名固定配置) 中执行本命令同样会立即丢弃当前世界、清除固定配置来源信息并取消会话暂停；之后保存不再写出 `world_source`。Playground 不提供受保护的重置或切换配置入口。重置后 Playground 观察的旧选择与聚焦/跟随目标一律清除，镜头原位转为自由观察，并提示 `world-replaced reset`（见 [observe](observe.md)）。

结果以 `Voxel world reset.` 开头，随后给出代表格查询和总体素数：切片 1 查询 `(4,4,4)`，切片 2 查询 `(15,16,15)`，切片 4 查询 `(80,1,15)`。若使用原始定义，应显示该切片的初始总数；切片 4 重置后该格为空。该操作要求当前目录仍包含 `core:ferrous-ore`。
