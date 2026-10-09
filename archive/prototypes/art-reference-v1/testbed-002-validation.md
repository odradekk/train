# Voxel Art Testbed 002 浏览器验证报告

> 由整理稿 `TRAIN-Art-Design-v1.0.html` 内的报告原文转写成 Markdown，检查项名称保持原文。原文件 `TRAIN-Testbed-Validation.md` 没有随初稿提供，其 SHA-256 见[本目录说明](README.md#来源文件指纹)。迁入时没有重新运行这些检查。

结果：52 项检查全部通过。

运行环境：Chromium 144，WebGL 2.0，ANGLE / SwiftShader 软件渲染。加载方式：对打包后的 HTML 字节执行 `page.set_content`。报告不证明用户设备的帧率、正式核心模型或 Godot 实现。

| 检查 | 结果 |
| --- | --- |
| Standalone HTML initializes the real WebGL renderer | 通过 |
| military: integer Cells, unique IDs, valid envelope and definitions | 通过 |
| military: initial grid forms one 6-connected component | 通过 |
| civil: integer Cells, unique IDs, valid envelope and definitions | 通过 |
| civil: initial grid forms one 6-connected component | 通过 |
| depot: integer Cells, unique IDs, valid envelope and definitions | 通过 |
| depot: initial grid forms one 6-connected component | 通过 |
| Every static shape and fan swept envelope is strictly within its Cell | 通过 |
| 24 proper orthogonal installation orientations | 通过 |
| Every shape remains inside Cell under all 24 orientations | 通过 |
| Base texture uses 128 texels per four Cells with mipmaps | 通过 |
| Real canvas pointer selection resolves an instanced Voxel | 通过 |
| Single-Voxel paint changes only the selected paint value | 通过 |
| Painting preserves Voxel identity and durability | 通过 |
| Paint mask keeps glass, metal and lights untinted | 通过 |
| Batch paint applies to every instance of the selected definition | 通过 |
| Replacement creates new Voxel identity at same Cell | 通过 |
| Half slab still forbids a second occupant in its Cell | 通过 |
| Undo restores original identity and definition | 通过 |
| Redo restores replacement | 通过 |
| Removal updates source data and removes all visible layer instances | 通过 |
| Rotation stays in 24 orientations and preserves coordinates | 通过 |
| Add legal voxel through common transaction API | 通过 |
| Out-of-envelope placement is rejected atomically | 通过 |
| Test durability drives per-instance damage attributes | 通过 |
| Repair clears damage without erasing normal wear settings | 通过 |
| Zero test durability removes the voxel | 通过 |
| Experiment JSON roundtrips all edited voxels | 通过 |
| Malformed import leaves all three fixtures unchanged | 通过 |
| Mapping uv updates real shared shader uniforms | 通过 |
| Mapping tri updates real shared shader uniforms | 通过 |
| Mapping checker updates real shared shader uniforms | 通过 |
| Lighting day renders without scene rebuild | 通过 |
| Lighting overcast renders without scene rebuild | 通过 |
| Lighting night renders without scene rebuild | 通过 |
| Quality low resizes render targets | 通过 |
| Quality high resizes render targets | 通过 |
| Quality balanced resizes render targets | 通过 |
| UI specimen switch civil | 通过 |
| UI specimen switch depot | 通过 |
| UI specimen switch military | 通过 |
| Screenshot is encoded as a real PNG | 通过 |
| Rigid/rotor animation does not rewrite fixture coordinates | 通过 |
| Rigid motion actually changes the render root transform | 通过 |
| Desktop-strip canvas really becomes 300 CSS pixels high | 通过 |
| Catalog displays all obtainable definitions in a read-only specimen tray | 通过 |
| Responsive viewport 1280x800 has usable canvas | 通过 |
| Responsive viewport 820x1000 has usable canvas | 通过 |
| Responsive viewport 390x844 has usable canvas | 通过 |
| No external resource requests | 通过 |
| No browser page exceptions | 通过 |
| No WebGL shader or console errors | 通过 |
