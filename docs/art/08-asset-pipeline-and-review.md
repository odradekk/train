# 08 资产制作流程与检查

> 决策出处：美术 Q220、Q230、Q239、Q240。交付物、源文件、目录与命名、制作顺序、检查清单、检查地点与检查光照、检查人与记录、样机的作用和各阶段范围，由[资产制作流程、交付物与检查](https://github.com/odradekk/train/issues/252)于 2026-10-09 确认；自定义模型的交付物、导入约定和源文件位置由[资产访问与导入：内容、体素和 Godot 的交接契约](https://github.com/odradekk/train/issues/286)于 2026-10-10 确认；制作顺序和检查条件从[美术方向初稿](../research/art-direction-draft-2026-10-08.md)第 11–12 节改写。

> **修订中**：当前里程碑是完整 Demo。Demo 的资产清单、三台样机、专项样例规格和占位资产的使用范围已由[Demo 内容与资产清单：覆盖场景与制作验收](https://github.com/odradekk/train/issues/283)于 2026-10-10 确定，见[Demo 内容与资产清单](../content/demo-catalog.md)。下文“本阶段”的首切片资产清单与实施票仅保留历史，待[按四模块重组现有工单并补齐 Demo 实施工单](https://github.com/odradekk/train/issues/294)更新，不能据此开工。大尺寸与拥挤场景专项已由[完整 Demo 的开局、目标、结束与验收](https://github.com/odradekk/train/issues/278)提前纳入 Demo；其余制作与画面检查规则仍有效。

## 边界

本文规定美术资产的交付物、源文件、目录与命名、制作顺序、检查清单，以及检查由谁做、怎样记录。表现字段、文件格式和校验见设计文档 [02 的表现资产校验](../design/02-definitions-and-content.md#表现资产校验)，内容目录见 [02 的内容目录与格式](../design/02-definitions-and-content.md#内容目录与格式)，渲染路径见 [07](../design/07-rendering.md)，本文引用、不重述。各类资产的画法见 [01](01-construction-language.md)–[07](07-icons-and-ui.md)。实施工单的流程和证据规则见[实现 SOP](../implement-sop.md) 和[派发 SOP](../dispatch-sop.md)。

## 不变量

1. **在游戏里检查。** 资产在真实的 Godot 游戏里检查通过才算完成。浏览器原型（包括样稿）只用来定方向和色值，自动检查只证明文件有效，都不能代替游戏里的检查（美术 Q240）。
2. **从具体对象出发。** 按样机或切片车辆的需要制作，已有的体素先复用，缺的才做，不先做闲置的体素定义（美术 Q239）。
3. **每份资产只有一个源。** 源是 PNG 本身或生成它的工具，二者选一；用工具生成的资产，改动都在工具里做，再重新导出。
4. **美术改动不改变玩法。** 只改图和表现字段，资产修订更新，语义修订不变（设计文档 02）。
5. **画面由开发者验收。** 画面是否通过，由开发者在运行中的游戏里决定。

## 规则

### 交付物

| 资产 | 交付物 |
| --- | --- |
| 立方体体素 | 各面 PNG，边长为 32 px × 平铺跨度；可选的正面图；`icon.png`，128×128（[07](07-icons-and-ui.md#物品图标)）；`.voxel.json` 中的表现字段：`faces`、`tile_span`、`material`，写法见[体素定义的格式](../content/voxel.md)；`.png.import`，导入方式改为 Keep File（`importer=keep`） |
| 形状 | 内置目录随代码提供，不按体素交付文件；美术只检查贴图投影到形状上的效果（[01](01-construction-language.md#形状)） |
| 自定义模型 | 源文件 `.blend`；导出的 `model.glb`，贴图嵌在文件里；每个表面的材质类别（[02](02-materials-and-textures.md#形状与自定义模型)）；`.glb.import`，导入方式改为 Keep File（`importer=keep`）。单位、原点、轴和文件内容的限制见[模型文件的约定](../content/voxel.md#模型文件的约定) |
| 场景模型、样机 | 内容文件：场景模型和样机都是蓝图 `.blueprint.json`，写法见[蓝图的格式](../content/blueprint.md)。设计编辑器和创作模式能用之前手写 |
| 检查记录 | 写在实施工单的完成评论里，见[检查人与记录](#检查人与记录) |

### 源文件

- 手绘的贴图和图标，源文件就是 PNG 本身。贴图每格 32 px，最大的平铺图 256×256，直接改 PNG，不另存分层文件。
- 用工具或样稿生成的图，生成它的工具就是源文件，留在仓库里，并在检查记录中写明。改色值时，样稿和对应的美术文档一起改。
- 自定义模型的 `.blend` 等源文件要保留。内置内容的源文件放在仓库根目录的 `art-source/`，按包和定义分目录，例如 `art-source/core/voxels/<local id>/model.blend`；目录里放 `.gdignore`，Godot 编辑器不会自动导入 `.blend`，导出包也不含它。源文件不放进内容包，Mod 作者自己保管源文件。
- 暂不使用 Git LFS。

### 目录与命名

- 每个体素一个定义文件和一个同名文件夹（设计文档 [02 的内容目录与格式](../design/02-definitions-and-content.md#内容目录与格式)），文件夹 `resources/voxels/<local id>/` 里的图按面命名：`top.png`、`side.png`、`bottom.png`、`front.png`、`icon.png`；自定义模型叫 `model.glb`。
- 几个面用同一张图时，直接绑定同一个文件，不复制。例如底面同顶面，就都绑定 `top.png`。
- 几个体素共用的图放在 `resources/voxels/shared/`，文件名写内容，例如 `vehicle-underside.png`。
- local id 用英文小写，单词之间用连字符，例如 `dry-grass`、`ballast`（设计文档 [02 的 ID 与命名空间](../design/02-definitions-and-content.md#id-与命名空间)）。目录只用来组织文件，不决定身份。

```text
builtin/core/resources/voxels/
├── grain.voxel.json
├── grain/
│   ├── top.png
│   ├── side.png
│   └── icon.png
└── shared/
    └── vehicle-underside.png
```

### 制作顺序

1. 选一个具体对象：样机或切片车辆，列出它要用的体素。
2. 已有的体素直接用；缺的才做，优先做能通用的（美术 Q239）。
3. 画贴图，写表现绑定。
4. 装到对象上，在游戏里按检查清单查看。
5. 通过后接入正式内容。

制作时在运行中的 Debug 构建里改图，用 `reload` 命令或 Ctrl+Shift+R 热重载后直接看效果（[控制台 reload](../console/reload.md)）。

### 检查清单

**单个体素**（做完就查）：

- 自动检查通过：内容校验（正方形、2 的幂、三面同尺寸、大小上限）和内容冒烟检查。自定义模型另查包围盒在格内、表面数和材质类别数一致、没有骨骼和动画等不支持的内容（[模型文件的约定](../content/voxel.md#模型文件的约定)）。
- `.png.import` 和 `.glb.import` 是 `importer=keep`，人工核对。
- 方向正确：侧面图的上方朝上；正面图在体素前方（局部 +Z）的那一面。
- 平铺：同种体素铺成至少 3 倍跨度宽的一片，看不出接缝；在任意位置切开，图案仍然均匀（[02 的贴图画法](02-materials-and-textures.md#贴图画法)）。
- 像素大小和相邻体素一致（32 px/格）。
- 材质类别填对。
- 图标放进外框缩到 32 px 后仍认得出（[07](07-icons-and-ui.md#物品图标)）。

**装到对象上之后**（在游戏里查）：

- 三种距离：近看（约 3 米）、跟随列车的默认距离、远处（对象只占屏幕的一小部分）。
- 运动：列车过弯、相机绕车转动时，贴图不闪烁、不游移。
- 光照：三种检查光照（[05](05-lighting-and-readability.md#检查光照)）。
- 可读性：先认出整体和用途，再看细部（[05](05-lighting-and-readability.md#可读性)）。
- 车轮这类必须按位置摆放的图案没有被切开（[02 的画法要求](02-materials-and-textures.md#画法要求)）。
- Demo 起增加：涂装只改可涂装的材质类别（[03](03-color-and-paint.md#玩家涂装)）；磨损叠加和损伤符合 [06](06-wear-damage-and-motion.md)。

性能不属于美术检查，按设计文档 [00 的本阶段性能目标](../design/00-product-and-core-loop.md#本阶段性能目标)。

### 检查地点与检查光照

- 不另做资产浏览器，检查都在真实游戏里做：首个切片在线路场景和节点场景里，用一列装了新素材的编组；Demo 起，单个体素和组装的检查也可以在设计编辑器里做。
- 三种检查光照用 Debug 构建的控制台命令切换：中性日光、阴天、夜间人工照明。只供检查，不是游戏里的时段。命令由[检查光照：Debug 构建切换三种检查光照](https://github.com/odradekk/train/issues/255)实现。

### 检查人与记录

- 自动检查由实现者运行，结果写进完成评论（实现 SOP）。
- 画面检查：实现者先按清单自查并截图，截图放在被忽略的 `.godot/validation/<工单号>/`，不进仓库；最后由开发者在运行中的游戏里查看，开发者确认后才算通过。
- 完成评论用一张表记录：检查项、条件（距离、光照、运动）、结果、问题。
- 不通过的问题在同一张工单里返工，超出工单范围的另开工单。
- 规格要改时，例如换色值，先改 docs/art 和样稿，再改资产。

### 样机

- 样机是检查共用体素定义的对象。新的体素定义至少装在一台样机上检查过，才算做完。
- 通用的体素定义要在不同用途的样机上都看得过去（美术 Q220、Q239）。
- 存档中的 Testbed 002 原型只是历史记录（[美术参考与原型证据](../../archive/prototypes/art-reference-v1/README.md)），不能当作检查环境。

## 本阶段

首个切片的资产由[补齐首个切片的货物与车辆体素素材](https://github.com/odradekk/train/issues/234)和[首个切片的场景美术素材](https://github.com/odradekk/train/issues/254)制作。首个切片的车头和货车（[01](01-construction-language.md#本阶段)）就是本阶段的样机。

- **交付**：按上文的目录和命名交付，粮食现在的 `voxels/grain/placeholder.png` 换成正式的图。
- **PNG 的来源**：两张工单各自给对应的样稿加“导出 PNG”，按上文的文件名导出：车辆和货物用[首个切片车辆贴图](../../archive/prototypes/first-slice-vehicle-textures.prototype.html)，场景用[首个切片场景资产](../../archive/prototypes/first-slice-scene-assets.prototype.html)。这两个样稿就是这批图的生成器，改色值时改样稿再重新导出，不直接改 PNG。`icon.png` 不从样稿导出，按 [07](07-icons-and-ui.md#物品图标) 手绘，源文件就是 PNG。
- **检查**：
  - 按检查清单进行，光照只有固定的白天。
  - 三种距离在线路场景里看，运动用列车过弯检查。
  - 站台在节点场景里看。
  - 图标在贸易界面里按 32 px 看。
- **记录与验收**：按上文的“检查人与记录”。

## Demo 范围

- 检查光照命令（[检查光照：Debug 构建切换三种检查光照](https://github.com/odradekk/train/issues/255)）。
- 在设计编辑器里检查单个体素和组装。
- 按现行尺寸重做三台样机：民用重载机车是大型机车，军用重装车辆是泰坦档位的武装重型车辆，工业维护建筑是重型车辆工厂；三者都是正式预置蓝图，见[Demo 内容与资产清单](../content/demo-catalog.md#预置蓝图)。
- 占位资产可用于不看画面的检查，以及早期阶段只确认流程与交互的人工功能试玩；美术检查、完整 Demo 人工验收、正式性能测量和完整窗口长测必须用正式资产，见[Demo 内容与资产清单](../content/demo-catalog.md#占位资产的使用范围)。
- 涂装、磨损叠加和损伤的检查。
- 自定义模型的交付与检查。
- 大尺寸设计与拥挤场景专项，覆盖泰坦车辆、长编组、拥挤站点、繁忙战斗和编辑器；样例规格见[Demo 内容与资产清单](../content/demo-catalog.md#大尺寸与拥挤场景专项样例)，性能验收遵循 [00](../design/00-product-and-core-loop.md#大尺寸与拥挤场景专项)，不以美术检查替代性能证据。

## Demo 之后

超出 Demo 已确认样例范围的更大规模检查；不承诺任意数量、任意设计复杂度下的帧率。Demo 内的专项不能再整体推迟到本阶段之后。

## 开放问题

- 已决定：[Demo 内容与资产清单：覆盖场景与制作验收](https://github.com/odradekk/train/issues/283)于 2026-10-10 确定三台样机对应的预置蓝图，以及大尺寸与拥挤场景专项的占用格数、部件数、嵌套情况、同屏数量和性能保证覆盖范围，见[Demo 内容与资产清单](../content/demo-catalog.md)。三台样机的外观细节在制作时按本文的检查流程确定。
