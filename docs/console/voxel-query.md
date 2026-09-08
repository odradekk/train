# `voxel query`：查询体素

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [朝向编码](README.md#朝向编码) · [结果与错误处理](README.md#结果与错误处理)

```text
voxel query <x> <y> <z>
voxel query 4 4 4
voxel query 0 0 0
```

参数：三个必填坐标，格式见[通用参数表](README.md#使用前提与通用参数)。命令只读取状态，不会改变世界或将 HUD 的固定目标格切换为该坐标。切片 2 查询 `15 16 15` 可核对交汇块占用；查询越界坐标仍返回 `empty`，不能据此判断可否放置。

占用格按字段分行输出（省略日志前缀），避免合法长 `namespace:id` 把规格截断：

```text
Cell (4, 4, 4): core:iron-ore
orientation px_py
durability 80
value 10
mass 1
max 150
Voxel count: 512.
```

`durability` 是实例当前耐久；`value`、`mass`、`max` 来自当前已发布目录，不是实例私有副本。空格输出 `Cell (...): empty.`，同样附带世界总体素数。

当前查询对越界坐标也返回 `empty`，不会报告 `OutOfBounds`，因此不能用查询中的 `empty` 判断坐标可否放置。增删和伤害命令会单独验证边界。
