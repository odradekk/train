# `composite replace`：用新定义替换格子上的旧件

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [朝向编码](README.md#朝向编码)

```text
composite replace <id> <x> <y> <z> <namespace:id> <orientation>
composite replace 1 3 1 0 core:refit-replacement px_py
```

参数：车辆运行时 ID、被替换的 Local Cell、新体素定义、新件朝向。替换按提交前该格占用解析旧件：成功时拆除旧身份，并只在确定性提交中为新件分配身份。新件按当前定义上限初始化，不能沿用旧件耐久、朝向或其它状态。空格返回 `Empty`。

票定替换件 `core:refit-replacement` 上限 150；成功后该格是新 ID、耐久 150、默认朝向。

```text
Composite Valid.
vehicle 1
allocated 172
parts 169
construction Valid
Voxel count: 0.
```

失败不分配新 ID。用法不合法时只给出 Usage。批量替换见 [`composite batch`](composite-batch.md)。
