# `voxel add`：添加体素

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [朝向编码](README.md#朝向编码) · [结果与错误处理](README.md#结果与错误处理)

```text
voxel add <x> <y> <z> <namespace:id> <orientation>
voxel add 0 0 0 core:iron-ore px_py
```

参数：三个坐标、一个完整定义 ID、一个朝向编码，全部必填，没有默认参数。只能在有效范围内的空格中添加，不会覆盖或旋转已经存在的体素。

成功返回 `Added`，实例使用当前已加载定义的最大耐久创建，后附该格的实际查询结果及世界总体素数。若要改变已有格的定义或朝向，先 `remove` 再 `add`；这是两个独立操作，第二步失败不会恢复第一步已移除的体素。

例如，从初始状态将角落体素改为另一正交朝向：

```text
voxel remove 4 4 4
voxel add 4 4 4 core:iron-ore pz_py
voxel query 4 4 4
```
