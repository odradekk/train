# 09 存档

> 决策出处：转录 Q29、Q42、Q79–Q88；2026-09-05 #34 确认无状态随机与浮点往返要求；[#35](https://github.com/odradekk/train/issues/35) 确认当前外部定义绑定与落盘方式，见 [ADR-0005](../adr/0005-resolve-current-definitions-and-separate-content-versions.md)。

## 边界

本文定义存档的组成、世界重建方式与兼容策略。Save-local Definition 的来源规则见 [02](02-definitions-and-content.md)；Stateful RNG 与运行时 ID 见 [04](04-deterministic-simulation.md)。

## 不变量

- 基础世界通过 Seed + GeneratorVersion 重建，之后应用 Runtime Delta。
- 所有主要格式从现在开始保留 Version 字段。
- Next Transaction ID、Next Entity ID、Save-local Definitions 必须进入存档；首版只有无状态随机，不需要保存可推进的 RNG State。后续添加有状态随机流时，其完整状态必须进入存档。
- 旧 Save 不能因为 Generator 更新而偷偷使用新算法重新解释世界。

## 规则

### 首版存档

```text
Save
├─ SaveFormatVersion
├─ WorldSeed
├─ GeneratorVersion
├─ Runtime Delta
├─ Next Transaction ID
├─ Next Entity ID
├─ External Definition References（DefinitionId + 上次使用的 Revision）
└─ Save-local Definitions
```

后期如果加载性能出现瓶颈，用 Snapshot + Delta 作为优化。首版不做完整 Event Sourcing。

连续量以可无损往返的 JSON 数值保存，使用与文化无关的 round-trip 表示，恢复相同的 `double` 值；不截断小数位，不允许非有限值。在有效语义内容相同的前提下，验收须比较中途保存恢复与不中断运行的结果，详见 [04](04-deterministic-simulation.md)。

物理轨道的连续曲线参数、明确连接、轨道体素及其对应关系属于待恢复的领域状态，见 [06](06-railway.md)。基础部分可由相同生成基底精确重建，修改与缺损由 Runtime Delta 恢复；不能只保存剩余占格后重新拟合曲线。恢复后须保持原有形状与缺口，渲染网格、弧长采样表和压缩路网仍是派生数据。

### 列车与轨道引用恢复

- 保存实际编组、车辆连接与朝向、活动车头、参考接触点及其 RailPosition、速度和停车状态，并保留恢复同一沿轨状态所需的物理轨道信息。RailPosition 使用稳定物理轨段身份与沿程位置，不保存派生压缩 Edge 身份作为位置依据。
- 轨段即使已全毁，只要仍被列车引用，也须能够恢复身份与定位所需几何；有效区间为空，不能借保留的曲线跨过缺口。另建轨道不能因为位置接近而接替旧身份。
- 无动力滑行恢复相应运动状态；因断轨停车的编组恢复零速度与等待显式启动的状态。阻塞原因按实际恢复后的世界重新检查，修复或读档均不自动启动，不恢复停车前的旧速度。
- 无法恢复必需物理轨段引用或解释编组状态时明确加载失败，不吸附到最近轨道，也不悄悄删除车辆。没有支撑但有完整定位依据的已停车状态，不等同于缺失引用。
- 渲染插值与相机显示状态不是列车运动权威；正常行驶、断开滑行、换端后和断轨停车均须通过 [04](04-deterministic-simulation.md) 的存档恢复一致性验收。

### 定义绑定与恢复

- 外部定义只保存 DefinitionId 和上次使用的 Revision，后者用于识别变化；不嵌入旧外部定义副本。加载时按 ID 使用当前安装的内置、Mod 或外部 Blueprint 定义。
- 玩家自定义车辆的布局及其所需的其他存档内定义内嵌保存；其中的外部零件按 ID 引用当前定义，不固定目标 Revision。世界实际布局、实例身份与运行状态从存档恢复，不被更新后的设计模板覆盖。
- 当前规格变化时保留状态绝对值，仅按新的允许范围修正，重新计算相关能力；耐久上限升高不自动修复、上限降低时截断。具体规则见 [02](02-definitions-and-content.md)。
- 缺失必需定义或无法解释保存的状态时明确加载失败；这也包括缺失 Mod 或必需资产导致所需内容包无法通过校验的情况。不自动替换、删除或回退到旧定义。更新 Mod 数值后产生不同模拟结果是预期行为，不属于无损往返失败。
- 若生成内容更新改变了重建基底，使旧 Runtime Delta 无法正确恢复原实际布局，按不兼容读档失败处理；不能把旧 Delta 静默应用到不同布局后当作成功恢复。

### 文件位置

- 存档：`user://saves/<存档 UUID>/save.json`。
- Blueprint：`user://blueprints/<蓝图 UUID>.composites.json`，复用 Composite 定义格式。
- 文件名中的 UUID 稳定不变，显示名称保存在文件内；重命名不改变身份或引用。Save-local 不作为独立内容目录扫描。

### 存档兼容

- 开发阶段不保证存档兼容。
- 进入正式发行阶段后，存档向前迁移成为硬性约束；届时可采用保存旧 Generator、Migration、Snapshot 等方式保证兼容。

## 首版范围

Save-local Definition、Runtime Delta、WorldSeed、IDs、World reconstruction、double 无损往返；达到正常 Save → Quit → Load → Continue（roadmap Phase 8）。

## 明确延后

Snapshot Save Optimization、正式版本 Save Migration；缺失 Mod 的兼容与回退（转录 Q42、Q88）。

## 开放问题

- 地图迷雾：存档格式细节与 Snapshot。
