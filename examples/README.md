# 普通示例存档

`basic-editing.world.json` 是格式 5、来源为 blank v1 的普通存档。用途与起始适用版本只在[示例清单](../Domain/WorldEditor/WorldEditorExamples.cs)中维护。真实内容依赖为内置 `core:ferrous-ore`；所用语义修订与耐久上限记录在文件的 `external_definitions` 中，不要求安装 Mod。

制作步骤使用正式领域操作，不直接修改世界或编写存档字段：

1. `BlankWorldSource.Establish` 建立范围 16 × 8 × 16、种子 42 的空白世界。
2. `VoxelDevelopmentSession.Add` 在 (2,1,2)、(3,1,2)、(4,1,2) 放置 `core:ferrous-ore`，朝向为 `VoxelOrientation.Identity`。
3. `SubmitEdit` 将 (5,1,2) 的同类体素放置命令排入模拟步 3。
4. `AdvanceSimulation(1)` 推进到步 1，再用 `Save` 保存为“体素编辑与待执行命令”。分发的是该保存产生的完整 JSON。

打开示例时只读取该文件，并通过 `WorldSaveCodec.TryDeserialize` 和 `LoadDocument` 的正式来源、依赖、世界状态校验恢复；不执行以上制作步骤。工作区按 `WorldEditorExamples.All` 明确列出分发文件，不扫描测试数据或任意 JSON。内容依赖从实际存档读取。新增示例须加入清单，并使用 `.world.json` 后缀以进入导出包。

副本进入同一个暂停编辑器，保留世界、内部身份、模拟位置和待执行命令，首次保存前没有工作存档 UUID。返回工作区再选择同一个示例即可重新开始；返回仍经过现有保存后继续／放弃／取消保护。

人工检查：主菜单进入世界编辑器，核对示例与本地存档分列及用途、起始适用版本、依赖提示；选择从示例创建副本，核对内容在初始视野内并可直接观察，同时确认暂停和未保存状态；编辑或运行后返回工作区，分别检查取消、保存后继续和放弃；再次选择示例，核对恢复到原始内容；首次保存副本后回到工作区，核对新本地存档可以独立打开，原示例仍保持原始内容。
