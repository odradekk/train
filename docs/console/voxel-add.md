# `voxel add`：添加体素

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [朝向编码](README.md#朝向编码) · [结果与错误处理](README.md#结果与错误处理)

本命令可撤销；尾随 `at <step>` 会结束现有历史，历史非空时须再尾随 `confirm-end-history`。

```text
voxel add <x> <y> <z> <namespace:id> <orientation>
voxel add 0 0 0 core:ferrous-ore px_py
voxel add 1 0 0 core:ledge pz_py
```

参数：三个坐标、一个完整定义 ID、一个朝向编码，全部必填，没有默认参数。只能在当前切片有效范围内的空格中添加（切片 1 为 `0..15`，切片 2 为 `0..31`），不会覆盖或旋转已经存在的体素。`core:ledge` 的局部碰撞盒小于一格，但仍然完整占格，不能再放其他体素。切片 2 可在 `15/16` 边界两侧添加，邻接可见面随正式占用更新。

成功返回 `Added`，实例使用当前已加载定义的最大耐久创建，后附该格的实际查询结果及世界总体素数。若要改变已有格的定义或朝向，先 `remove` 再 `add`；这是两个独立操作，第二步失败不会恢复第一步已移除的体素。

例如，从初始状态将角落体素改为另一正交朝向：

```text
voxel remove 4 4 4
voxel add 4 4 4 core:ferrous-ore pz_py
voxel query 4 4 4
```

定时执行：在末尾追加 `at <step>`（例如 `voxel add 60 3 20 core:ferrous-ore px_py at 303`），命令进入世界修改队列，在该模拟步边界按接受顺序执行，到期时重新检查占用等前提；输出改为 `Edit <id> voxel.add pending` 与 `accepted <步:序号> target <步>` 两行。不带 `at` 时立即执行，输出同上。位置、拒绝和保存规则见 [`voxel edits`](voxel-edits.md)。
