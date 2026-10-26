# 核心模拟层：命令提交与执行

本页记录命令提交与执行，与[契约目录](core.md)一起使用。

## 命令与执行

命令输入是封闭的类型目录，不提供字符串路由加任意 JSON 的扩展口。全部命令在协调线程同步 `Submit`，包括 `FoundSettlement` 和携带完整固定投影的 `PublishContent`；核心可以在内部使用候选保证原子性，不公开命令准备对象或另一个完成入口。控制台先在宿主解析参数，再调用相同命令，不访问核心实现。

运行计划的 `PlanStop.Waypoint` 只有途经节点；`PlanStop.Station` 才携带停留步数及有序货物规则。`AutomaticGoodsRule` 共用货物、库存和目标数量，`AutomaticGoodsAction.Buy`／`Sell` 分别携带可选逐件付款上限／收款下限，空值表示无限价；`Load`／`Unload` 不携带价格条件。成交结果和报价方向继续使用 `TradeAction`。

| 领域 | 公开命令及限定 |
| --- | --- |
| 铁路 | 编组/解编、有序运行计划、启动/停车、现有线路加轨、断点修理、自动修理来源；没有新建任意图边或拆轨命令 |
| 贸易 | 足量货物买卖、免费装卸、实际车辆买卖、租赁/续租；手动总额付款上限/收款下限，自动规则逐件限价 |
| 自动交易 | 放在运行计划的有序停靠规则内；每站每货只一项动作，关系改变不会改写成另一种动作 |
| 生产 | 设施能力、带类型流程、启用状态和优先级；无批次取消退款、库存凭空补充或 NPC 生产规划 AI |
| 建设 | 自有任务或原子 NPC 委托，目标设计/地块/实例、显式来源顺序、执行设施；建造、改装、修理、拆解及任务优先级 |
| 编辑 | `ApplyInstanceDesign` 只换完整合法且构成未变的设计；构成变化走整份改装，无混合部分应用 |
| 聚居地 | 空节点建城、宣战、明确占领；无外交恢复、人口搬迁或配给开关 |
| 战斗 | 列车/聚居地交战规则、列车撤退和集中攻击；无直接指定伤害结果的玩家命令 |
| 内容与开发 | 固定候选的 `PublishContent`；开发构建允许真实断点调试命令，其他调试观察在宿主使用正常查询和驱动 |

`JobRequest` 不接受草稿或运行状态。建造的大型设备以`DesignKind.LargeEquipment.ManufacturedGood` 入库，不创建车辆。委托只允许车辆建造、改装、修理，设施必须有资格；受理顺序决定 NPC 队列，不能用请求的优先级插队。拆解可以主动暂停，其他三类没有主动暂停/取消命令。续租沿用原容量并重新校验，不自动部分缩减；相应容量、合同和时序遵循[租赁规则](../design/12-inventory-and-storage.md#租用库存与共有容量)。

提交结果分两类：

- `NotAccepted`：冻结、命令身份耗尽或无效的预备基准等使入口不再接收，不取得命令编号。
- `Completed`：回执包含编号、前后位置及 CommandDecision：Accepted 携带新建身份与模板副本清单，Rejected 携带业务问题，Faulted 携带稳定故障详情；不再另存一份可矛盾的故障状态与空值字段。正常拒绝不改业务状态，但前进命令编号和应有序号，留在录制中。

命令和每个模拟步各自原子。命令内部故障回到命令前业务边界并冻结，失败步骤回到该步前边界并冻结；先前完成步骤不回滚。诊断堆栈不进入确定性摘要。`Advance(steps)` 返回实际完成步数和事件，首个故障停止，不私自重试。暂停和倍速不影响同一命令在同一边界的结果。

`CoreProblem.Code` 是稳定、非本地化代码。共同代码为 `object_missing`、`content_missing`、`invalid_state`、`not_owned`、`access_denied`、`insufficient_goods`、`insufficient_capacity`、`insufficient_money`、`price_condition_failed`、`busy`、`frozen`、`identity_exhausted`、`content_changed`、`unsupported_format`、`incompatible_replay`、`generation_failed`；字段路径及相关身份补足定位。更多领域原因在本模块扩展，不允许调用方解析诊断文本。非法枚举、未初始化数组、不满足声明数值范围或使用已释放/已消费的对象属于编程错误，可抛 `ArgumentException`、`ObjectDisposedException`、`InvalidOperationException`。正常玩法拒绝不抛异常。
