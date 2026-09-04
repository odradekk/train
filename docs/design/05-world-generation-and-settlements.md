# 05 世界生成与聚居地

> 决策出处：转录 Q89–Q95、Q102、Q178。

## 边界

本文定义程序生成流水线、资源生成、聚居地的结构、聚居地与铁路的关系、Layout 放置。铁路的拓扑、图与施工见 [06](06-railway.md)；聚居地的经济规则留待后续扩充；术语以 `CONTEXT.md` 为准。

## 不变量

- 同一 WorldSeed + GeneratorVersion 必须产生同样的初始世界；不同 GeneratorVersion 不要求得到相同地图。
- 后续阶段新增 RNG 调用不能改变前面阶段的结果。
- Settlement exists ≠ Station exists ≠ Rail connection exists。
- 经济类型（Archetype）与外观 Layout 严格分离。

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

- Resource Potential Field → Actual Resource Voxels。Potential Field 用于宏观判断哪些区域矿产丰富、聚居地更适合发展什么经济。
- 聚居地类型原则上响应自然资源，而不是为了聚居地类型强行生成资源；允许少量 Gameplay Validation 和保证性调整。

### 聚居地

- 聚居地本身不是 Composite。SettlementInstance { Economy, Population, Faction, Position, Buildings, optional Station }。
- **SettlementArchetype** 负责 Gameplay：经济、资源偏好、生产、消费、其他规则。
- **SettlementLayoutDefinition** 负责空间布局：Building Composite、World Voxel、Decoration、Site Constraints、optional Station/Rail Socket。建筑为 Composite，道路与装饰为世界体素（转录 Q93）。

### 聚居地与铁路

- 首版世界生成可以要求所有参与自动铁路贸易的聚居地都有站点并接入 RailNetwork；底层允许未来存在未通铁路的聚居地。
- RailSocket 是站点与铁路接入能力的合法性要求，而不是聚居地身份本身的必要条件（转录 Q178 修正 Q102）。

### Layout 放置

- 手工 Layout 首版允许 0°、90°、180°、270°；不默认允许 Mirror、Arbitrary Rotation、Scale（转录 Q94）。
- Layout 拥有 Site Constraints：footprint、最大高度差、biome 或 tag 要求、forbidden terrain。
- 生成器允许有限、确定性的 Site Preparation：清理装饰、小范围填土、小范围挖平；超过约束则拒绝 Candidate。

## 首版范围

Seed、GeneratorVersion、Terrain、Resources、Settlement placement、Layout、基础 Rail Graph、Physical Rail（roadmap Phase 5）；首版生成器不自动生成复杂桥梁和隧道。

## 明确延后

复杂桥梁生成、复杂隧道生成、AI 铁路施工；Mirror、环路密度调节（转录）。

## 开放问题

- [定义聚居地在体素世界中的模型](https://github.com/odradekk/train/issues/36)：`CONTEXT.md` 的地图位置是二维整数逻辑坐标，代码为 `MapPosition(X, Y)`，而世界是三维体素坐标，聚居地的位置是什么；聚居地类型与 SettlementArchetype 是否同一概念，术语表"分类暂不产生模拟行为"如何更新；Layout 与建筑数量、自然资源种类的关系；Station 与 RailSocket 的关系；静态 Building Composite 是否占据 World Cell。
- [补齐首版范围缺口](https://github.com/odradekk/train/issues/30)：Faction 的首版含义。
- 未冻结参数：World Size、World Generation Retry Count（[roadmap](roadmap.md)）。
- 地图迷雾：各阶段的具体算法与 Layout 文件格式。
