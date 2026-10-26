# 核心模拟层公开契约

本页落实[核心模拟层公开契约：命令、查询、事件与复现](https://github.com/odradekk/train/issues/287)，并经[契约审计修订](https://github.com/odradekk/train/issues/296)收窄，与[公共约定](conventions.md)、[跨层事务](transactions.md)和[编辑协议](editing.md)一起使用。玩法以设计文档为准，本页说明调用方法及其责任。

- [core-construction.md](core-construction.md)：施工能力输入与执行交接（#501）；自有车辆施工；车辆立即应用、改装与拆解（#332）；建筑施工与大型设备（#343）；NPC 车辆委托与施工报价。
- [core-status.md](core-status.md)：交付状态与入口；验证与性能责任。
- [core-settlements.md](core-settlements.md)：聚居地与经济查询；建城与玩家聚居地；宣战与占领；士气、投降与恢复。
- [core-production.md](core-production.md)：加工与回收；节点资源。
- [core-values.md](core-values.md)：所有权与数值。
- [core-lifecycle.md](core-lifecycle.md)：创建、恢复与销毁；阶段一生成器。
- [core-queries.md](core-queries.md)：读视图、查询与事件。
- [core-trains.md](core-trains.md)：当前列车运行。
- [core-combat.md](core-combat.md)：战斗。
- [core-inventory.md](core-inventory.md)：库存与租约。
- [core-market.md](core-market.md)：市场报价与手动交易；备货与查询；货物报价与执行；现车。
- [core-commands.md](core-commands.md)：命令与执行。
- [core-content-sync.md](core-content-sync.md)：内容候选与同步提交。
- [core-capture.md](core-capture.md)：捕获、编码与录制；当前载荷。
- [core-recording.md](core-recording.md)：自动交易与运行报告；完整领域状态与录制。
- [core-replay.md](core-replay.md)：严格重放。
