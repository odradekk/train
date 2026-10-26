# Hand off presentation assets as raw bytes keyed by asset revision

2026-10-10 经[资产访问与导入：内容、体素和 Godot 的交接契约](https://github.com/odradekk/train/issues/286)的 Q1–Q15 确认：各层按资产修订引用贴图和模型；内容层保存资产的原始字节，随内容世代一起发布；内容层通过自己定义、由 Godot 实现的资产检查接口取得元数据，再执行全部资产规则；解码后的像素、模型数据和 GPU 资源只存在于 Godot 中。这样纯 .NET 模块不引用 Godot，通过校验的字节就是上传的字节，规则只写在内容层一处。

规则见[设计 02 的资产的交接](../design/02-definitions-and-content.md#资产的交接)，各模块的责任见[契约索引的表现资产的交接](../contracts/README.md#表现资产的交接)，模型文件的约定见[体素定义的格式](../content/voxel.md#模型文件的约定)。

## Considered Options

- **按“包 + 路径”引用资产**：改文件名或几个定义共用一张图时会重复建资源，Godot 还要自己判断文件有没有变。按内容寻址时，相同字节天然只有一份。
- **内容层保存解码后的像素交给 Godot**（原有代码的做法）：像素在纯 .NET 里多存一份，模型还要另定一种纯 .NET 网格格式。
- **内容层不留字节，Godot 上传时按路径重读**：校验后文件被改过时，画面用的就不是通过校验的那一份。
- **内容层用纯 .NET 自己解析 PNG 和 GLB**：只读文件头查不出数据损坏；完整解码要引入新的库，两套解析器对同一个文件还可能得出不同结论。

## Consequences

- [ADR-0004](0004-keep-the-world-model-as-the-single-source-of-truth-and-treat-godot-as-a-derived-adapter.md) 所说的 AssetId 就是资产修订。
- 修订 [ADR-0011](0011-move-the-content-layer-into-its-own-project-and-split-definitions-by-reader.md)：Godot 向内容层提供的“PNG 解码”改为资产检查（PNG 和 `.glb`），只返回元数据。旧像素解码路径已在 [#452](https://github.com/odradekk/train/issues/452) 退役；当前工厂使用资产检查元数据并保留原始字节，见[内容契约](../contracts/content.md)。
- 资源创建不参与内容能否发布的决定。通过检查的字节由同一个解码器上传，之后再失败按引擎故障报告，不回滚内容。
- 内容资产不使用 Godot 编辑器的导入产物，内置的 PNG 和 `.glb` 用 Keep File 导入。`.glb` 的 Keep File 导入目前只有 Godot 源码依据，由[契约原型](https://github.com/odradekk/train/issues/292)在 4.7.2 上验证。
- 本决定不代表接口已经实现。具体声明由内容层、体素引擎层和 Godot 层的契约票写进各自的接口文档。
