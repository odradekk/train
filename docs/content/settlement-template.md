# 聚居地模板

聚居地模板是创建一个聚居地所用的初始状态：在哪个场景模型上，有哪些地块，哪些地块上预先建好建筑，玩家建立聚居地时建哪些起始建筑，开局库存有什么。文件名是 `<local id>.settlement-template.json`，放在 `resources/settlement-templates/`。Schema 见 [schemas/settlement-template.schema.json](schemas/settlement-template.schema.json)，示例见 [examples/](examples/)。

规则的来由见设计文档 [16 的聚居地模板](../design/16-settlements.md#聚居地模板)、[05 的世界生成流程](../design/05-world-graph-and-generation.md#世界生成流程)和 [09 的存档中的内容](../design/09-persistence.md#存档中的内容)。

```json
{
  "schema_version": 1,
  "id": "farm-village",
  "settlement_type": "farm",
  "weight": 2,
  "scene_model": "village-ground",
  "plots": [
    { "id": 1, "size": "station", "at": [0, 1, -10], "orientation": "pz_py", "building": "small-station" },
    { "id": 2, "size": "small", "at": [-6, 1, 2], "building": "warehouse" },
    { "id": 3, "size": "medium", "at": [6, 1, 0] }
  ],
  "founding": [{ "plot": 1, "building": "small-station" }],
  "inventory": [{ "voxel": "grain", "quantity": 40 }],
  "display": { "name": "农庄村落", "description": "……" }
}
```

模板不按读取方分节：核心模拟层读地块的编号、尺寸、建筑、`founding` 和 `inventory`，场景读 `scene_model` 和地块的位置、朝向。

## 字段

| 字段 | 类型 | 必填 | 默认值 | 说明与校验 | 阶段 |
| --- | --- | --- | --- | --- | --- |
| `schema_version` | 整数 | 是 | — | 固定为 1 | Demo |
| `id` | 字符串 | 是 | — | local id，必须和文件名一致 | Demo |
| `settlement_type` | 引用 | 是 | — | 这份模板属于哪个聚居地类型。可以是其他包的类型，例如 Mod 给 `core` 的农庄加一份模板 | Demo |
| `weight` | 整数 | 否 | 1 | 在同一类型的模板中被选中的权重，正整数 | Demo |
| `scene_model` | 引用 | 是 | — | 一个类别为场景模型的蓝图，提供地形整形、道路和装饰 | Demo |
| `plots` | 对象数组 | 是 | — | 地块表，至少一个，见下表 | Demo |
| `founding` | 对象数组 | 否 | `[]` | 玩家在空节点建立聚居地时建的起始建筑，每项是 `plot` 和 `building`；BOM 开工时从列车库存一次扣除 | Demo |
| `inventory` | 对象数组 | 否 | `[]` | 开局库存，每项是 `{ "voxel": …, "quantity": … }`，或者大型设备的 `{ "blueprint": …, "quantity": … }`（功能复合体按标识入库，组装体按复制出的设计的构成哈希入库）；数量是正整数 | Demo |
| `display.name`、`display.description` | 字符串 | 是 | — | 1–100 和 1–2000 个字符，创作模式和调试界面里显示 | Demo |

地块表的每一项：

| 字段 | 必填 | 默认值 | 说明与校验 |
| --- | --- | --- | --- |
| `id` | 是 | — | 地块编号，正整数，在模板内唯一；玩家下“在地块 P 建造”命令时用它 |
| `size` | 是 | — | `small`、`medium`、`large` 或 `station`；各尺寸的格数在场景实测时确定 |
| `at` | 是 | — | `[x, y, z]`，场景模型的坐标：建筑原点放在这里，即地块占地中心、地面上第一层格 |
| `orientation` | 否 | `px_py` | 写法同蓝图的朝向，只能是绕竖直轴的 4 种：`px_py`、`pz_py`、`nx_py`、`nz_py`；建筑按这个朝向摆放，例如 `pz_py` 让建筑的正面朝 −X。站点地块的朝向怎样决定铁轨方向，随进站区域的场景生成确定 |
| `building` | 否 | — | 世界生成时预先建在这里的建筑蓝图 |

## 加载器检查的规则

- `id` 和文件名一致；引用的类型、蓝图和货物都存在，跨包引用的包已声明为依赖。
- `scene_model` 是场景模型；`building` 和 `founding` 里的蓝图是建筑。
- 建筑能放进地块：普通建筑放进同尺寸或更大的地块；`station` 类型的建筑只能放在站点地块，站点地块只能放 `station` 类型的建筑。
- 地块编号不重复，`founding` 用到的地块存在。
- `inventory` 里的 `voxel` 是货物，`blueprint` 是大型设备。
- 地块之间不重叠、地块在场景模型范围内的检查，等地块尺寸的格数确定后加入。

## 从世界生成到聚居地出现在节点上

1. **选类型**：世界生成在“占据者”阶段，按节点的地貌和类型的 `weight`、`preferred_terrain` 选出聚居地类型。从 Demo 起，没有模板的类型不参与选择；首个切片没有模板，按类型直接生成聚居地。世界生成配置不直接引用模板。
2. **选模板**：所有 `settlement_type` 指向这个类型的模板都是候选，用 WorldSeed 和节点身份构成的带键随机按 `weight` 选一份，之后不再改变。
3. **复制设计**：从 `building` 引用的蓝图复制设计，给每个预建的建筑实例；从 `scene_model` 复制设计，给这个聚居地的场景。之后蓝图怎么改，都不影响这个聚居地。
4. **初始状态**：
   - 地块表和建筑来自模板，开局库存来自 `inventory`；
   - 常住人口在类型的范围内取值；供需按 [19](../design/19-markets-and-trading.md)由真实库存、人口和启用的生产计划派生，不再读取类型的临时供需；站台由站点里的 `platform` 部件提供，不读类型的 `initial_platform_count`；
   - 士气和电力池用代码里的初始值，所有者和关系由世界生成配置决定。
5. **出现在节点上**：核心模拟层得到聚居地状态；场景按场景模型和地块位置摆出地面、装饰和建筑。存档内嵌模板的副本和场景模型设计的副本，相同内容只存一份，读档后不再重新选择。

玩家在空节点建立聚居地时，第 1、2 步相同；之后只建 `founding` 里的起始建筑，不建 `building` 里的预建建筑，开局库存为空，常住人口取类型的下限。

世界生成还可产生有限的初始待售车辆，见 [19](../design/19-markets-and-trading.md#现车交易与估值)；车辆不是上面的 `inventory` 货物。其初始化格式由[内容契约](https://github.com/odradekk/train/issues/288)确定，默认现车、生产计划与开局库存的规则见[Demo 内容与资产清单](demo-catalog.md#npc-的初始库存与现车)，本文与现有 Schema 尚未表达这些新增输入。

## 样例

| 文件 | 种类 | 展示的写法 | 阶段 |
| --- | --- | --- | --- |
| `example-pack` 的 `farm` 农庄 | 聚居地类型 | 供给粮食、地貌偏好、权重、人口范围、临时站台数、地图符号 | 首个切片 |
| `farm-village` 农庄村落 | 聚居地模板 | 三个地块：站点地块预建小站点，小地块预建仓库，中地块空着；起始建筑是小站点；开局存有 40 份粮食 | Demo |
| `village-ground` 村落地面 | 场景模型 | 24×24 格的草地，带 `flatten`，嵌套一个树丛 | Demo |
| `small-station` 小站点 | 建筑 | `plot_size: station`、`building_type: station`；混凝土站台加一个带 `platform` 的站台控制台 | Demo |
| `warehouse` 仓库 | 建筑 | 预建在小地块上 | Demo |
| `example-addon` 的 `farm-hamlet` 农庄小村 | 聚居地模板 | 第二个包给 `example-pack` 的农庄类型加一份模板，引用写完整标识 | Demo |

## 和现有代码的差别

现在的代码没有聚居地模板，也没有世界生成。实现由实施工单和[世界生成与宏观地图](https://github.com/odradekk/train/issues/223)完成。
