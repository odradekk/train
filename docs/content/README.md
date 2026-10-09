# 内容定义标准

这里是内容包和各种内容定义的格式标准：字段说明、JSON Schema 和一个示例内容包。规则的来由见设计文档 [02](../design/02-definitions-and-content.md)、[03](../design/03-vehicles-buildings-and-designs.md) 和 ADR-0011 到 ADR-0015；字段怎样写，以这里为准。

现在的加载器还读不了这套格式，实施工单完成之前，示例只供阅读。实施工单的验收标准包括“加载器能完整加载全部示例，示例通过 Schema 校验”。

| 文件 | 内容 |
| --- | --- |
| [manifest.md](manifest.md) | 内容包的 `mod.json` |
| [voxel.md](voxel.md) | 体素定义 `<local id>.voxel.json` |
| [blueprint.md](blueprint.md) | 蓝图 `<local id>.blueprint.json`，以及被嵌套的复合体和存档共用的设计对象 |
| [settlement-type.md](settlement-type.md) | 聚居地类型 `<local id>.settlement-type.json` |
| [settlement-template.md](settlement-template.md) | 聚居地模板 `<local id>.settlement-template.json`，以及从世界生成到聚居地出现在节点上的整条链 |
| [schemas/](schemas/) | JSON Schema（draft 2020-12），一种文件一个，共用的类型在 `common.schema.json` |
| [examples/](examples/) | 示例：内容包 `example-pack`、依赖它的 `example-addon`，以及用户内容库里的一个玩家蓝图 |
| [tools/make_placeholder_pngs.py](tools/make_placeholder_pngs.py) | 按定义生成占位 PNG |

这个目录里有 `.gdignore`，Godot 编辑器不会导入这里的图片。

## 内容包的结构

```text
<包目录>/
├── mod.json
└── resources/
    ├── voxels/
    │   ├── <local id>.voxel.json
    │   ├── <local id>/            这份定义自己的资产
    │   └── shared/                几份体素定义共用的资产
    ├── blueprints/
    ├── settlement-types/
    └── settlement-templates/
```

- Mod 放在 `user://mods/<包目录>/`。内置的 `core` 包放在 `builtin/core/`，没有 `mod.json`，版本就是游戏版本。
- 加入或移除一份定义，就是加入或删除它的文件和同名文件夹。

## 通用规则

- **一个文件一份定义**：文件名是 `<local id>.<种类>.json`，根对象就是这份定义。文件里的 `id` 必须和文件名一致，不一致就报错。
- **严格 JSON**：未知字段、重复字段、注释和尾随逗号都无效。包里任何一份定义有错，整个包都不加载。
- **`schema_version`**：每种文件各自编号。当前 manifest 和聚居地类型是 2，体素定义和蓝图是 3，聚居地模板是 1。
- **local id**：1–64 个字符，只用小写英文字母、数字、下划线和连字符，以字母或数字开头。约定用连字符分隔单词，例如 `diesel-engine`。
- **引用**：同一个包里的定义直接写 local id；其他包的定义写 `<命名空间>:<local id>`，命名空间是 `core` 或那个包的 UUID，例如 `core:grain`。引用了其他包的定义，就必须在 manifest 里声明依赖那个包。
- **资产路径**：
  - 不带斜杠的文件名，指这份定义自己的同名文件夹，例如 `"top.png"`；
  - 以 `shared/` 开头的路径，指同类定义共用的文件夹，例如 `"shared/vehicle-underside.png"`；
  - 其他写法都无效。
  - 文件名只用小写英文字母、数字、下划线和连字符。
- **行为**：类型由游戏代码提供，内容只选类型、填参数。行为以类型为键写成对象：核心参数写在 `simulation.behaviors`，表现参数写在 `appearance.behaviors`。
- **标签**：取值由游戏代码提供，写了未知标签整个包无效。
- **阶段**：字段表的“阶段”一列写从哪个阶段开始需要，分为首个切片、Demo、Demo 之后。首个切片只实现其中一部分，但不能用和标准冲突的写法。

## Schema 和加载器的分工

Schema 只检查单个文件的结构和取值范围。下面这些由加载器检查，各格式说明里列出了它自己的规则：

- `id` 和文件名一致；
- 引用的定义存在，跨包引用的包已声明为依赖；
- 资产文件存在，图片的格式和尺寸符合要求；
- 涉及几个字段的规则，例如碰撞盒不超出所在的格。

## 示例包

- `example-pack` 依赖 `core`，所有定义都写在包里，不引用 `core` 的内容。
- `example-addon` 依赖 `example-pack`，用完整标识引用它的体素、蓝图和聚居地类型，演示包依赖和跨包引用。
- `content-library/` 是用户内容库里的一个玩家蓝图。

数值只用来演示写法，不是正式设计。

改了示例里的定义后，补齐缺少的占位图：

```powershell
python docs/content/tools/make_placeholder_pngs.py docs/content/examples/example-pack
```

已有的图默认跳过，加 `--force` 才重新生成。
