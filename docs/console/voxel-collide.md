# `voxel collide`：查询碰撞

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [结果与错误处理](README.md#结果与错误处理)

```text
voxel collide <x> <y> <z>
voxel collide <minx> <miny> <minz> <maxx> <maxy> <maxz>
voxel collide 4 4 4
voxel collide 15.25 16 15 16.75 17 16
```

参数有两种互斥形式，都只读取已提交世界和当前已发布目录，不改变占用、耐久、朝向或 HUD 选中格。

| 形式 | 参数 | 探测盒 |
| --- | --- | --- |
| 单格 | 三个整数世界坐标，格式见[通用参数表](README.md#使用前提与通用参数) | 该 Cell 的完整盒 `[x, x+1] × [y, y+1] × [z, z+1]` |
| 任意盒 | 六个有限十进制数，按世界轴 `min → max`，可用小数；解析使用不变区域性 | 半开重叠：`min < other.max` 且 `max > other.min`；共面不算命中 |

占格与碰撞分离。体素始终占据整个 Cell，不能在已占用格再放置；`collision.kind` 决定查询是否命中：

| 定义 | 碰撞 | 查询 |
| --- | --- | --- |
| `core:iron-ore`、`core:fixed` | `full_box` | 完整 Cell 盒参与命中 |
| `core:hollow` | `none` | 仍占格、不能重复放置，查询不命中 |

权威数据是世界占用和当前定义，不是 Mesh、旧 Collider 或画面。删除或归零后，领域查询立即未命中；Godot 派生碰撞在下一次刷新后与领域一致。旧碰撞形状不能授权增删或伤害。

## 结果

按字段分行（省略日志前缀），避免合法长 `namespace:id` 把状态截断：

```text
Collision Hit (1)
cell (4, 4, 4): core:iron-ore
kind full_box
```

盒查询可返回多个命中，按 X、Y、Z 排序。未命中和越界会带上探测盒：

```text
Collision Miss [0, 0, 0 -> 1, 1, 1].
Collision OutOfBounds [16, 4, 4 -> 17, 5, 5].
Collision InvalidQuery.
```

| 状态 | 含义 |
| --- | --- |
| `Hit` | 探测盒与至少一个 `full_box` 占用格重叠；后附每个命中的格子、定义 ID 和 `kind` |
| `Miss` | 探测盒与世界有重叠，但重叠范围内没有 `full_box`（空格、`none` 占格、或缺少当前定义） |
| `OutOfBounds` | 单格坐标超出当前切片有效范围；或盒与世界体积完全不相交 |
| `InvalidQuery` | 盒坐标非有限，或任一轴 `max ≤ min`（零体积或翻转） |
| `Usage: voxel ...` | 参数个数无法构成单格或六参数盒，或坐标无法解析；不执行查询 |

`voxel query` 对越界格仍显示 `empty`，不能用来判断边界。碰撞查询对越界单格返回 `OutOfBounds`。`Miss` 不是空格证明：`core:hollow` 占用时 `voxel query` 有实例，`voxel collide` 仍为 `Miss`。

切片 2 可查 `15/16` 两侧或跨边界盒。例如 `voxel collide 15.25 16 15 16.75 17 16` 在两侧都是 `full_box` 时应命中 `(15,16,15)` 与 `(16,16,15)`；去掉一侧后只剩另一侧。同一事件跨区伤害后，被删除格立即 `Miss`，未归零邻格仍 `Hit`。

领域盒查询按半开重叠判定，共面不算命中。Godot 物理形状是闭合接触；开发场景里用格子内缩盒（约 `[+0.25,+0.75]`）或同样内缩的跨格盒做交叉检查，避免只靠共面接触对上领域结果。任意大盒在真实引擎里仍走 `IntersectShape`，命中集合以该次查询返回值为准。

合法热重载把 `full_box` 改为 `none` 后，已占用实例的查询立即 `Miss`，画面碰撞在刷新后一致；纯显示文字或贴图更新不改变碰撞；非法或拒绝的 `reload` 保留旧目录、世界和碰撞。需要丢弃布局时用 `voxel reset`，不要把碰撞查询当成写入入口。
