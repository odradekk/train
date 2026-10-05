# 16 聚居地

> **所属层**：核心模拟层；聚居地的体素表现属于节点场景。
>
> **修订中**：本文由 05 拆出，原样保留以全局体素世界为前提的聚居地规则，由[聚居地的经营、建设与场景](https://github.com/odradekk/train/issues/206)改写；“聚居地与铁路”中的连通部分已由[宏观世界模型：节点、边与地图生成](https://github.com/odradekk/train/issues/200)和[列车在图上的运行与调度](https://github.com/odradekk/train/issues/203)取代：聚居地占据节点，通过线路连通，列车在节点占用站台，见 [05](05-world-graph-and-generation.md) 和 [06](06-railway.md#节点与站台)。“静态建筑占格与净空”依赖全局世界网格，已被 [ADR-0008](../adr/0008-simulate-the-world-in-a-voxel-free-core-and-build-voxel-scenes-as-presentation.md) 取代。改写完成前，上述规则不能作为实现依据；其余规则继续有效。

> 决策出处：转录 Q89–Q95、Q102、Q178；2026-09-05 #30 确认 Faction 延后，[#36](https://github.com/odradekk/train/issues/36) 确认位置、生成分类、建筑与资源事实来源、接轨及静态占格。2026-10-05 由 05 拆出，见[新的设计文档结构](https://github.com/odradekk/train/issues/199)。

## 边界

本文定义聚居地的结构、聚居地与铁路的关系、Layout 放置和静态建筑占格。世界生成见 [05](05-world-graph-and-generation.md)；铁路的拓扑、图与施工见 [06](06-railway.md)；聚居地的生产、库存与设施见 [12](12-inventory-and-storage.md)、[13](13-production-runtime-and-facilities.md)，市场与人口规则留待后续扩充；术语以 `CONTEXT.md` 为准。

## 不变量

- Settlement exists ≠ Station exists ≠ Rail connection exists。
- 聚居地类型（SettlementArchetype）与空间布局（Layout）严格分离；本阶段 Archetype 只承担生成用途，经济行为延后。
- 聚居地位置只有一份三维体素锚点；建筑数量与资源种类反映实际世界，不由 Layout 模板另存一份权威清单。

## 规则

### 聚居地

- 聚居地本身不是 Composite。本阶段模型记录 SettlementId、Archetype、三维布局锚点、朝向、实际建筑实例引用及可选 Station，保留既有的静态常住人口数据及其正数约束；聚居地市场模拟与人口模拟属于 Demo，具体设计待定。Faction 不在 Demo 内，模型不保留阵营占位字段。聚居地的生产、库存、Power Pool 与 Labor Pool 接口由 [12](12-inventory-and-storage.md)、[13](13-production-runtime-and-facilities.md) 定义，能力来自建筑 Composite 内的真实设备 Voxel，聚居地不拥有无来源的抽象产能。
- **SettlementArchetype** 与术语表的聚居地类型是同一概念，不维护两套分类；本阶段描述候选 Layout 及选择条件，按配置权重、地形和占地条件选择，资源偏好留到资源生成阶段（[#95](https://github.com/odradekk/train/issues/95) Q3），不产生经济模拟行为。
- **SettlementLayoutDefinition** 负责空间布局：Building Composite、World Voxel、Decoration、Site Constraints、optional Station/Rail Socket。建筑为 Composite，道路与装饰为世界体素（转录 Q93）。

### 位置与布局实例

- 聚居地位置采用 Layout 锚点的世界三维整数体素坐标，布局朝向单独保存。高度由生成器结合地形与 Layout 约束确定。
- 二维地图需要位置时从三维锚点投影，不继续独立维护旧 `MapPosition(X, Y)`。当前 `Settlement` 仍保存二维位置和直接设置的按类型建筑计数，实现生成时一并改为三维锚点和按实例派生的统计。
- Layout 只是生成模板，放置后形成实际建筑实例；聚居地记录这些实例，建筑数量按当前实例的建筑类型派生汇总，站点不计入普通建筑数量。
- 制造、拆除或破坏改变实际建筑集合后，统计反映新状态；修改 Layout 模板不自动重建已有聚居地。
- 静态建筑断裂后仍只记录原来的一个建筑，整体失效，不按分离部分增加计数；所有核心毁坏后移除原引用，重建时记录新身份。碎块归属及修复规则见 [03 的建筑契约](03-vehicles-buildings-and-designs.md#本阶段静态建筑契约)。

### 聚居地与铁路

- Demo 中每个聚居地有零个或一个站点，允许没有站点或尚未接入铁路的聚居地；生成配置可显式指定需要接轨的站点，不要求贸易系统参与。
- 站点通过一个或多个 RailSocket 描述接轨位置和方向。Layout 可放置站点及其接口；RailSocket 是站点接轨能力的描述，不是聚居地身份的必要条件（转录 Q178 修正 Q102）。
- RailSocket 不等于物理轨道的 Track Port，也不自动创建铁路连接或压缩图节点。实际连通性由物理轨段的明确连接与当前有效区间决定，不能从轨道格邻接关系猜测；Station 作为重要位置参与派生的 RailControlPoint，见 [06](06-railway.md)。
- 铁路生成先规划连续几何，再登记钢轨、轨枕实体占格并验证车辆扫掠净空。空间不足时调整线路或拒绝方案，不生成单格急弯或默认 1:1 坡；遵循 [06 的首批标准](https://github.com/odradekk/train/blob/62c40d2/docs/design/06-railway.md#首批铁路几何与机械参数)，未完成正式验证的线路组合不默认开放。五档合规车辆共用正常生成且完整的线路。
- 允许多个接轨接口不代表 Demo 实现多站台运营或复杂调度。
- 本阶段一个站点对应一个实际建筑，沿用 [03 的建筑失效规则](03-vehicles-buildings-and-designs.md#本阶段静态建筑契约)：断裂使整个站点失效，停止接受新的停靠。接轨接口绑定实际部件，部件消失时对应接口失效；站点身份、建筑可用状态、接轨接口和轨道连通性分别检查。站点受损不自动删除物理轨道，完整且满足其他运行条件的线路仍可通行。
- 接口声明在生成时落实到实际部件绑定；更新 Layout/Blueprint 不重写已放置站点的绑定。接口部件被修复后重新校验接轨状态，不凭位置相近创建轨道连接。站点失效或引用撤销不清除列车实际占用与必要通行保护，运行层继续遵守 [06](https://github.com/odradekk/train/blob/62c40d2/docs/design/06-railway.md#本阶段网络运行契约)。

本阶段不含资源生产的生成契约，还须消费 [06 的网络运行契约](https://github.com/odradekk/train/blob/62c40d2/docs/design/06-railway.md#本阶段网络运行契约)：面向声明支持的编组长度、方向和负载，提供实际可通行的站点路径、整列等待空间与会让条件。仅压缩图连通不能证明多列车能持续运行；具体拓扑、配置与失败条件由[地图与铁路生成契约](https://github.com/odradekk/train/issues/95)确定。此要求不强制每个聚居地设站或接轨，也不引入多站台和复杂调度。

### Layout 放置

- Demo 中手工 Layout 允许 0°、90°、180°、270°；不默认允许 Mirror、Arbitrary Rotation、Scale（转录 Q94）。
- Layout 拥有 Site Constraints：footprint、最大高度差、biome 或 tag 要求、forbidden terrain。
- 生成器允许有限、确定性的 Site Preparation：清理装饰、小范围填土、小范围挖平；超过约束则拒绝 Candidate。
- 世界编辑器中的 Layout 编排、从实际对象提取及手工原子放置遵守 [02 的内容创作流程](02-definitions-and-content.md#蓝图与-layout-创作)。生成配置显式选择候选 Layout；发布新模板不自动改变候选集或已有世界，模板合法也不代替标准铁路地图的联合验证。

### 静态建筑占格与净空

- 静态 Building Composite 保留独立身份与局部体素网格，同时在世界网格登记实际体素占用位置。占用登记只引用建筑中的体素，不复制为 World-owned Voxel，也不改变其 Composite 所有权。
- 同一世界格不能再放置另一个地形或轨道体素。检查实际体素占用，不把建筑包围盒中的所有空格都视为占用。
- 静态建筑参与统一的世界占用与碰撞查询。RailConstructionPlan 既验证轨道格占用，也验证车身 Swept Clearance；占用与 Collision 仍按 [01](01-voxel-engine-and-scenes.md) 区分。
- 修改、拆除或破坏建筑时，占用与碰撞查询同步反映新状态，不留下已删除体素的占位。
- 建筑整体使用整数锚点和四向水平旋转，不缩放，不套车辆五档尺寸。新建、改装和完整修复均验证实际占格与既有铁路净空；冲突时原子拒绝，没有列车正在经过也不能破坏完整线路的通行条件。建筑空洞按真实空格处理；体素采用 None 或局部碰撞盒不免除整格占用及整格铁路净空检查。
- 建筑碎块原位转为世界体素，或修复时将符合条件的原位残块收回，均按 [03](03-vehicles-buildings-and-designs.md#本阶段静态建筑契约)原子变更唯一所有者及其占用登记。修复目标描述期望完整设计，不替代当前实际布局，也不预占已缺失格子。

## Demo 范围

聚居地市场模拟与人口模拟属于 Demo，具体设计待定。聚居地的取得、经营与建设见 [00](00-product-and-core-loop.md#开局与聚居地)。

## Demo 之后

Faction 的设计与实现；多站台运营与复杂调度；Layout 的 Mirror。

## 开放问题

- 由[聚居地的经营、建设与场景](https://github.com/odradekk/train/issues/206)改写，见开头的修订说明。
