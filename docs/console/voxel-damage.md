# `voxel damage`：对体素施加伤害

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [朝向编码](README.md#朝向编码) · [结果与错误处理](README.md#结果与错误处理)

```text
voxel damage <x> <y> <z> <amount>
voxel damage 4 4 4 25
voxel damage 4 4 4 100 5 4 4 100 6 4 4 25
```

参数：一组或多组 `x y z amount`。`amount` 必须是正整数。一组目标走单目标伤害；多组目标属于同一伤害事件，先校验全部请求，再统一扣减耐久并删除归零体素，最后才刷新显示。不能用 `voxel remove` 代替本命令验证破坏。

部分伤害成功返回 `Damaged`，后附该格当前查询（定义、朝向、剩余耐久）和世界总数。归零或过量伤害返回 `Removed`，该格变为空。同一事件中可以同时出现删除和部分伤害。

从初始 512 格状态：

```text
voxel reset
voxel damage 4 4 4 25
voxel damage 4 4 4 75
voxel add 4 4 4 core:iron-ore px_py
voxel add 4 4 4 core:iron-ore px_py
```

应依次得到耐久 75 / 总数 512、该格为空 / 总数 511、重加后耐久 100 / 总数 512，最后一次为 `Occupied`。

```text
voxel reset
voxel damage 5 5 5 100
voxel query 5 5 5
```

内部格归零后为空、总数 511，相邻内部面更新；普通世界体素不会因失去支撑而坍塌。封闭空腔从固定相机看不到，不表示伤害失败。

```text
voxel reset
voxel damage 4 4 4 100 5 4 4 100 6 4 4 25
```

最终总数 510，`(6,4,4)` 耐久 75。交换这三组目标的输入顺序，最终占用、耐久和可见面相同。

`amount` 为 `0` 或负数返回 `InvalidDamage`；越界返回 `OutOfBounds`；空格返回 `Empty`，不创建实例。多目标中任一项失败则整件事失败，已提交占用和耐久不变。参数个数、坐标或数量无法解析时只给出用法，不执行世界命令。
