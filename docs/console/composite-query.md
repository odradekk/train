# `composite query`：查询已制造车辆

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数)

```text
composite query
composite query 1
composite query 1 parts
composite query 1 parts 2
```

参数：可选的车辆运行时整数 ID（从 1 开始）。省略时列出当前会话的全部车辆摘要，并附带第 1 页零件。未知 ID 返回 `Composite UnknownVehicle <id>.`，不改会话。

`parts` 子参数只读某一页零件。页码从 1 开始，每页 32 条，按 Local Cell 稳定排序。省略页码时读第 1 页。超出总页数仍返回 `parts page <n>/<total>`，不含零件行。绑定零件 ID 与零件行都以本次制造返回值为准，不要写死恰好分到的整数。

成功时按字段分行写出身份、声明规格、构造/身份保留/运营状态、刚体 Transform、固定包络、零件数和接触锚点。接触坐标与绑定零件格分别列出，不能把接触点 Y 误当成零件格 Y。零件行给出身份、Local Cell、定义、朝向和当前耐久。

```text
Composite <vehicle-id> core:dev-hollow-shell
archetype core:dev-hollow-shell
size small
construction Valid
identity Retained
operational Unevaluated
transform 8,8,8 px_py
envelope 0,0,-1 -> 8,6,4
parts 168
contact 1,-0.75,1.5 cell (1,0,1) part <core-part-id>
contact 7,-0.75,1.5 cell (7,0,1) part <rear-part-id>
parts page 1/6
part <id> cell (0,0,-1) core:vehicle-shell px_py durability 100
```

168 格夹具共 6 页。第 6 页用 `composite query <id> parts 6`。没有车辆时返回 `Composite none.`。本票的运营状态固定为 `Unevaluated`，不伪造牵引或其他 Capability。查询不改变权威身份、布局或 `NextEntityId`。
