# `voxel new`：新建空白世界

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数)

```text
voxel new 33 17 19 42
voxel new 320 32 32 0 discard
voxel new 16 16 16 18446744073709551615 save
voxel new 16 16 16 42 cancel
```

语法：`voxel new <size-x> <size-y> <size-z> <seed> [discard|save|cancel]`。已进入任一体素开发场景或 Playground 后可用。

尺寸使用三个十进制整数，表示从原点开始的有限三维空间：X 为 **1–320**，Y/Z 各为 **1–32**，有效坐标分别为 `0..size-1`。这些是当前开发版本的保守支持边界，不是最终世界容量承诺；Chunk 边长固定为 16，不要求尺寸整除 16。种子使用无符号十进制整数，范围 **0–18446744073709551615**；不接受负数、小数、溢出、缺参数或额外参数。非法输入明确报告 `IncompatibleBase` 和 `stage base subject blank relationship parameters`，不保存或替换当前世界。

可选选择沿用 [`voxel reset`](voxel-reset.md)：当前未保存时，无选择只要求选择；`discard` 放弃当前更改；`save` 先成功保存再新建；`cancel` 保留当前世界。保存失败时不新建。新世界首次保存前也视为未保存。

成功时整体替换当前世界，旧观察选择和聚焦/跟随目标清除，镜头原位转为自由观察。新世界没有体素、建筑、轨道或车辆，不隐含直轨，也不需要 `core:ferrous-ore` 或其他资源定义。它默认暂停在第 0 步，没有待执行命令，没有存档 UUID，标为未保存。输出以 `Voxel blank world.` 开头，并报告范围、种子、暂停和未保存状态。

来源类型为 `blank`，`source_version=1`；有效输入记录范围、种子和 Chunk 尺寸，`base_fingerprint` 核验空白基底，内容依赖为空。保存仍使用来源基底加 Delta，不增加世界快照。首次 [`voxel save`](voxel-save.md) 成功才建立 UUID，失败保留未保存状态和原身份。

可复现的保存与继续操作：

```text
voxel new 33 17 19 42 discard
voxel add 17 1 1 core:ferrous-ore px_py
voxel damage 17 1 1 25
voxel remove 17 1 1 at 3
rail step
voxel save Blank World
```

记下输出的 UUID，执行 `quit` 退出进程。重新启动，进入任一 Playground，再执行：

```text
voxel load <uuid> discard
voxel query 17 1 1
voxel add 18 1 1 core:ferrous-ore px_py
rail step 2
rail resume
rail pause
voxel use straight-rail-empty discard
voxel load <uuid> discard
```

恢复后应暂停在第 1 步，耐久为 75，原移除命令仍待执行；推进两步后它执行一次。随后可继续编辑、运行，并在空白、固定配置和旧切片存档之间切换。自动验证为 `scripts/run-godot-playground-cross-process.ps1 -BlankWorld`，使用隔离用户数据和两个真实 Godot 进程。
