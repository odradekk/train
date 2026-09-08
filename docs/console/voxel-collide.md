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
| 单格 | 三个整数世界坐标，格式见[通用参数表](README.md#使用前提与通用参数) | 该 Cell 的完整盒 `[x, x+1] × [y, y+1] × [z, z+1]`。整格盒与 `local_box` 重叠时仍为 `Hit`，即使格中心在盒外；格内盒外要用六参数探测 |
| 任意盒 | 六个有限十进制数，按世界轴 `min → max`，可用小数；解析使用不变区域性 | 半开重叠：`min < other.max` 且 `max > other.min`；共面不算命中 |

占格与碰撞分离。体素始终占据整个 Cell，不能在已占用格再放置；`collision.kind` 决定查询是否命中：

| 定义 | 碰撞 | 查询 |
| --- | --- | --- |
| `core:iron-ore`、`core:fixed` | `full_box` | 完整 Cell 盒参与命中 |
| `core:hollow` | `none` | 仍占格、不能重复放置，查询不命中 |
| `core:ledge` | `local_box` | 仍占一整格；只有旋转后的局部盒与探测重叠才命中 |

`local_box` 写在定义的 `collision` 对象里，坐标是所属 Cell 的局部单位立方体 `[0, 1]³`：原点在局部 (0,0,0) 角，+X/+Y/+Z 分别是体素 Right/Up/Forward，单位 1 = 1 格边 = 1 Godot 单位。编码：

```json
"collision": {
  "kind": "local_box",
  "offset": { "x": 0.125, "y": 0, "z": 0.25 },
  "size": { "x": 0.25, "y": 0.875, "z": 0.5 }
}
```

`offset` 与 `size` 必须是有限 double，各轴 `size` 严格为正，`offset >= 0` 且实际算出的 `offset + size` 既大于 `offset` 又不大于 1。仅 `size > 0` 不够：若加法后两端无法区分（例如 `offset.x=0.5` 且 `size.x=1e-20`），或形状越出本 Cell，定义会被拒绝。内容合法性只看这个局部 double 区间，不按某个开发世界尺寸或 Godot 单精度采样。已发布且局部可表示的盒在查询时按所属 Cell 的局部几何重叠，Godot 形状按局部尺寸和朝向派生，不把 `cell + size` 加塌后的世界端点当成权威。未知字段、非有限值、非正尺寸同样拒绝；内置错误拒绝整次发布，非法 Mod 整包排除。`none` / `full_box` 不能带 `offset` 或 `size`。

`core:ledge` 的示例盒偏心且三轴不等。默认 `px_py` 下，世界盒在所属格的 `[x+0.125, x+0.375] × [y, y+0.875] × [z+0.25, z+0.75]`；`pz_py` 后变为 `[x+0.25, x+0.75] × [y, y+0.875] × [z+0.125, z+0.375]`。不能只用居中立方体验证旋转。

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
| `Hit` | 探测盒与至少一个有效碰撞形状重叠（`full_box` 整格或旋转后的 `local_box`）；后附每个命中的格子、定义 ID 和 `kind` |
| `Miss` | 探测盒与世界有重叠，但重叠范围内没有有效形状（空格、`none` 占格、`local_box` 的格内盒外、或缺少当前定义） |
| `OutOfBounds` | 已构成合法正体积探测盒，但单格超出当前切片有效范围（含三轴 `int.MinValue` / `int.MaxValue`），或盒与世界体积完全不相交 |
| `InvalidQuery` | 文本已解析为数字，但盒坐标非有限（`NaN` / `Infinity` / `-Infinity`），或任一轴 `max ≤ min`（零体积、翻转，以及公开 `default(VoxelCollisionBox)`） |
| `Usage: voxel ...` | 参数个数无法构成单格或六参数盒，或坐标文本无法解析为数字；不执行查询 |

单格在构盒前按世界整数坐标读入；格盒用双精度表示 `[x, x+1]`，因此 `2147483647` 这类合法整数不会在加一时溢出。三种入口共用同一有效性规则：六参数查询、公开 `VoxelCollisionBox` 重载和 `default` 零体积盒都走 `InvalidQuery`，不能把默认 struct 当成合法越界盒。

```text
voxel collide 2147483647 0 0
voxel collide 0 0 0 0 0 0
voxel collide NaN 0 0 1 1 1
voxel collide foo 0 0 1 1 1
```

依次得到 `OutOfBounds`、`InvalidQuery`、`InvalidQuery`、`Usage`。

`voxel query` 对越界格仍显示 `empty`，不能用来判断边界。碰撞查询对越界单格返回 `OutOfBounds`。`Miss` 不是空格证明：`core:hollow` 占用时 `voxel query` 有实例，`voxel collide` 仍为 `Miss`。

切片 2 可查 `15/16` 两侧或跨边界盒。例如 `voxel collide 15.25 16 15 16.75 17 16` 在两侧都是 `full_box` 时应命中 `(15,16,15)` 与 `(16,16,15)`；去掉一侧后只剩另一侧。同一事件跨区伤害后，被删除格立即 `Miss`，未归零邻格仍 `Hit`。

领域盒查询按半开重叠判定，共面不算命中。Godot 物理形状是闭合接触；开发场景里用格子内缩盒（约 `[+0.25,+0.75]`）或同样内缩的跨格盒做交叉检查，避免只靠共面接触对上领域结果。任意大盒在真实引擎里仍走 `IntersectShape`，命中集合以该次查询返回值为准。

合法热重载把 `full_box` 改为 `none` 后，已占用实例的查询立即 `Miss`，画面碰撞在刷新后一致；把 `local_box` 的有效几何改到另一角落后，命中区域立即改用新参数。纯显示文字或贴图更新不改变碰撞；非法盒、缺失定义或新朝向约束与已有实例冲突时，整次 `reload` 拒绝，保留旧目录、Generation、世界、查询和已发布碰撞。需要丢弃布局时用 `voxel reset`，不要把碰撞查询当成写入入口。

切片 1 可放 `core:ledge` 后对照盒内/盒外探测；切片 2 可在 `(15,1,8)` 做 Chunk 边界旋转对照。手册索引中的组合示例给出了 `px_py` / `pz_py` 的具体探测盒。
