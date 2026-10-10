# 世界生成配置

文件为 `resources/generation-profiles/<local id>.generation-profile.json`，`schema_version` 为 1。遵循 [通用规则](README.md)：一文件一份定义、严格 JSON、命名空间引用和包依赖。Schema 见 [对应 Schema](schemas/generation-profile.schema.json)。样例只展示格式，不冻结配平数值。

配置具有包命名空间加 local id 的稳定身份。WorldSeed 与 GeneratorVersion 由宿主传入，不写在此文件；内容解析配置、引用与默认值，返回核心 `ResolvedGenerationConfiguration`、`NewGameStartInput` 及生成内容语义修订，组成 `WorldGenerationInput`。作者只填蓝图引用，所有 DesignHash 由内容计算。生成预算计数语义归 GeneratorVersion。宿主不读取 JSON 或内部索引。

## 字段

所有字段必填，没有隐式默认值；数组可以为空，除非下述业务校验另有限制。连续值必须有限。

显示字段组成 `DisplayText(Name, Description)`，投影到 `GenerationProfileSummary.Display`，用于宿主展示和选择生成配置。

| 字段 | 说明 |
| --- | --- |
| `display.name` | 显示名称，去掉首尾空白后 1–100 个字符。 |
| `display.description` | 说明，去掉首尾空白后 1–2000 个字符。 |
| `configuration` | 完整的核心生成配置；各字段逐一说明见下文。 |
| `start` | 起始资金与有序车队；蓝图引用由内容计算设计哈希。 |

## 完整配置的字段与校验

配置字段均显式必填，以下是 `configuration` 的全部分组。内容负责解析与引用校验，生成器负责候选世界是否满足约束；不能把预算内没有找到候选报告为配置必然无解。

| 字段 | 单位与含义 |
| --- | --- |
| `width_meters`、`height_meters` | 正数米，地图矩形范围 |
| `minimum_node_spacing_meters` | 正数米，节点最小间距 |
| `candidate_probability` | [0,1]，节点候选概率 |
| `elevation_noise`、`moisture_noise`、`river_noise` | NoiseOctave 数组；每项 `scale_meters` 正数米、`amplitude` 非负振幅；参数不规定噪声算法 |
| `lake_elevation_threshold` | 有限数，湖泊海拔阈值 |
| `river_band_half_width` | 非负数，河流噪声窄带半宽，与噪声值同尺度 |
| `terrain_bands` | 每项 `terrain` 地貌引用；`minimum_elevation`/`maximum_elevation`、`minimum_moisture`/`maximum_moisture` 为有限范围端点，最小不大于最大；`maximum_relief` 非负起伏上限；`detour_multiplier` 不小于 1；`maximum_tracks` 正整数铁轨上限。边界命中与带选择算法由 GeneratorVersion 规定 |
| `terrain_sample_spacing_meters` | 正数米，线路地貌采样间距 |
| `settlement_ratio`、`lair_ratio` | [0,1]，聚居地、生物聚落比例，两者之和不超过 1 |
| `hostile_settlement_ratio` | [0,1]，聚居地中初始敌对比例 |
| `lair_weights` | 每项 `type` 引用生物聚落类型、`weight` 正整数；类型不重复；需要生成聚落时至少一项 |
| `resources` | 节点资源生成表，见下文 |
| `initial_railway` | `maximum_tracks` 全局正整数上限；`minimum_initial_tracks`、`maximum_initial_tracks` 是非负整数初始铁轨范围；`additional_cycle_edges` 非负整数，增加的成环边数。最小值不大于最大值，最大值不超过全局上限 |
| `budget` | `maximum_stage_retries`、`maximum_world_retries` 正 Int32 次数；`maximum_candidates`、`maximum_search_work` 正 Int64 计数预算。计数归生成器版本，不使用时间超时替代 |
| `npc_initialization` | 库存与现车初始化参数，见下文 |
| `start_constraints` | 默认开局检查参数，见下文 |
| `rules` | `population`、`market`、`construction`、`railway`、`combat` 分别引用对应规则，投影为核心 RuleSelection。选中的规则必须存在且类别正确 |

每个资源条目包括：`good` 是 Raw Good 体素引用；`allowed_terrain_types` 是允许的地貌引用数组；`probability` 在 [0,1]；`minimum_reserve`、`maximum_reserve` 是非负 Int64 件数，最小不大于最大；`renewable` 是布尔值；`recovery_units_per_second` 是非负件/模拟秒；`regeneration_interval_seconds` 是正数模拟秒。资源货物不重复、地貌引用不重复。非可再生资源仍显式填写两个恢复字段，但核心不执行恢复；可再生资源的参数供运行中的再生使用，不由内容执行再生。

`npc_initialization` 全部字段：`fill_inputs_and_living_targets` 决定是否按核心规则补输入原料与生活物资目标；`output_reserve_hours` 是非负模拟小时的潜在产量预存时长；`minimum_for_sale_vehicles`、`maximum_for_sale_vehicles` 是非负 Int32 数量范围，最小不大于最大；`for_sale_vehicle_candidates` 是不重复的车辆蓝图引用数组，内容计算 DesignHash，初始化由核心按车间档位筛选。需要现车时数组不能为空。内容不推算库存目标、产能或车辆名额。

`start_constraints` 全部字段：`minimum_reachable_non_hostile_settlements` 和 `minimum_reachable_empty_nodes` 是非负 Int32 个数；`require_station`、`exclude_adjacent_lairs` 是布尔值；`fully_loaded_purchase_rounds_per_train` 是正 Int32 满载采购轮数；`required_affordable_commission_design` 是车辆蓝图引用，内容计算 DesignHash，核心验证采购后资金是否仍足够委托建造。内容不计算运行报价或自行选择起点。

`start.initial_money` 是最小货币单位的非负 Int64 整数；`start.trains` 数组顺序保留，每列车 `vehicles` 是非空有序车辆蓝图引用数组，顺序就是编组顺序，允许同一蓝图重复；`inventory` 是货物数量数组，每项使用 `voxel` 或 `blueprint` 与正 Int64 `quantity`，大型设备引用按既有货物身份规则解析。货物身份不重复。内容计算车辆属性与哈希，不能凭空填入能力。

所有引用检查存在性、类别、包依赖；地貌带至少一项，局部地貌模型覆盖配置允许的轨数。聚居地类型和模板的选择由定义权重产生，本配置不重复模板索引。有效生成语义修订涵盖配置及其引用内容；发布序号不能替代它。Profile 的稳定身份用于选择；核心记录完整已解析输入，而不是依靠 Profile id 日后重新加载。

样例只演示一个最小生成配置，起始车队和数值不声称满足正式 Demo 的默认开局或配平验收。正式开局须按 Demo 清单设置两列车、购买检查与现车范围，再经生成与试玩验证。
