# 05 世界生成与聚居地

> 决策出处：转录 Q89–Q95、Q102、Q178；2026-09-05 #30 确认 Faction 延后，[#36](https://github.com/odradekk/train/issues/36) 确认位置、生成分类、建筑与资源事实来源、接轨及静态占格。

## 边界

本文定义程序生成流水线、资源生成、聚居地的结构、聚居地与铁路的关系、Layout 放置。铁路的拓扑、图与施工见 [06](06-railway.md)；聚居地的经济规则留待后续扩充；术语以 `CONTEXT.md` 为准。

## 不变量

- 同一 WorldSeed + GeneratorVersion + 有效生成语义内容必须产生同样的初始世界；不同 GeneratorVersion 或生成内容不要求得到相同地图。
- 后续阶段新增 RNG 调用不能改变前面阶段的结果。
- Settlement exists ≠ Station exists ≠ Rail connection exists。
- 聚居地类型（SettlementArchetype）与空间布局（Layout）严格分离；首版 Archetype 只承担生成用途，经济行为延后。
- 聚居地位置只有一份三维体素锚点；建筑数量与资源种类反映实际世界，不由 Layout 模板另存一份权威清单。

## 规则

### 世界生成

世界采用程序生成 + 手工 Blueprint/Layout，而不是固定手工地图。世界生成必须使用 Seed 可复现。

### Generation Pipeline

```text
WorldSeed
↓ Terrain
↓ Resource Potential
↓ Actual Resources
↓ Settlement Candidates
↓ Settlement Archetypes
↓ Logical Settlement Nodes
↓ Logical Rail Graph
↓ Layout Selection + Rotation
↓ Settlement Materialization
↓ Physical Rail Pathfinding
↓ Decoration
↓ Validation
```

- 每个 Stage 使用独立派生 Seed：Hash(WorldSeed, GeneratorVersion, StageId, RetryIndex)。
- 每个 Stage：Generate → Validate → Success / Retry。Retry 必须有明确上限；生成器允许返回 WorldGenerationFailed，不能无限循环。

### 资源生成

- Resource Potential Field → Actual Resource Voxels。Potential Field 用于宏观判断资源分布并选择符合资源偏好的聚居地类型，不作为聚居地当前资源种类的事实来源。
- 聚居地类型原则上响应自然资源，而不是为了聚居地类型强行生成资源；允许少量 Gameplay Validation 和保证性调整。

### 聚居地资源种类

- 资源种类从聚居地周边当前实际存在、带 `natural_resource` 标签的体素派生，不固定保存生成时的清单；Layout 不另行声明资源种类。
- 查询范围以聚居地锚点为基准，由生成配置明确，不能把 Layout 中声明的建筑占地隐式当作资源范围。具体范围形状与参数随生成切片确定。
- 一种资源在范围内被采尽后，从派生种类中消失；体素或其资源标签变化后，查询结果须反映当前世界。该结果只表示种类，不表示储量或产量。

### 聚居地

- 聚居地本身不是 Composite。首版模型记录 SettlementId、Archetype、三维布局锚点、朝向、实际建筑实例引用及可选 Station，保留既有的静态常住人口数据及其正数约束；生产、消费、人口变化与 Faction 的玩法设计和实现延后，首版不保留阵营占位字段。
- **SettlementArchetype** 与术语表的聚居地类型是同一概念，不维护两套分类；首版描述生成所需的资源偏好与候选 Layout，不产生经济模拟行为。
- **SettlementLayoutDefinition** 负责空间布局：Building Composite、World Voxel、Decoration、Site Constraints、optional Station/Rail Socket。建筑为 Composite，道路与装饰为世界体素（转录 Q93）。

### 位置与布局实例

- 聚居地位置采用 Layout 锚点的世界三维整数体素坐标，布局朝向单独保存。高度由生成器结合地形与 Layout 约束确定。
- 二维地图需要位置时从三维锚点投影，不继续独立维护旧 `MapPosition(X, Y)`；具体代码与内容迁移在生成切片执行。
- Layout 只是生成模板，放置后形成实际建筑实例；聚居地记录这些实例，建筑数量按当前实例的建筑类型派生汇总，站点不计入普通建筑数量。
- 制造、拆除或破坏改变实际建筑集合后，统计反映新状态；修改 Layout 模板不自动重建已有聚居地。

### 聚居地与铁路

- 首版每个聚居地有零个或一个站点，允许没有站点或尚未接入铁路的聚居地；测试场景可显式选择需要生成铁路连接的站点，不要求贸易系统参与。
- 站点通过一个或多个 RailSocket 描述接轨位置和方向。Layout 可放置站点及其接口；RailSocket 是站点接轨能力的描述，不是聚居地身份的必要条件（转录 Q178 修正 Q102）。
- RailSocket 不等于实际轨道体素的 Track Port，也不自动创建铁路连接或压缩图节点。实际连通性由 Physical Rail Voxel 的端口与内部连接决定，Station 作为重要位置参与派生的 RailControlPoint，见 [06](06-railway.md)。
- 允许多个接轨接口不代表首版实现多站台运营或复杂调度。

### Layout 放置

- 手工 Layout 首版允许 0°、90°、180°、270°；不默认允许 Mirror、Arbitrary Rotation、Scale（转录 Q94）。
- Layout 拥有 Site Constraints：footprint、最大高度差、biome 或 tag 要求、forbidden terrain。
- 生成器允许有限、确定性的 Site Preparation：清理装饰、小范围填土、小范围挖平；超过约束则拒绝 Candidate。

### 静态建筑占格与净空

- 静态 Building Composite 保留独立身份与局部体素网格，同时在世界网格登记实际体素占用位置。占用登记只引用建筑中的体素，不复制为 World-owned Voxel，也不改变其 Composite 所有权。
- 同一世界格不能再放置另一个地形或轨道体素。检查实际体素占用，不把建筑包围盒中的所有空格都视为占用。
- 静态建筑参与统一的世界占用与碰撞查询。RailConstructionPlan 既验证轨道格占用，也验证车身 Swept Clearance；占用与 Collision 仍按 [01](01-voxel-world.md) 区分。
- 修改、拆除或破坏建筑时，占用与碰撞查询同步反映新状态，不留下已删除体素的占位。

## 首版范围

Seed、GeneratorVersion、Terrain、Resources、三维 Settlement placement 与朝向、生成用途的 Archetype、Layout、实际建筑与资源派生统计、可选站点及 RailSocket、静态建筑占用查询、基础 Rail Graph、Physical Rail（roadmap Phase 5）；首版生成器不自动生成复杂桥梁和隧道。

## 明确延后

生产、消费、人口变化等经济行为，Faction 的设计与实现，多站台运营与复杂调度；复杂桥梁生成、复杂隧道生成、AI 铁路施工；Mirror、环路密度调节（转录）。

## 开放问题

- 未冻结参数：World Size、World Generation Retry Count（[roadmap](roadmap.md)）。
- 地图迷雾：各阶段的具体算法与 Layout 文件格式。
