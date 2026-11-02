# 规模输入

`scene-scale.py` 生成四档输入，再单独核对两档近景。生成和核对均要求源码干净。输出使用 `TestResults/` 或仓库外的新目录、空目录。

```powershell
python scripts/measure/scene-scale.py generate --output TestResults/scene-scale-inputs
python scripts/measure/scene-scale.py verify --input TestResults/scene-scale-inputs --output TestResults/scene-scale-verification
```

`generate` 构建 CLI 和 `Tools/SceneScale/`，生成 1／10／30／100× 普通存档。倍率是远处建筑占用格数的目标，清单记录实际倍率。生成只运行 bounds（读取边界）、make（CLI 制作存档）、stats（计数），不运行 ready／local 近景核对。`examples/starting-trade/recipe.json` 的命令按顺序在模拟步 0 执行，模拟步 1 保存。仓库 builtin 只读，各档修改输出目录中的副本。存档包含布局和设计副本，探针用仓库 builtin 恢复核对；生成产物不提交。

焦点为节点世界原点 `(0,0,0)`。远处建筑以 core-city-hub／bastion-fortress 组合镜像放置，两侧完整地块均须避开已有地块和焦点周围 192 格圆。100× 的追加位置也须通过这一筛选。10／30／100× 扩大地面的平坦范围（flatten），高度保持为 1 格。N／C／F 分别是圆外建筑的占用格数、包含它们的世界 32³ 区块数、六邻接暴露面数。模型地面另列，程序地形的占用 N／C／F 不可从公开投影取得。`navigation_center` 只记录真实导航范围中心。

生成清单 `scale-manifest.json` 和核对清单 `scale-verification.json` 按 `input_identity` 绑定 `recipe_sha256`、`content_tree`、`save_sha256`。`content_tree.files` 记录相对 POSIX 路径及文件字节 SHA256；树哈希对排序后的“路径 + NUL + 小写文件 SHA256 + LF”UTF-8 字节再取 SHA256。跨平台优先比较配方和内容树；P2-R/F2 四档存档字节也与本机一致。verify 检查四档标识和源码身份，记录 `generation_manifest_sha256`，仅对 1／10× 运行 ready／local 近景核对。规模路径的 [P2-R/F2 分段耗时记录及使用限制](../../docs/ci.md#linux-夜间运行)已记录。

近景核对覆盖五个相机位置：原点和 X／Z 各 ±64 格。各位置的 `firstGeometry.fully_inside` 对象集合须非空。跨档比较以节点原点为圆心、半径 192 格的圆内完整对象的非轨道三角形数、哈希及全部高度列，只排除 `RailSteel`／`Sleeper` 网格；失配即停止。`railCenterline` 将公开整条轨道的 XYZ 调试折线逐格采样，裁剪到 192 格圆，按 0.001 格量化、规范端点顺序、去重，独立于网格的 128 格分段。折线不含停车轨，须用首站地块侧向边界证明停车轨在圆外，否则记为 `unknown`。

每个位置先满足帧 Ready、192 格圆不与雾区相交、每列均返回精确高度，再等待对象集合、三角形数和几何哈希连续 30 秒不变。每位置最多等待 300 秒，超时输出 `outcome=unknown` 并停止。1／10× 均隔十秒复核，期间变化即停止。这只证明观察窗口内稳定，后台地形没有公开完成状态。

`stability_samples` 的 `firstGeometry`／`secondGeometry` 记录对象标识、位姿、范围、材质、三角形及非轨道摘要，按 `fully_inside` 和 `crossing_boundary` 汇总。组哈希使用世界坐标和材质，不含会话标识。同档稳定等待和复核检查完整网格；三角形相交检查使用半径 192.001 格的保守圆。原点不传相机参数，其余位置使用 192 格投影半径。

跨界对象不参与跨档判定；`raw_mesh_comparison` 列出完整网格差异，`local_content_match` 表示近景通过。`crossing_geometry_observations` 按位姿和范围保留快照计数、哈希、首次观测及此前最后缺席时刻。首次观测仅为发布时间上界，受相机半径影响；无相交三角形的对象不入清单，缺席不证明未发布。游戏补齐仍以 `far_ready` 实测。

失败时保留 JSON、日志和分项结果。`stage` 记录具体阶段，命令失败的 `RuntimeError` 消息写入 `failure`，`failed_observation` 指向已有失败观测 JSON。

Linux 规模输入的生成档位、运行范围和产物布局见 [CI](../../docs/ci.md#linux-夜间运行)。开发机用待验收提交运行 `verify`，只打开 1／10×，两档均作十秒复核。
