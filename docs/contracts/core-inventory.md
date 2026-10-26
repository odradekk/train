# 核心模拟层：库存与租约约定

本页记录库存与租约约定，与[契约目录](core.md)一起使用。

## 库存与租约

`FindInventory`、`FindLease` 的身份必须非零；对象不存在返回 `Lookup.Missing`。结果及其集合可以保留，后续命令、推进、发布或会话释放不改变旧结果。库存只有按 `GoodId` 计数的栈，三种身份不相互合并。缺定义货物保留身份和数量、照常占容量，`DefinitionMissing=true`，装卸拒绝 `content_missing / Good`。库存质量只包含已知定义的部分，不作为容量约束。

容量来自有效结构耐久及部件耐久均大于零、定义未缺失的存储部件。列车汇总成员车辆，聚居地汇总所有建筑，不按建筑类别限制。城内统计只含聚居地库存与当地租用库存，不含停靠列车。设物理容量为 P、有效租约额度合计为 A、到期租约货物合计为 E，聚居地库存容量为 `max(0, P-A-E)`。有效租约扣完整额度，不重复扣其中货物；到期只保留实际货物占位。

`InventoryView.Used` 为整件数量合计，`Reserved` 为预留容量合计，`Reservations` 保留 JobId、容量和预计返还。列车 Available 为 `max(0, Capacity-Used-Reserved)`；城内库存还取与 `max(0, P-全城Used-全城Reserved)` 的较小值。租用库存 Capacity 保留合同额度；非 ReadWrite 库存 Available 为零，到期显示的额度不构成继续占有空空间的承诺。能力下降不删货、不删预留，超容禁止新增入库，合法取出仍允许。

可租比例额度为 `floor(P×RentableCapacityRatio/10000)`，只计有效租约。实际承诺空间为聚居地库存的 Used+Reserved，加各有效租约的 `max(合同额度, Used+Reserved)`，再加各到期租约的 Used+Reserved；可租容量为比例剩余额度与实际未承诺空间的较小值，各项最低为零。到期预留继续占位。续租排除自身旧额度及占位，再检查原容量和自身 Used+Reserved，不重复计入；不自动缩减额度。

库存访问权独立于直接装卸授权。玩家列车、自有聚居地库存、有效租用库存及非敌对 NPC 聚居地库存显示 ReadWrite；到期租库为 WithdrawOnly；敌对聚居地库存和租库为 Frozen，优先于到期。冻结不释放额度、货物或预留。直接装卸只允许列车与同地自有聚居地库存或玩家租库之间移动，NPC 库存交易归 #384。

装卸要求非零身份、合法货物身份及正数量，否则取得编号前抛 ArgumentException。合法输入依序检查：对象存在（object_missing / Train、Settlement、LocalInventory）；列车所有权（not_owned / Train）；同地（train_not_at_settlement / Train.Position）；当地库存归属（invalid_state / LocalInventory）；冻结（access_denied / LocalInventory）；NPC 库存（not_owned / LocalInventory）；卸入到期租库（access_denied / LocalInventory）；在 Station 停留中（`Servicing`）、停稳在 S（`Docked` 且速度为 0）并持有站台预约（platform_required / Train.Position）；缺定义（content_missing / Good）；来源数量（insufficient_goods / Quantity）；目标及全城容量（insufficient_capacity / Train.Inventory.Capacity 或 LocalInventory.Capacity）。返回首个拒绝，不抢占站台，不新增站台状态。成功发一个 GoodsTransferred，Action 为 Load/Unload，双方库存和数量为实际完整转移，ActualAmount 为零；拒绝不改双方货物或资金，无业务事件。

Rent 要求非零聚居地身份和正容量，Renew 要求非零租约身份，两者期限必须为正；AcquireLease.MaximumPayment 必须非负，否则取得编号前抛 ArgumentException。命令解码拒绝负付款上限，代码为 invalid_state，字段为 /maximum_payment。只允许在非敌对 NPC 聚居地租用；敌对拒绝 access_denied / Request.Settlement，自有拒绝 invalid_state / Request.Settlement。其后依序检查市场规则（content_missing / MarketRule）、期限必须为当前 `LeaseDurationTicks` 的整数倍且倍数为 1 至 `MaximumLeaseDurationMultiple`，续租后的剩余租期也不得超过两者乘积（非整数倍、倍数超限、续租后剩余超限均为 invalid_state / Request.DurationSteps）、容量（insufficient_capacity / Request.Capacity）、付款上限（price_condition_failed / MaximumPayment）、钱包（insufficient_money / Wallet）及新租身份（identity_exhausted / World.Next.Lease 或 World.Next.Inventory）。对象不存在使用 object_missing / Request.Settlement 或 Request.Lease。

租金单位为最小货币整数单位/容量/模拟小时，总额为 `ceil(单价×容量×期限步数/216000)`，整份只向上取整一次，不逐期取整，无最低一元。期限上限和续租剩余期限的比较不会因合法规则值或请求期限过大而溢出。仅在规则存在、期限合法且容量检查通过后计价；到期步加法、checked Int128 计价中间计算和结果转换溢出沿用内部故障。有效续租从原到期步追加，到期续租从当前完成步起算，沿用容量并使用当前规则单价。LeaseView.Paid 是累计实际付款，LeaseAcquired.ActualPayment 为本次付款。QuoteLease 不锁资源；规则缺失、期限不合法或容量不足（含续租自身货物及预留占位放不下）时 Total 为空，其余关系、钱包等拒绝仍给总价和 Blockers。付款上限由 AcquireLease 重新检查，报价请求没有付款上限参数。报价计算故障返回 CoreResult.Faulted，不冻结活动会话。

从完成步 n 推进到 n+1 时，期限落在该边界的租约按 LeaseId 升序发 LeaseExpired，事件排在同一步铁路事件之前。到期状态由完成步派生，恢复不重复发事件，空租约及其库存不自动删除。市场规则缺失不暂停到期，也不禁止其它条件合法的手动装卸。规则更新不追改已付金额、合同额度或到期步。

预留随库存保存、恢复并参与检查点。解码检查正容量、返还货物唯一且规范排序、正数量及合计不超过容量、JobId 非零且全世界唯一；预计返还货物定义加入引用清单。#332 的车辆改装、拆解，以及 #343 的建筑改装、拆解和大型设备制造，创建和释放预留；任务与返还库存中的预留按任务编号严格双向配对。车辆与建筑的制造、修理不创建返还预留，大型设备制造预留一件成品。本票不新增控制台命令，单命令手册和索引更新不适用。
