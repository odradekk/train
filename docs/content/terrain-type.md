# 地貌类型

文件为 `resources/terrain-types/<local id>.terrain-type.json`，`schema_version` 为 2。遵循 [通用规则](README.md)，Schema 见 [terrain-type.schema.json](schemas/terrain-type.schema.json)。地貌的稳定身份是包命名空间加 local id。世界生成阈值、绕行系数和铁轨上限在 [生成配置](generation-profile.md)，本文件提供局部场景输入，不重复宏观生成规则。旧版本返回 `unsupported_format`。

所有字段必填，没有隐式默认值；连续值必须有限。场景生成算法归体素模块，本格式不指定噪声、采样、平整或路径算法。

显示字段组成 `DisplayText(Name, Description)`，投影到 `TerrainAppearanceProjection.Display`。

| 字段 | 类型与单位 | 语义 |
| --- | --- | --- |
| `display.name` | 字符串 | 显示名称，去掉首尾空白后 1–100 个字符 |
| `display.description` | 字符串 | 说明，去掉首尾空白后 1–2000 个字符 |
| `surface_voxel` | 体素引用 | 地表使用的场景体素 |
| `subsurface_voxel` | 体素引用 | 下层使用的场景体素 |
| `rock_voxel` | 体素引用 | 岩层使用的场景体素 |
| `local_height_noise` | 对象数组 | 局部高度噪声参数；每项 `scale_meters` 是有限正数米，`amplitude` 是有限非负振幅。复用核心 NoiseOctave 输入形状，不承诺噪声算法 |
| `decorations` | 对象数组 | 每项 `scene_model` 引用场景模型蓝图，`weight` 为正整数；空数组表示没有装饰 |
| `decoration_target_per_square_meter` | 非负数，个/平方米 | 装饰的单位面积目标密度；具体采样与平整方式归体素模块 |
| `construction_site` | 场景模型蓝图引用 | 施工地块使用的场景模型 |
| `railway_models` | 对象数组 | 每项 `tracks` 是正整数铁轨数，`bridge`、`tunnel_portal`、`repair_site` 是场景模型蓝图引用；地貌与轨数一起选择桥梁、隧道口和断轨修复工地 |

加载器检查：文件名与 id 一致，全部引用存在且依赖已声明；地表、下层和岩层是无 simulation 的场景体素；装饰、桥梁、隧道口、断轨修复工地和施工地块引用是场景模型；装饰引用不重复，轨数键唯一，并覆盖所选生成配置允许该地貌产生的全部非零铁轨数；没有装饰时目标密度必须为 0。`schema_version` 不是 2 时返回 `unsupported_format`。局部高度噪声和模型载荷交给场景消费者，核心只接收 TerrainTypeId。这些场景引用只进入表现，不进入核心输入身份或生成语义修订。

样例的下层、岩层、桥梁、隧道口、修复工地和施工地块借用既有场景内容以展示引用格式，不表示正式美术或可通过场景制作验收；噪声、密度等数值不冻结配平。
