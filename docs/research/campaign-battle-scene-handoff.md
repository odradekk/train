# 大地图与局部战斗场景的衔接：《骑马与砍杀》和《全面战争》的做法

研究票：[研究：大地图与局部战斗场景的衔接](https://github.com/odradekk/train/issues/195)

所属地图：[体素引擎改作局部场景：设计文档与 open issue 全面修订](https://github.com/odradekk/train/issues/193)

研究日期：2026-10-03

这张研究票只收集有来源的事实和可借鉴的做法，不替[分层架构与局部场景的权威边界](https://github.com/odradekk/train/issues/198)做决定。

## 来源与标记

- **[官方]**：开发商或发行商自己发布的资料，包括手册、官方 wiki 和 Assembly Kit 文档、补丁说明、开发日志、开发者本人的帖子、官方 API 文档，以及随官方 Module System 发布的源文件。
- **[社区]**：玩家 wiki、论坛、Reddit、Steam 讨论、mod 文档，以及第三方反编译（标 [社区·反编译]）和第三方解包的游戏数据（标 [社区·解包]）。反编译代码最能说明游戏实际怎么做，但不是开发者发布的，也可能落后于最新版本。
- **[推断]**：我从上述材料推出的结论，单独标出并写明依据。
- 用词：三个系列叫法不同，本文统一写“自动结算”（Warband 的 auto-calc，Bannerlord 的 Send troops 或模拟，Total War 的 Auto-resolve）和“手动战斗”（进入战斗场景亲自打）。Warband 和 Bannerlord 的作战单位叫“队伍”，Total War 叫“军队”。
- 版本：Warband 取官方 Module System 1.171（经第三方镜像）和 2010 年版手册；最终版是 1.174，1.172 到 1.174 的官方公告不涉及战斗规则。Bannerlord 当前稳定版是 v1.4.8（2026-08-10），反编译代码是 v1.4.7，beta 是 v1.5.0。Total War 逐条标注作品和版本；WH3 的数值来自第三方解包的 9.0 数据。
- 读取限制：totalwar.com 新闻页对脚本返回 429，本文链接的是 Web Archive 快照。TaleWorlds 论坛被 Cloudflare 拦截，Bannerlord 的官方补丁说明取自 Steam 公告，论坛帖取自 Web Archive。Reddit 页面被验证拦截，改读 Arctic Shift 存档，赞数和评论数是存档值。

## 结论

1. **选择权在玩家，AI 之间的战斗全部自动结算。** 三个系列都在遭遇时让玩家选“手动战斗 / 自动结算 / 撤退”。强制规则很少：任务战和部分重要战斗不能自动结算，海战只能自动结算（WH3），Bannerlord 主角受伤不能手动战斗、士气过低不能自动结算，Warband 自动结算要求队伍里至少 4 名健康成员。
2. **自动结算是不依赖战斗场景的数值模拟，只读摘要信息。** Warband 把队伍强度换算成每轮攻击次数，再交给引擎抽样出伤亡。Bannerlord 逐击随机模拟，输入是兵种等级、兵种类别在该地形和攻守身份下的修正、统帅的 Tactics 技能和 perk、士气，不读具体装备。WH3 的解包数据显示它按 4 秒一步推进“每秒击杀”。
3. **没有找到把自动结算校准到手动结果的机制，实际靠四种做法缩小差距。** 两条路径共用伤亡判定和奖励规则（Bannerlord、Warband）；战前给预测（Total War）；让玩家影响自动结算（Total War 的战斗姿态，Bannerlord 的选兵和 Tactics）；补丁逐版本调参。Total War 官方公开的设计目标之一是“玩家总能打得比自动结算好”。
4. **带入战斗的是单位名册和少量战役状态，写回的是伤亡和奖励，粒度因游戏而异。** Bannerlord 逐兵实时写回，手动战斗和自动结算调用同一组方法。Warband 按兵种汇总伤亡。Total War 向战役脚本返回一个结果对象，含击杀与溃逃比例、结果等级、是否自动结算、是否撤退，两条路径共用。
5. **战斗地图都是预制内容，用大地图上的少量信息选用，不是战斗时从零生成。** Bannerlord 把世界地图分块，每块指定预制场景，出生点由遭遇位置和双方方向推算。Warband 按地形类型选 9 种随机场景模板，再由地形生成器生成，聚居地用固定场景。Total War 的 Attila 工具文档说明战斗地图由战役 tile map 派生，WH3 的数据是按区域、战斗类型、阵营映射到预制战斗。
6. **战斗期间大地图时间停止，战斗中不能存档，参战方在开战前确定。** Warband 手册明确写 “another party cannot join from the world map”。增援来自开战前已确定的名单：Warband 和 Bannerlord 是同一批人分波出场，Total War 是战前面板列出的相邻军队，从战场边缘按战役位置的方向、带延迟入场。
7. **撤退是战斗的一种结束方式，已发生的伤亡保留，不会自动转成自动结算。** Bannerlord 退出手动战斗直接结束；Warband 撤退或主角倒下时按一次“撤退伤亡”结算后结束。Bannerlord 只在“规模和波数上限用完仍未分出胜负，且兵力悬殊”时自动转为自动结算。
8. **AI 的战略判断也读取自动结算结果。** WH3 8.1 的官方说明写明 AI 对峙的概率与自动结算结果成正比。Bannerlord 玩家称 AI 只在模拟结果近乎必胜时才开战（社区说法）。
9. **常见抱怨有三类：高价值单位被弱敌击杀；偏差方向随版本、难度、技能而变；读档重打。** 第一类在三个系列都有记录，第二类见于 Total War 和 Bannerlord。官方对读档的处理：WH3 2.2.0 修复“读快速存档后自动结算结果会变”，Bannerlord 的随机数状态随存档保存。

## 一览

| 问题 | Warband 1.171 | Bannerlord v1.4.x | Total War（近年作品） |
| --- | --- | --- | --- |
| 谁选手动或自动 | 玩家，遭遇菜单 | 玩家，遭遇菜单 | 玩家，战前面板 |
| AI 之间的战斗 | 引擎按轮调用脚本结算 | 地图事件每 30 分钟（攻城突击 60 分钟）模拟一轮 | 自动结算（玩家说法，WH3 8.1 官方说明间接印证） |
| 自动结算输入 | 兵种等级、人数、受伤状态；不看装备和地形 | 兵种等级、地形与攻守下的兵种类别修正、Tactics、士气；不读具体装备 | 官方只写“比较双方各项数据”；WH3 解包数据含单位数据、将领、法术、攻城器械、战斗类型 |
| 缩小与手动结果的差距 | 无；手册提醒部队没有你在场会打得差 | 共用伤亡规则；补丁逐项调参；战斗未决时转自动结算 | 战前预测、战斗姿态、难度、逐版本调参 |
| 带入战斗的状态 | 兵种堆叠、伤员数、英雄；战斗优势 | 名册里的个体（负伤、溃逃、阵亡的不出场）、英雄血量、队伍士气、难度系数、城墙与攻城器械 | 单位（兵种、人数比例、经验）、将领、战役效果、攻城器械 |
| 写回方式 | 按兵种汇总伤亡；英雄带 HP | 逐兵实时写入名册，个体经验记在名册里 | 结果对象：结果等级、击杀与溃逃比例，加逐单位剩余状态 |
| 规模上限与增援 | Battle Size 选项；分阶段；增援波 | 200 到 1000（攻城 150 到 1000）；波数可选 3、4、5 或不限 | 单场单位数上限（Rome II 为 20 个，其余成为增援）；增援军延迟入场 |
| 战斗地图 | 按地形类型选 9 种随机场景模板；聚居地固定场景 | 世界地图分块，每块指定预制场景；聚居地专用场景 | Attila 工具文档：由战役 tile map 派生，聚居地按最近聚居地；WH3 数据：按区域、战斗类型、阵营映射到预制战斗 |
| 部署方向 | 任务模板与场景入口 | 遭遇位置和双方统帅位置之差 | 取自接近方向；增援按战役位置的方向入场 |
| 战中撤退 | Tab 键，离敌够远才行；按一次撤退伤亡结算 | 离敌 5 米以上，或主角已倒；直接结束，不转模拟 | 战中走出边界；未撤出单位的规则官方未写明 |
| 主角倒下 | 任务立即结束，按一次撤退伤亡结算 | AI 继续打，可接管另一名士兵（v1.3.4 起） | 无对应概念 |
| 战中存档 | 不能快速存档 | Esc 菜单没有存档项 | 官方未写明；玩家称战中不能读档 |
| 战斗期间大地图时间 | 停止（反编译推断） | 停止（代码） | 回合制，战斗在回合内进行 |
| 开战后其他部队加入 | 不能（手册） | 不能（代码推断） | 增援军战前确定，战斗中陆续入场 |

## 1 什么时候自动结算，由谁决定，用什么输入，怎样控制差距

### 1.1 谁决定，什么时候强制

**Warband**

- **[官方]** 遭遇菜单（`simple_encounter`）按条件列出 `Charge the enemy.`（手动战斗，主角未受伤才有）、`Order your troops to attack without you.`（自动结算，队伍至少有 4 名健康成员才有）、`Leave.`、`Pull back, leaving some soldiers behind to cover your retreat.` 和投降，由玩家点选。[module_game_menus.py](https://github.com/int19h/mb_warband_module_system/blob/master/Module_system%201.171/module_game_menus.py)。手册第 12 章列出同样的选择，并提醒：“Generally, your troops will not fight so well without you on the line beside them, but this can save time and hassle if the fight looks to be a slaughter.”[Warband 手册](https://cdn.akamai.steamstatic.com/steam/apps/48700/manuals/PDX5505US_Warband_Manual_US.pdf)
- **[官方]** AI 队伍之间的战斗由引擎按轮调用脚本 `game_event_simulate_battle`，脚本注释：“This script is called whenever the game simulates the battle between two parties on the map.”[module_scripts.py](https://github.com/int19h/mb_warband_module_system/blob/master/Module_system%201.171/module_scripts.py)

**Bannerlord**

- **[社区·反编译]** 遭遇菜单里 Attack 进入手动战斗，`Send troops.` 走自动结算。本队没有健康士兵而友军有兵时，后者改称 `Leave it to the others.`。主角受伤时 Attack 不可用（`You are wounded.`），士气过低时 Send troops 不可用（`Your men lack the courage to continue the battle without you. (Low Morale)`）。v1.4.7。[MenuHelper.cs](https://github.com/BannerlordCode/bannerlord-1.4.7/blob/main/TaleWorlds.CampaignSystem/Helpers/MenuHelper.cs)
- **[社区·反编译]** AI 之间的战斗不打开战斗场景。大地图时间推进时，地图事件每隔一段游戏时间模拟一轮，间隔由 `GetSimulationTickInterval` 给出：攻城突击 60 分钟，其他 30 分钟。[DefaultCombatSimulationModel.cs](https://github.com/BannerlordCode/bannerlord-1.4.7/blob/main/TaleWorlds.CampaignSystem/GameComponents/DefaultCombatSimulationModel.cs)
- **[官方]** v1.0 发布说明：手动战斗因战场规模和增援波数上限没分出胜负时，游戏回到遭遇菜单；“If there is a huge strength difference between the sides, even if there are remaining troops on both sides, the battle auto-resolves.”[Bannerlord 1.0 发布说明](https://store.steampowered.com/news/app/261550/view/4714731755429514639)
- **[官方]** 开发者 Duh_TaleWorlds 解释波数上限的用意（2022-11-22）：“primarily aimed at players with lower battle sizes that don't want to auto-resolve the entire encounter, but do want a built-in limit for the mission to control the time spent.”[论坛帖，Web Archive 快照](http://web.archive.org/web/20231015104840/https://forums.taleworlds.com/index.php?threads/new-wave-reinforcement-system.455777/)

**Total War**

- **[官方]** Rome II 手册：战斗面板提供三项，在战场上打、“通过比较双方各项数据”自动结算、撤退。[Rome II 手册](https://r2enc.totalwar.com/en/manual/single-player/0016_enc_page_campaign_play_military_attack) Rome: Total War（2004）的官方 wiki 说法类似，面板还显示把增援计入的胜率条，并提醒“Autoresolving a battle is speedy, but casualties may be heavy and you can't protect valuable generals.”[Mastering Battles](https://wiki.totalwar.com/w/Mastering_Battles.html)
- **[社区·解包]** WH3 9.0 的界面文字：任务战和“部分重要战斗”不能自动结算；海战只能自动结算；攻城战在有攻城手段前，攻击和自动结算按钮都不可用。自动结算按钮提示：“Automatically resolve the battle if you have a clear advantage over the enemy. However, you will have a better chance at victory should you fight the battle manually.”[ui_text_replacements](https://github.com/Shazbot/WH3-Dump/blob/main/text/db/ui_text_replacements__.loc.tsv)
- **[社区]** AI 之间的战斗：玩家称“the AI only autoresolves against other AI unless a player is involved”（[r/totalwar](https://www.reddit.com/r/totalwar/comments/1jteybb/dumb_question_about_ai_vs_ai)），另一帖称“all AIvsAI interactions are done via autoresolve”（[CA 论坛，2024-08-30](https://community.creative-assembly.com/total-war/total-war-warhammer/forums/8-general-discussion/threads/6939-let-s-talk-about-autoresolve-and-why-it-needs-a-revision-bad)）。
- **[官方]** WH3 8.1 说明在 AI 已知问题里写道，两军对峙的概率“proportional to the autoresolve result - the more evenly matched the armies are in auto resolve, the higher the chance this will happen”，即 AI 的战略判断读取自动结算结果。[WH3 Patch 8.1](https://store.steampowered.com/news/app/1142710/view/1836506165582899)

### 1.2 自动结算的输入和算法

**Warband**

- **[官方]** 玩家方的自动结算按“轮”进行（菜单 `order_attack_2`）。单兵强度 = max(1, (等级+4)²×2/100)，乘以未受伤人数；受伤的英雄记 0，主角本人不计。取“我方含随从”和“敌方”强度中较小的一个查阈值表得到除数（强度 ≤25 为 1，≤50 为 2，≤75 为 3，≤125 为 4，≤200 为 5，≤400 为 6，此后每翻倍加 1，超过 102400 为 15），双方强度各除以该数，作为本轮攻击次数交给引擎操作 `inflict_casualties_to_party_group` 抽样出伤亡。每轮后玩家选“继续进攻”或“把士兵叫回来”。代码注释显示公式在 1.125、1.126 改过。[module_game_menus.py](https://github.com/int19h/mb_warband_module_system/blob/master/Module_system%201.171/module_game_menus.py)、[module_scripts.py 的 party_calculate_strength](https://github.com/int19h/mb_warband_module_system/blob/master/Module_system%201.171/module_scripts.py)
- **[官方]** 输入只有等级、人数和受伤状态，不含装备、地形、阵型。遭遇菜单只显示人数（“You have {reg10} troops fit for battle against their {reg11}.”），没有结果预览。
- **[官方]** 与自动结算无关但值得注意：手动战斗里 `script_calculate_battle_advantage` 用双方健康人数比（超出 100% 的部分乘 2）加 30×（我方最高 Tactics − 敌方 Tactics），再除以 100，传给引擎的 `set_battle_advantage`。手册说明战场规模上限按这个“战斗优势”在双方之间分配。
- **[社区·反编译]** 引擎内部的单次判定不在模块源码里，只能看第三方资料（Android 版反编译，未在 PC 版验证）：从队伍里随机抽一个健康成员，普通兵 65% 阵亡、35% 变伤员；PC 版的逆向笔记同样记 35%，且不受 Surgery 影响。[Android 反编译](https://github.com/1922828151/MnB_Mobile/tree/master/libMBExpMobile/decompile)、[WarbandTogether 笔记](https://github.com/Night1099/WarbandTogether/blob/main/docs/flows/battle-pipeline.md)
- **[官方]** 技能说明写明 Surgery 每点有 4% 概率让重伤的队伍成员变为伤员而不是阵亡。[module_skills.py](https://github.com/int19h/mb_warband_module_system/blob/master/Module_system%201.171/module_skills.py) **[推断]** 自动结算与手动战斗在“阵亡还是负伤”上的比例可能不同：前者按第三方资料固定为 35%，后者受 Surgery 影响。

**Bannerlord**

- **[社区·反编译]** 逐击模拟：每次出手从己方随机取 1 名士兵，打对方随机 1 名。伤害 = (0.5 + 0.5×随机数) × 40 × (攻击者战力 / 被击者战力)^0.7 × 优势系数，再乘 perk 系数和士气系数。[DefaultCombatSimulationModel.cs](https://github.com/BannerlordCode/bannerlord-1.4.7/blob/main/TaleWorlds.CampaignSystem/GameComponents/DefaultCombatSimulationModel.cs)
- **[社区·反编译]** 兵种战力 = (2+Tier)×(10+Tier)×0.02，英雄的 Tier 按等级/4+1 计并乘 1.5，统帅的 captain 类 perk 另有加成。再乘一张修正表，键是兵种类别（步兵、远程、骑兵、骑射）、战场环境（平原、森林、渡河、村庄、攻城等）和攻守身份。环境由大地图位置的地形和天气决定（`GetContextForPosition`）。读到的兵种数据只有等级、类别和是否骑乘，不读具体装备。[DefaultMilitaryPowerModel.cs](https://github.com/BannerlordCode/bannerlord-1.4.7/blob/main/TaleWorlds.CampaignSystem/GameComponents/DefaultMilitaryPowerModel.cs)
- **[社区·反编译]** 优势系数以 1 为起点，加上领队英雄的 Tactics 技能效果（`TacticsAdvantage`）；有 `PreBattleManeuvers` perk 时，Tactics 每高于对方 1 点再加 1%；攻城战的进攻方减 10%；另有一批 Tactics 类 perk 按条件逐项加成。士气另算：出手一方的士气低于 50 的部分，以及被击一方的士气高于 50 的部分，各按 0.005 的系数降低这次伤害。[DefaultCombatSimulationModel.cs](https://github.com/BannerlordCode/bannerlord-1.4.7/blob/main/TaleWorlds.CampaignSystem/GameComponents/DefaultCombatSimulationModel.cs)
- **[官方]** v1.4.5 去掉了自动结算里默认的骑兵加成，“Terrain-specific effects still apply”。[v1.4.5 说明](https://store.steampowered.com/news/app/261550/view/1833334318569123)
- **[官方]** v1.3.0 起模拟有士气和撤退：“When a side loses too many rounds in a simulation, it may attempt to retreat. During the retreat phase, the opponent continues to strike for four additional rounds.”[v1.3.0 beta 说明](https://store.steampowered.com/news/app/261550/view/1810503566552360)
- **[官方]** 官方 API 文档（1.4.8）列出同一个类的方法：`SimulateHit`（参数含 `strikerAdvantage` 和双方士气）、`GetBattleAdvantage`、`GetBluntDamageChance`、`GetSimulationTickInterval`、`GetSimulationTicksForBattleRound`、`GetPursuitRoundCount`、`GetSettlementAdvantage`，与上面 1.4.7 的反编译代码一致。[API 文档](https://apidoc.bannerlord.com/v/1.4.8/class_tale_worlds_1_1_campaign_system_1_1_game_components_1_1_default_combat_simulation_model.html)

**Total War**

- **[官方]** CA 开发者在 Reddit 说明 WH2 自动结算的取向（账号 Grace_CA，带 Creative Assembly 标识，2018-02-15）：“The auto resolve doesn't primarily make decisions based on how much units cost, but it's very much about how well those units will perform in whatever matchup the auto resolver decides to engage them in. The auto resolver categorises units in groups (melee, missile) and picks an engagement for them, which doesn't necessarily take into account the more sophisticated strategic tactics and clever matchups which players would use.”[帖子](https://www.reddit.com/r/totalwar/comments/7xqj7d/autoresolve_functionality_in_wh2/)
- **[官方]** 补丁说明里能看出的输入：弹药（WH2 Empire Undivided）、单体单位受伤后的行为（WH3 5.1.3）、远程强度修正（WH3 6.3.4）、守城加成（Rome II Patch 4）、将领、聚居地防御。[WH3 Hotfix 5.1.3](https://store.steampowered.com/news/app/1142710/view/5963413728352875917)、[WH2 Empire Undivided](https://web.archive.org/web/2024/https://www.totalwar.com/news/total-war-warhammer-ii-patch-notes-the-empire-undivided-update)
- **[社区·解包]** WH3 9.0 的数据表显示自动结算是定步长模拟：时间步长 4，另有每秒击杀、弹药、追击、守方塔数（要塞 3，无墙 1）、多余近战伤害系数等变量，修正项按战斗类型（野战、伏击、桥、聚居地、港口、夜战）和兵种克制分组。变量含义由名称推断。[campaign_variables](https://github.com/Shazbot/WH3-Dump/blob/main/db/campaign_variables_tables/data__.tsv)、[autoresolver_modifier_group_to_modifiers](https://github.com/Shazbot/WH3-Dump/blob/main/db/autoresolver_modifier_group_to_modifiers_tables/data__.tsv)
- **[社区]** Pharaoh 的战前面板说明（Fandom 转述游戏内文字）：自动结算按双方相对实力、将领与饰品的加成、是否有防御工事（城墙、营地）计算结果和伤亡，并在点击前给出预测。[Pre-Battle Options (Pharaoh)](https://totalwar.fandom.com/wiki/Pre-Battle_Options_(Pharaoh))
- **[官方]** 战斗姿态：Rome II 允许玩家在自动结算前选择姿态，告诉将领“how to fight the upcoming battle”。进攻姿态：己方整体攻击加成、骑兵另有攻击加成，但敌方获得防御加成，双方远程效果降低，双方都不能撤退。防守姿态：己方整体防御加成，敌方获得攻击加成，双方远程效果提高，损失过大时会尝试撤退。均衡姿态：不加不减，允许撤退。[Rome II 手册](https://r2enc.totalwar.com/en/manual/single-player/0016_enc_page_campaign_play_military_attack)

### 1.3 怎样控制它和手动战斗结果的差距

**没有找到事后校准到手动结果的机制**（读过的 Warband 模块源码和 Bannerlord 反编译代码里都没有，Total War 没有官方公式）。以下是各系列公开可见的做法。

- **共用规则（Bannerlord）**：**[官方]** e1.6.3 “the chance is the same in both missions and simulations”（英雄负伤后死亡的概率）；e1.1.0 “Simulated battles no longer give more XP than fought battles.”[e1.6.3](https://store.steampowered.com/news/app/261550/view/4069556867204072408)、[e1.1.0](https://store.steampowered.com/news/app/261550/view/4654976078204642243)。**[社区·反编译]** 手动战斗和模拟都经 `MapEventSide.OnTroopKilled / OnTroopWounded / OnTroopRouted` 写回；两边的死亡概率都取 `1 − PartyHealingModel.GetSurvivalChance(...)`；经验系数不同，模拟 0.9，手动战斗 1.0；模拟里钝器伤害按固定概率处理（有玩家参与 0.3，纯 AI 战斗 0.1）。[MapEventSide.cs](https://github.com/BannerlordCode/bannerlord-1.4.7/blob/main/TaleWorlds.CampaignSystem/MapEvents/MapEventSide.cs)、[SandboxAgentDecideKilledOrUnconsciousModel.cs](https://github.com/BannerlordCode/bannerlord-1.4.7/blob/main/SandBox/GameComponents/SandboxAgentDecideKilledOrUnconsciousModel.cs)、[DefaultCombatXpModel.cs](https://github.com/BannerlordCode/bannerlord-1.4.7/blob/main/TaleWorlds.CampaignSystem/GameComponents/DefaultCombatXpModel.cs)
- **共用规则（Warband）**：**[官方]** 手动战斗和自动结算都进入同一个胜利菜单 `mnu_total_victory`，用敌方伤亡计算俘虏、战利品和经验。声望和士气只在手动战斗后更新（`battle_debrief`）。[module_game_menus.py](https://github.com/int19h/mb_warband_module_system/blob/master/Module_system%201.171/module_game_menus.py)
- **战前预测（Total War）**：**[官方]** WH2 1.12 起自动结算按钮悬停时先显示结果等级和损失估计，将被歼灭的单位卡闪红，开发者说明用意是让玩家“better decide when to auto-resolve, and when you think you can out-fight an algorithm”。[WH2 Hammers & Herdstones 说明](https://web.archive.org/web/2024/https://www.totalwar.com/news/total-war-warhammer-ii-the-hammers-herdstones-update) **[社区·解包]** WH3 的预测误差分 5 档（0.15、0.10、0.05、0.01、0）。**[社区]** 3K 的预测即最终结果。Bannerlord 的 Send troops 悬停提示显示的是情境文字（v1.3.0 说明里称 “simulation context tooltip”），反编译代码里只有清剿藏身处的选项显示成功率：`Send troops to clear ({SUCCESS_CHANCE}%)`（[HideoutCampaignBehavior.cs](https://github.com/BannerlordCode/bannerlord-1.4.7/blob/main/TaleWorlds.CampaignSystem/CampaignBehaviors/HideoutCampaignBehavior.cs)）。
- **设计目标（Total War）**：**[官方]** Grace_CA 列出 WH2 自动结算的四个目标：“Ensure unit losses, where it makes sense”“Don't make it tedious”“Let the player outperform the AR in all cases and on all difficulty levels”“Protect unique high quality units”，并承认这些目标彼此冲突。同帖说明 WH1 时“too easy to auto resolve your way through the campaign barely losing units”。[帖子](https://www.reddit.com/r/totalwar/comments/7xqj7d/autoresolve_functionality_in_wh2/)
- **难度（Total War）**：**[官方]** WH3 1.2.0：结果改由战斗难度而不是战役难度决定，并降低高难度下自动结算的惩罚。[WH3 Update 1.2.0](https://web.archive.org/web/2024/https://www.totalwar.com/news/total-war-warhammer-iii-update-120) **[社区]** 玩家称低难度下自动结算有“约 300%”之类的加成，没有官方数值；解包的 `autoresolver_modifier_group_to_modifiers` 表里，按难度分组的行在 9.0 中取值为 0，难度数值的实际位置没有找到。[r/totalwar 讨论](https://www.reddit.com/r/totalwar/comments/1jrsx5g/why_am_i_so_much_worse_than_the_autoresolve/)
- **玩家可控项**：Total War 的战斗姿态（见 1.2）；Bannerlord 的 Tactics 技能和 perk 进入优势系数（见 1.2），v1.1.0 起要塞战可以选兵：“Added troop selection to keep battles. (For modding: You can now initialise battle simulation with selected troops.)”（[v1.1.0 说明](https://store.steampowered.com/news/app/261550/view/5070528176557709918)）；Warband 的 Tactics 在模块源码里只用于手动战斗的战斗优势，自动结算的模块公式里没有它（引擎内部的抽样不可见）。
- **逐版本调参**：**[官方]** Rome II Patch 9 “A small global penalty for the player in autoresolved battles”，Patch 3 “Smaller and depleted AI controlled forces are now less likely to survive auto-resolved battles”（[Patch 9](https://wiki.totalwar.com/w/Total_War_ROME_II:_Patch_9)、[Patch 3](https://wiki.totalwar.com/w/Total_War_ROME_II:_Patch_3)）；WH1 Update 5 “Autoresolver protects high quality units more”（[Update 5](https://wiki.totalwar.com/w/Total_War_WARHAMMER_Update_5)）；Bannerlord e1.2.0 “Increased effect of number advantage in simulation battles”（[说明](https://store.steampowered.com/news/app/261550/view/3112493747271299871)），e1.3.0 “Cavalry advantage in simulations is reduced to 20% from 30%”（[说明](https://store.steampowered.com/news/app/261550/view/3110243214556585064)），e1.5.5 “Implemented a new system that calculates troop power based on context.”（[说明](https://store.steampowered.com/news/app/261550/view/3917534300074200484)）。
- **自动结算也能强制套用**：**[社区·解包]** Total War 的战役脚本文档里，自定义战场记录可设置“forces the application of the autoresolver to the battle result after the battle, regardless of what happened in the battle itself”，并随存档保存。**[推断]** 战役层可以在战斗结束后把自动结算的结果强加给战斗，所以自动结算是战斗结果的后备来源，不只是玩家的另一个选项。[campaign_manager 文档](https://github.com/Shazbot/WH3-Dump/blob/main/documentation/script/campaign/campaign_manager.html)

## 2 单位状态怎样带入战斗，结果怎样写回

### 2.1 带入

**Warband**

- **[官方]** 选 `Charge the enemy.` 后依次调用 `script_calculate_renown_value`、`script_calculate_battle_advantage`，`set_battle_advantage`、`set_party_battle_mode`，设置任务模板 `mt_lead_charge`，按地形跳转场景，任务结束后回到遭遇菜单（`$g_next_menu`）。[module_game_menus.py](https://github.com/int19h/mb_warband_module_system/blob/master/Module_system%201.171/module_game_menus.py)
- **[官方]** 手册：Battle Size 是同一时刻战场上的总兵力，按战斗优势在双方之间分配；出场的单位是随机的，主要看在队伍列表里的位置，靠前的更可能出场；本阶段负伤的士兵在整场战斗里保持负伤；英雄血量跨阶段延续；阶段之间可以脱离战斗，但放弃已得的战利品。[Warband 手册](https://cdn.akamai.steamstatic.com/steam/apps/48700/manuals/PDX5505US_Warband_Manual_US.pdf)

**Bannerlord**

- **[社区·反编译]** 出场者取自地图事件里的个体名册（`FlattenedTroopRoster`，每个士兵一个 `UniqueTroopDescriptor`），负伤、溃逃、阵亡的不出场。出场名额按双方兵力比例分配（`BattleSizeAllocating`），增援用 `Wave` 方式，波数取自选项。[DefaultTroopSupplierProbabilityModel.cs](https://github.com/BannerlordCode/bannerlord-1.4.7/blob/main/TaleWorlds.CampaignSystem/GameComponents/DefaultTroopSupplierProbabilityModel.cs)、[SandBoxMissionSpawnHandler.cs](https://github.com/BannerlordCode/bannerlord-1.4.7/blob/main/SandBox/Missions/MissionLogics/SandBoxMissionSpawnHandler.cs)
- **[社区·反编译]** 战场规模选项：野战 200、300、400、500、600、800、1000，攻城 150、230、320、425、540、625、1000。[BannerlordConfig.cs](https://github.com/BannerlordCode/bannerlord-1.4.7/blob/main/TaleWorlds.MountAndBlade/BannerlordConfig.cs)
- **[官方]** 战场初始化参数 `MissionInitializerRecord` 的字段：`SceneName`、`TerrainType`、`NeedsRandomTerrain`、`RandomTerrainSeed`、`PlayingInCampaignMode`、`SceneHasMapPatch`、`PatchCoordinates`、`PatchEncounterDir`、`AtmosphereOnCampaign`、`DamageToFriendsMultiplier` 等。[API 文档](https://apidoc.bannerlord.com/v/1.4.8/struct_tale_worlds_1_1_core_1_1_mission_initializer_record.html)
- **[官方]** v1.0：出场波数可设为 3、4、5 或不限，默认不限；攻城时城墙和攻城器械的生命值在大地图与战斗之间双向同步：“Hit points of siege equipment between the campaign map and mission now get synchronized.”[Bannerlord 1.0 发布说明](https://store.steampowered.com/news/app/261550/view/4714731755429514639)
- **[社区·反编译]** 队伍士气折算成士兵的初始士气：`CampaignAgentComponent.GetMoraleAddition` 取（队伍士气−50）/2，再加上己方实力占比×10−5。攻城时，城墙各段的生命值比例和已造好的攻城器械随任务一起传入（`OpenSiegeMissionWithDeployment`）。[CampaignAgentComponent.cs](https://github.com/BannerlordCode/bannerlord-1.4.7/blob/main/SandBox/CampaignAgentComponent.cs)、[PlayerSiege.cs](https://github.com/BannerlordCode/bannerlord-1.4.7/blob/main/TaleWorlds.CampaignSystem/Siege/PlayerSiege.cs)

**Total War**

- **[官方]** Attila Assembly Kit：战斗里的单位由 `<unit>` 标签定义，含兵种、`<unit_experience>`（经验，在单位卡上显示为军阶，使单位战斗力更强）、`<general>`；增援军（`<reinforcement_army>`）作为军队的子标签时，其单位“drip-fed into the host army”，作为联盟的子标签时则是由 AI 控制的独立军队。[Unit Tags](https://wiki.totalwar.com/w/Total_War:_ATTILA_KIT_-_Unit_Tags)、[Army Tags](https://wiki.totalwar.com/w/Total_War:_ATTILA_KIT_-_Army_Tags)
- **[社区·解包]** WH3 的战役脚本文档里，军队单位对象有 `unit_key`、`percentage_proportion_of_full_strength`、`uses_hitpoints_in_campaign`、`experience_level`。**[推断]** 战役层保存的是兵种、人数比例和经验，而不是逐个士兵。[model_hierarchy](https://github.com/Shazbot/WH3-Dump/blob/main/documentation/script/campaign/model_hierarchy.html)
- **[社区·解包]** 战役效果直接进入战斗，例如 “Can't Retreat from battle”“Enters battle Winded”“Reinforcement time the army is increased by 50%”。[effects_additional_tooltip_details](https://github.com/Shazbot/WH3-Dump/blob/main/text/db/effects_additional_tooltip_details__.loc.tsv)
- **[官方]** Rome II 手册：一场战斗最多 20 个单位自动编为一军，多出的单位可以改作增援（取消面板上的勾选）。[Rome II 手册](https://r2enc.totalwar.com/en/manual/single-player/0016_enc_page_campaign_play_military_attack)

### 2.2 写回

**Warband**

- **[官方]** 任务结束时脚本 `script_count_mission_casualties_from_agents` 逐 agent 统计伤亡，按兵种堆叠累加到 `p_player_casualties`、`p_enemy_casualties`、`p_ally_casualties`，每个堆叠记录人数和伤员数。任务模板带 `mtf_battle_mode`，进入前菜单调用 `set_party_battle_mode`。[module_scripts.py](https://github.com/int19h/mb_warband_module_system/blob/master/Module_system%201.171/module_scripts.py)、[module_mission_templates.py](https://github.com/int19h/mb_warband_module_system/blob/master/Module_system%201.171/module_mission_templates.py)
- **[社区]** `set_party_battle_mode` 的作用：“causes battle casualties to be reflected on troops, killing/wounding them in participating parties (including effects on health of player and other hero troops)”，即战斗里的死伤由引擎直接作用到参战队伍，英雄带个体 HP。[mbcommands wiki](https://mbcommands.fandom.com/wiki/Template:Op_game_control_screens_menus_dialogs_and_encounters)
- **[官方]** 战后处理，手动和自动结算共用：伤员转为俘虏（`party_add_wounded_members_as_prisoners`），战利品按敌方伤亡逐个兵种抽取；经验 `script_party_give_xp_and_gold` 对敌方每个兵种堆叠按（等级+10）²/10×人数累加，乘玩家贡献比例，上限 40000，再乘 50% 到 99% 的随机数。分成份额：普通兵 1，英雄同伴 3，玩家 10（手册）。[module_scripts.py](https://github.com/int19h/mb_warband_module_system/blob/master/Module_system%201.171/module_scripts.py)、[Warband 手册](https://cdn.akamai.steamstatic.com/steam/apps/48700/manuals/PDX5505US_Warband_Manual_US.pdf)
- **[推断]** 写回的是按兵种汇总的数，不是逐个士兵的明细。依据：伤亡以兵种堆叠（troop id、人数、伤员数）累加；只有英雄带个体状态。

**Bannerlord**

- **[社区·反编译]** 写回逐兵、实时进行。手动战斗里士兵阵亡、昏迷、溃逃时，经 `PartyGroupAgentOrigin` 调用 `MapEventSide.OnTroopKilled / OnTroopWounded / OnTroopRouted`；自动结算直接调用同一组方法（`ApplySimulationDamageToSelectedTroop`）。阵亡的立即从队伍名册删除，负伤的立即标记。[MapEventSide.cs](https://github.com/BannerlordCode/bannerlord-1.4.7/blob/main/TaleWorlds.CampaignSystem/MapEvents/MapEventSide.cs)、[PartyGroupAgentOrigin.cs](https://github.com/BannerlordCode/bannerlord-1.4.7/blob/main/TaleWorlds.CampaignSystem/AgentOrigins/PartyGroupAgentOrigin.cs)
- **[社区·反编译]** `MapEventParty` 用扁平名册记录每个个体的负伤状态和经验（`Add(character, isWounded, xp)`），另有阵亡名册 `DiedInBattle`、负伤名册 `WoundedInBattle` 和战利品接收名册 `RosterToReceiveLootMembers`；地图事件由 `MapEvent.FinalizeEvent` 收尾。[MapEventParty.cs](https://github.com/BannerlordCode/bannerlord-1.4.7/blob/main/TaleWorlds.CampaignSystem/MapEvents/MapEventParty.cs)
- **[官方]** 1.0 的说明：攻城战中途撤退会改变大地图上的攻城器械状态，“Retreating during an offensive siege now removes the battering ram and siege towers.”；器械受损后撤退，生命值也会同步回大地图。[Bannerlord 1.0 发布说明](https://store.steampowered.com/news/app/261550/view/4714731755429514639)

**Total War**

- **[官方]** Attila Assembly Kit 的待结算战斗（pending battle）脚本接口：`attacker_is_stronger`、`percentage_of_attacker_killed`、`percentage_of_defender_killed`、`percentage_of_attacker_routed`、`percentage_of_defender_routed`、`attacker_commander_fought_in_battle`、`attacker_battle_result`（取值 `close_victory`、`decisive_victory`、`heroic_victory`、`pyrrhic_victory`、`close_defeat`、`decisive_defeat`、`crushing_defeat`、`valiant_defeat`），以及战斗类型判断（海战、攻城、伏击、夜战）。[Extra Scripting Guides](https://wiki.totalwar.com/w/Total_War:_ATTILA_KIT_-_Extra_Scripting_Guides)
- **[社区·解包]** WH3 的待结算战斗对象带 `attacker_strength`、`defender_strength`、`has_been_fought`、`is_auto_resolved`、`ended_with_withdraw`、`quest_mission_key` 等字段。**[推断]** 自动结算和手动战斗共用同一个战斗对象，用 `is_auto_resolved` 区分。[model_hierarchy](https://github.com/Shazbot/WH3-Dump/blob/main/documentation/script/campaign/model_hierarchy.html)
- **[社区·解包]** 战役脚本文档说明，默认进入战斗前会保存战役（有参数可以“Launch the battle immediately without saving the campaign first”），战后重新载入，所以战役状态在战斗期间是冻结的存档。同一文档的“custom battlefield”记录随存档保存，原因是“records which modify the autoresolver result must remain active even after the campaign has loaded back in after battle”。[campaign_manager 文档](https://github.com/Shazbot/WH3-Dump/blob/main/documentation/script/campaign/campaign_manager.html)
- **[社区·解包]** 战后规则以结果等级为键，例如夺取饰品的概率按险胜、决定性胜利、英勇胜利为 20、30、40；俘虏规则手动和自动结算不同（“captured in real-time battles or by chance after auto-resolved battles”）。[campaign_variables](https://github.com/Shazbot/WH3-Dump/blob/main/db/campaign_variables_tables/data__.tsv)
- **[官方]** 回写缺陷的补丁说明：增援军的唯一单位是将领时，其战死没有写回战役（Rome II Patch 12，[说明](https://wiki.totalwar.com/w/Total_War_ROME_II:_Patch_12)）；WH3 2.0.0 起自动结算也给单位经验。
- **[推断]** 写回既有汇总（击杀与溃逃比例、结果等级），也有逐单位状态（人数比例、经验、将领死亡）。依据：上面的脚本接口。

## 3 战斗地图从哪里来

**Warband**

- **[官方]** 野外战斗：`script_setup_random_scene` 读取主队所在位置的地形类型（`party_get_current_terrain`），映射到 `scn_random_scene_steppe / plain / snow / desert` 和各自的 forest 变体共 8 种，水域用 `scn_water`，桥用 plain，其余地形用默认的 `scn_random_scene`。这些场景带标志 `sf_generate|sf_randomize|sf_auto_entry_points`，即地形由生成器随机生成，入口自动生成。[module_scripts.py](https://github.com/int19h/mb_warband_module_system/blob/master/Module_system%201.171/module_scripts.py)、[module_scenes.py](https://github.com/int19h/mb_warband_module_system/blob/master/Module_system%201.171/module_scenes.py)
- **[官方]** 村庄袭击用该村庄预设的外景（`slot_castle_exterior`）和 `mt_village_raid`；围城用城镇或城堡预设的场景。每个聚居地在初始化脚本里分配专属场景。[module_game_menus.py](https://github.com/int19h/mb_warband_module_system/blob/master/Module_system%201.171/module_game_menus.py)
- **[社区·反编译]** 随机场景的地形种子由玩家队伍的大地图坐标、存档里的战役种子、以及“任务启动次数 % 3”算出，所以同一坐标最多三种变体，随任务启动次数轮换。Android 版反编译，未在 PC 版验证。[Android 反编译](https://github.com/1922828151/MnB_Mobile/tree/master/libMBExpMobile/decompile) Fandom wiki 玩家观察：撤退后重新开战“There will be 3 different maps that will rotate as you retreat.”[Battle](https://mountandblade.fandom.com/wiki/Battle)
- **[推断]** 同一位置、同一遭遇的野战地图不一定相同；聚居地场景固定。依据：上面的种子公式和聚居地专属场景。

**Bannerlord**

- **[官方]** 开发日志 #10：“we divided the world map into segments, with each segment having its own scene assigned to it. When entering a battle, the system looks at the position and direction of the parties on the world map and uses this to determine the spawn points in the scene. The scenes are modelled on the world map terrain”。当时有 72 个战斗场景。[Development Update #10](https://store.steampowered.com/news/app/261550/view/4224939931832809813)。同期的 e1.7.0 补丁说明：“The spawn positions and the facing directions of each side inside the field battle mission are now determined by the encounter position and direction on the campaign map.”并新增 23 个场景，总数 72。[e1.7.0 说明](https://store.steampowered.com/news/app/261550/view/4224939386876436351)
- **[社区·反编译]** 选场景：按位置查引擎提供的地块索引（`MapPatchData`，含场景编号和格内位置），选 `MapIndices` 包含该编号的场景；有多个时按地形匹配或随机取一个；没有时按所在地形类型找，再不行随机取并触发断言。场景表是 `sp_battle_scenes.xml`，场景 id 形如 `battle_terrain_020`。[DefaultSceneModel.cs](https://github.com/BannerlordCode/bannerlord-1.4.7/blob/main/TaleWorlds.CampaignSystem/GameComponents/DefaultSceneModel.cs)、[mod 文档](https://raw.githubusercontent.com/Litauen/docs.bannerlordmodding.lt/HEAD/editor/battle_scene_grid.md)
- **[社区·反编译]** 野战传给战斗场景的参数：场景名、主队所在位置的地形类型、`NeedsRandomTerrain = false`、`RandomTerrainSeed = MBRandom.RandomInt(10000)`、该位置的大气（时间、季节、天气）、格内位置 `PatchCoordinates`、遭遇方向 `PatchEncounterDir`（攻方统帅位置减守方统帅位置，归一化）、难度系数。[MenuHelper.cs](https://github.com/BannerlordCode/bannerlord-1.4.7/blob/main/TaleWorlds.CampaignSystem/Helpers/MenuHelper.cs)
- **[社区·反编译]** 出生点：把格内位置换算成场景里的“遭遇点”（`GetPatchSceneEncounterPosition`），在场景预设的出生路径里按“方向与遭遇方向越一致、离遭遇点越近，得分越高（距离权重 1000/(1+距离)）”选一条，以路径上离遭遇点最近处为分界点（路径方向相反时两方互换）；场景没有分块信息时随机选一条，分界点取在路径长度的 37% 到 63% 之间。[BattleSpawnPathSelector.cs](https://github.com/BannerlordCode/bannerlord-1.4.7/blob/main/TaleWorlds.MountAndBlade/BattleSpawnPathSelector.cs)
- **[社区·反编译]** 攻城和村庄战不走分块选图，而是用该聚居地自己的场景：村庄战 `LocationComplex.GetScene("village_center", 城墙等级)`，攻城战 `GetLocationWithId("center").GetSceneName(城墙等级)`，并传入各段城墙的生命值比例和攻城器械。[PlayerEncounter.cs](https://github.com/BannerlordCode/bannerlord-1.4.7/blob/main/TaleWorlds.CampaignSystem/Encounters/PlayerEncounter.cs)、[PlayerSiege.cs](https://github.com/BannerlordCode/bannerlord-1.4.7/blob/main/TaleWorlds.CampaignSystem/Siege/PlayerSiege.cs)
- **[官方]** 场景数量持续增加：开发日志 #12 写到再加 6 个战斗地形，总数 76 个（[Development Update #12](https://store.steampowered.com/news/app/261550/view/5124582150127345736)）；v1.5.0 beta 又新增 4 个战斗地形（[beta 说明](https://store.steampowered.com/news/app/261550/view/1841579228662390)）。
- **[推断]** 同一位置、同一遭遇的场景和出生位置由坐标和方向决定，这部分没有随机数；会变的是大气（种子取自战役时间的秒数）、`RandomTerrainSeed`（引擎是否使用未核实）和普通兵的装备组合。依据：上面的代码。

**Total War**

- **[官方]** Attila（2015）的 Terry 工具文档：战斗地图由“main battle tile maps which define the entire battle map derived from the campaign map”组成。数据包括：地块（高度图、混合图、树石建筑等刚性模型）、地块数据库、tile map、低频高度图、禁行图（验证战斗位置）、战斗 catchment、气候图、地块升级。“A catchment is a square area on the map that defines the type of battle that can take place in a specific location.”[Terry 概述](https://wiki.totalwar.com/w/Total_War:_ATTILA_Terry.html)
- **[官方]** 开战位置要通过禁行图校验：“a battle will only spawn in a location that has a valid 8x8 pixel area with no red pixels within”。聚居地用专门地块：“The game works by looking for the nearest settlement based on the location of the battle triggered from the campaign map.”战役触发的战斗不读“主导文化图”，改用代码驱动方案。[Terry 地块图](https://wiki.totalwar.com/w/Terry_-_Creating_a_Battle_Tile_Map)、[Terry 聚居地](https://wiki.totalwar.com/w/Terry_-_Settlements)
- **[官方]** 气候图（5 种气候）决定贴图、树和环境；默认“生成的地图”，脚本可在某个坐标改用自定义地图（“Instead of a generated map, use a custom map for this location”）。[Terry 环境](https://wiki.totalwar.com/w/Terry_-_Environment)、[Campaign Script Interface](https://wiki.totalwar.com/w/Total_War:_ATTILA_KIT_-_Campaign_Script_Interface)
- **[官方]** Rome II 手册：“The topography of the battlefield is a direct representation of the corresponding area on the campaign map where the attack was made.”[Rome II 手册](https://r2enc.totalwar.com/en/manual/single-player/0016_enc_page_campaign_play_military_attack)
- **[官方]** 部署方向取自战役：Rome: Total War “your army always deploys on a section of the battlefield that matches the line of its approach on the campaign map”（[Mastering Battles](https://wiki.totalwar.com/w/Mastering_Battles.html)）；Attila 的增援军入场角度用与正北的夹角定义（[Army Tags](https://wiki.totalwar.com/w/Total_War:_ATTILA_KIT_-_Army_Tags)）；WH1 Update 3、WH3 1.1.0 把增援改为按其战役位置相对战场的方向入场。
- **[社区·解包]** WH3 9.0 的数据表有 1319 个战斗定义（classic 809、ambush 110、river_crossing 105、unfortified_settlement 92、siege 38 等），战役图上的颜色区域、战斗类型、攻守阵营和所需 tile upgrade 映射到一个战斗组（`battle_group`，共 3622 条，键无重复）；tile upgrade 含阵营和战役状态（如混沌腐化）；聚居地等级 1 到 5 对应不同城防；气候和季节组合共 41×4 条，天气全部为 dry、概率 100。[battles_tables](https://github.com/Shazbot/WH3-Dump/blob/main/db/battles_tables/data__.tsv)、[battle_catchment_override_battle_mappings](https://github.com/Shazbot/WH3-Dump/blob/main/db/battle_catchment_override_battle_mappings_tables/data__.tsv)
- **[官方]** 选图错误是补丁修复项，说明地图按位置分配：WH3 3.0.0 “Updated the campaign map battle selection ... to ensure that the correct battle tiles are displayed”；5.2.0 “Fixed an issue where the wrong battle map would load when fighting near mountains in Ulthuan.”Pharaoh Dynasties 把 catchment 从 4×4 扩为 6×6 个地块，每块 256 米。[WH3 3.0.0](https://web.archive.org/web/2024/https://www.totalwar.com/news/total-war-warhammer-iii-update-300)、[WH3 5.2.0](https://web.archive.org/web/2024/https://www.totalwar.com/news/total-war-warhammer-iii-patch-520)、[Pharaoh 开发说明](https://web.archive.org/web/2024/https://www.totalwar.com/news/total-war-pharaoh-dev-update-battle)
- **[推断]** 官方资料里选图是位置、战斗类型、阵营、地块升级的确定性映射，没有找到战场随机种子的说明。依据：WH3 键无重复、天气概率固定为 100。同一位置是否每次得到同一战场，官方未写明。

## 4 撤退、中断、存档、时间与增援

### 4.1 撤退与中断

**Warband**

- **[官方]** 手册：按 Tab 撤退，条件是离敌军足够远；中途脱离会放弃已得的战利品；自动结算的部队全灭后可以撤离；亲自上阵被击败而队伍里还有健康的人时，他们会把你拖出战场，之后可选择逃跑或再战；没有人救你则战斗结束，敌方可能俘虏部分或全部部队。[Warband 手册](https://cdn.akamai.steamstatic.com/steam/apps/48700/manuals/PDX5505US_Warband_Manual_US.pdf)
- **[官方]** 代码：Tab 键在已获胜时直接结束，否则离敌 15 米以内显示 “Can't retreat”，否则询问是否撤退；确认后若任务已超过 20 秒，调用 `script_simulate_retreat(10, 20, 1)` 按一次撤退伤亡结算，统计伤亡后结束任务。主角倒下后延迟 4 秒触发同样的 `simulate_retreat`、战斗结果记为失败并结束任务，剩余战斗不会转为自动结算。[module_mission_templates.py](https://github.com/int19h/mb_warband_module_system/blob/master/Module_system%201.171/module_mission_templates.py)

**Bannerlord**

- **[社区·反编译]** 战斗中退出：主角已倒下，或离任何敌人超过 5 米才允许（`IsPlayerCloseToAnEnemy(5f)`）。退出直接结束战斗，不转成模拟，此前的伤亡已经写回。防守方在攻城战里退出视为投降。[BattleEndLogic.cs](https://github.com/BannerlordCode/bannerlord-1.4.7/blob/main/TaleWorlds.MountAndBlade/BattleEndLogic.cs)
- **[官方]** 主角倒下后战斗由 AI 继续，v1.3.4 起可以接管另一名士兵：“You can now take direct control of another troop after getting knocked out in battles and sieges.”[v1.3.4 说明](https://store.steampowered.com/news/app/261550/view/1817483467032776)
- **[官方]** 战斗未决时回到遭遇菜单（见 1.1）。1.0 起攻城战中撤退会移除进攻方的攻城器械（见 2.2）。

**Total War**

- **[官方]** 战前撤退：Rome II 手册 “If under attack it is usually only possible to use the button once, so if the enemy attack again in the same turn you will be forced to face them in battle.”[Rome II 手册](https://r2enc.totalwar.com/en/manual/single-player/0016_enc_page_campaign_play_military_attack)
- **[官方]** 战中撤退：Rome: Total War 的战场边缘有红线标出的界外，“Units cannot be ordered beyond this line, but they will go there when routing or withdrawing.”战前面板的白旗则是取消战斗，“your army will withdraw some distance to a safe location”。[Mastering Battles](https://wiki.totalwar.com/w/Mastering_Battles.html) WH3 2.0.0 给旧的伏击战地图加了新的撤离机制。3K 防守方可经标记的撤离点撤退（社区 wiki）。
- 战中撤退后没撤出的单位怎么算：**未找到官方原文**。脚本接口里只有 `ended_with_withdraw` 标记。

### 4.2 存档

- **Warband**：**[社区]** 界面文字 `ui_cant_quick_save` 是 “Can't Quick-save during battle...”，出现在 TLD mod 的英文语言文件里，推测沿用自原版文本（[tldmod ui.csv](https://github.com/tldmod/tldmod/blob/master/Languages/en/ui.csv)）。**[社区·反编译]** Android 版反编译显示任务模板带 `mtf_battle_mode` 标志时拒绝快速存档（[Android 反编译](https://github.com/1922828151/MnB_Mobile/tree/master/libMBExpMobile/decompile)）。Esc 菜单是否可存档未验证。
- **Bannerlord**：**[社区·反编译]** 战斗中的 Esc 菜单只有 Return to the Game、Options、Photo Mode、Exit to Main Menu 等项，没有存档；存档的执行挂在大地图状态的更新里（`SaveHandler.SaveTick / CampaignTick`）。[MissionGauntletSingleplayerEscapeMenu.cs](https://github.com/BannerlordCode/bannerlord-1.4.7/blob/main/TaleWorlds.MountAndBlade.GauntletUI/Mission/Singleplayer/MissionGauntletSingleplayerEscapeMenu.cs)、[MapState.cs](https://github.com/BannerlordCode/bannerlord-1.4.7/blob/main/TaleWorlds.CampaignSystem/GameState/MapState.cs) **[官方]** v1.3.0 beta 说明有一条修复：“Fixed the simulation context tooltip on Send Troops showing incorrect information when loading a save during an encounter.”说明在遭遇菜单里存的档可以读回。[v1.3.0 beta 说明](https://store.steampowered.com/news/app/261550/view/1810503566552360)
- **Total War**：**[官方]** 战役每回合自动存档，玩家回合内可以手动存档，传奇难度除外。[Total War Academy](https://academy.totalwar.com/campaign-how-turns-work/) **[社区·解包]** 序章提示 “You can always quick-save before a battle. You can re-load from the menu.”官方 4.1.0 修复了“读档后战前按钮没有反映玩家已作出的选择”（[WH3 4.1.0](https://web.archive.org/web/2024/https://www.totalwar.com/news/total-war-warhammer-iii-patch-410)）。战斗中途能否存读档，官方未写明；玩家称战斗地图上不能读档（[Steam 讨论](https://steamcommunity.com/app/594570/discussions/0/3374780959384691072)）。
- **[社区]** WH3 玩家在 CA 论坛请求“在战斗结束后加一个快速存档”，因为在敌方回合里打完战斗后，下一步的外交弹窗可能导致崩溃，而这时没有可用的存档点（[CA 论坛，2025-09](https://community.creative-assembly.com/total-war/total-war-warhammer/forums/8-general-discussion/threads/11340-could-we-get-a-quicksave-button-for-after-a-battle-is-fought)）。
- **[推断]** 三个系列都不把战斗场景内部状态放进存档，存档发生在战斗之外；Total War 在敌方回合里的战斗之后没有存档点。依据：以上各条。

### 4.3 读档后自动结算的结果

- **Bannerlord**：**[社区·反编译]** 全局随机数生成器 `Game.RandomGenerator` 是可存档属性，其状态随存档保存。[Game.cs](https://github.com/BannerlordCode/bannerlord-1.4.7/blob/main/TaleWorlds.Core/Game.cs) **[社区]** 2020 年有玩家报告：在“攻击或派兵”的菜单里存档后反复模拟，结果完全一致，[论坛帖 Web Archive](http://web.archive.org/web/20231018173830/https://forums.taleworlds.com/index.php?threads/simulation-battles-are-rigged.417819/)。**[推断]** 读档后直接重复同样操作会复现结果，中间有别的操作消耗随机数就会变；手动战斗不能复现。
- **Total War**：**[官方]** WH3 2.2.0 “Fixed an issue where auto-resolve battle results would change after loading a quick save.”（[说明](https://web.archive.org/web/2024/https://www.totalwar.com/news/total-war-warhammer-iii-update-220)），说明开发者把“同一存档、同一操作得到同一自动结算结果”当作应有行为。**[社区]** 玩家称战役随机数带种子，“If you just reload you have the same seed and the rng will be the same.”（[r/totalwar](https://www.reddit.com/r/totalwar/comments/tepl99/is_wh3_savescumming_proof)）。
- **Warband**：**[官方]** 开发者在 AI 战斗的随机结束判定处留了注释：“Players can make save loads and change history because these random values are not determined from random_slots of troops”。[module_scripts.py](https://github.com/int19h/mb_warband_module_system/blob/master/Module_system%201.171/module_scripts.py) **[社区·反编译]** 伤亡抽样用全局随机数，种子取自启动时间，存档只写战役种子；未找到玩家或开发者对 Warband 读档结果是否变化的明确说法。

### 4.4 战斗期间的大地图时间

- **Bannerlord**：**[社区·反编译]** 大地图只在地图状态是当前状态时才推进（`MapState` 的更新里，`GameStateManager.ActiveState == this` 才调用 `Campaign.RealTick`）。**[推断]** 战斗任务是另一个游戏状态，所以战斗进行中大地图时间不走，其他部队不移动。[MapState.cs](https://github.com/BannerlordCode/bannerlord-1.4.7/blob/main/TaleWorlds.CampaignSystem/GameState/MapState.cs)
- **Warband**：**[社区·反编译]** Android 版反编译里，大地图小时推进只由大地图界面的更新驱动；没有找到官方文字说明，未在 PC 版验证。
- **Total War**：**[官方]** 战役以回合推进，玩家结束回合后其他阵营依次行动。[Total War Academy](https://academy.totalwar.com/campaign-how-turns-work/) **[社区]** AI 发起的战斗插在其回合中，打完后继续检查其他阵营的回合（[CA 论坛，WH3，2025-09](https://community.creative-assembly.com/total-war/total-war-warhammer/forums/8-general-discussion/threads/11340-could-we-get-a-quicksave-button-for-after-a-battle-is-fought)）。

### 4.5 其他部队能否中途加入，增援怎样出场

- **Warband**：**[官方]** 手册：“Only those parties that started the initial battle can have reinforcement waves: another party cannot join from the world map.”[Warband 手册](https://cdn.akamai.steamstatic.com/steam/apps/48700/manuals/PDX5505US_Warband_Manual_US.pdf) 代码印证：`script_let_nearby_parties_join_current_battle` 在进入遭遇菜单时运行，条件是该队伍不在战斗、不附属他队、不在城镇，且与玩家距离小于 5（夜晚 3，强盗 3 / 夜晚 2）。已经发生的双方战斗，玩家可选择 “Move in to help / Rush to the aid / Don't get involved”。
- **Warband 增援波**：**[官方]** 任务模板 `lead_charge` 里增援按波出场，进攻方最多 2 波，每波 `add_reinforcements_to_entry` 7（相对数），触发条件是该侧存活人数（按战场规模归一化）低于 6，且任务已进行 10 秒以上。[module_mission_templates.py](https://github.com/int19h/mb_warband_module_system/blob/master/Module_system%201.171/module_mission_templates.py)
- **Bannerlord**：**[社区·反编译]** 遭遇开始时，`PlayerEncounter.CheckNearbyPartiesToJoinPlayerMapEvent` 检查附近其他队伍是否加入，半径 `GetEncounterJoiningRadius` 为 3；劫掠、攻城突击、藏身处等遭遇不做这项检查。战斗场景里的兵只来自开打时地图事件里的双方名册。**[推断]** 战斗进行中不会有新部队加入。依据：上面的代码，以及官方说明里增援只写“波”（同一批人分批出场）。[PlayerEncounter.cs](https://github.com/BannerlordCode/bannerlord-1.4.7/blob/main/TaleWorlds.CampaignSystem/Encounters/PlayerEncounter.cs)、[DefaultEncounterModel.cs](https://github.com/BannerlordCode/bannerlord-1.4.7/blob/main/TaleWorlds.CampaignSystem/GameComponents/DefaultEncounterModel.cs)
- **Total War**：**[官方]** Rome: Total War 的战前面板列出相邻军队作为增援，并计入胜率。[Mastering Battles](https://wiki.totalwar.com/w/Mastering_Battles.html) **[社区·解包]** WH3 的界面文字：“Reinforcing armies appear at the edge of the battlefield terrain during the fighting.”数据表里有一组 `reinforcement_arrival_time_*` 变量（最小距离 0.1、最大距离 2.0、将领等级系数 0.7）和 `reinforcement_radius`（1.0），由变量名推断到达时间与距离和将领等级有关；自动结算里的增援时间变量 `autoresolver_battle_reinforcement_time` 为 0。[campaign_variables](https://github.com/Shazbot/WH3-Dump/blob/main/db/campaign_variables_tables/data__.tsv)
- **[官方]** 增援的规则演变：WH3 2.3.0 “Attacking an army while in the Ambush stance will no longer prevent your opponent from bringing reinforcements.” WH3 2.4.0 守军回到开战就入场，不再当作增援。WH3 4.0.0 玩家把增援交给 AI 控制时，对方也会把同样数量的增援军交给 AI。[WH3 2.3.0](https://web.archive.org/web/2024/https://www.totalwar.com/news/total-war-warhammer-iii-update-230)、[WH3 2.4.0](https://web.archive.org/web/2024/https://www.totalwar.com/news/total-war-warhammer-iii-update-240)、[WH3 4.0.0](https://web.archive.org/web/2024/https://www.totalwar.com/news/total-war-warhammer-iii-update-400)

## 5 已知问题和玩家评价

### 5.1 官方公开的问题与调整

- **Bannerlord 官方修复**（均来自 Steam 公告）：v1.3.13 “Fixed a bug that prevented all available troops from being used in simulations, which could previously cause losing battles due to limited troop numbers and entire sieges being lost as a result.”（[说明](https://store.steampowered.com/news/app/261550/view/1819386365106738)）；v1.4.5 修复攻城方在攻城战里撤退后被整军击败、玩家可以俘虏全部剩余部队并拿走全部战利品（[说明](https://store.steampowered.com/news/app/261550/view/1833334318569123)）；v1.1.0 修复玩家在战斗中撤退后攻城仍进入主堡战（[说明](https://store.steampowered.com/news/app/261550/view/5070528176557709918)）；e1.6.2 修复输掉攻城模拟后玩家军团里的领主消失（[说明](https://store.steampowered.com/news/app/261550/view/3936699406866711581)）。
- **Total War 官方修复**：WH1 Update 2 自动结算总把玩家当作进攻方；WH3 1.1.0 “attacking an enemy Army in a settlement resulted in better outcomes than when an enemy Army attacked the player”；WH3 5.1.3 单体单位受伤后的自动结算偏差；3K 1.6.1 “autoresolve values were wildly off if continuing a campaign from the launcher”；Pharaoh 1.1.0 战前预测偶尔不准。[WH3 1.1.0](https://web.archive.org/web/2024/https://www.totalwar.com/news/total-war-warhammer-iii-update-110)、[3K 1.6.1](https://web.archive.org/web/2024/https://www.totalwar.com/news/total-war-three-kingdoms-patch-161)、[Pharaoh 1.1.0](https://web.archive.org/web/2024/https://www.totalwar.com/news/total-war-pharaoh-patch-notes-110)
- **Warband**：TaleWorlds 没有公开关于 auto-calc 的说明；1.172 到 1.174 的公告不涉及。模块源码里留有未解决项，例如攻城自动结算没有与溃逃数比较 “(TODO : compare with num_routed_us)”，盟军后加入时俘虏分配未处理 “#TODO: This doesn't handle prisoners if our allies joined battle after us.” [module_game_menus.py](https://github.com/int19h/mb_warband_module_system/blob/master/Module_system%201.171/module_game_menus.py)

### 5.2 玩家评价

均为 **[社区]**。帖子只选能代表重复出现的说法的。

**Total War**

- 差距偏向玩家（WH3）：[Steam 2026-06-01](https://steamcommunity.com/app/1142710/discussions/0/657108053777366678)（31 条回复）矮人少量单位对大军，自动结算判“close victory”，手动却在接战前就损失一半军队，原话 “heavy bias towards player victory feels completely backwards”；[r/totalwar 1jrsx5g](https://www.reddit.com/r/totalwar/comments/1jrsx5g/why_am_i_so_much_worse_than_the_autoresolve/) 高赞回复（112 赞）“If you're playing normal or below, you definitely get a bonus to auto resolve in your favor.”
- 自动结算漏算：[r/totalwar 1dbww6c](https://www.reddit.com/r/totalwar/comments/1dbww6c/autoresolve_is_strategically_superior_to_fighting/)（980 赞）伏击战里自动结算对方全灭而手动要追杀、全局召唤与法术不计入；[CA 论坛 2024-08-30](https://community.creative-assembly.com/total-war/total-war-warhammer/forums/8-general-discussion/threads/6939-let-s-talk-about-autoresolve-and-why-it-needs-a-revision-bad) 认为预览“Too Accurate”、败方总被全歼，并说 AI 对 AI 全走自动结算，所以非传奇领主被人为压在低等级。
- 玩家自己的对照：[r/totalwar 1o8qq6m](https://www.reddit.com/r/totalwar/comments/1o8qq6m/i_almost_always_perform_worse_than_autoresolve/)（WH3，2025-10-17）存档后手动打、读档后自动结算，击杀与损失比几乎相同，但手动损失了一个单位而自动结算没有；[r/totalwar uljr1b](https://www.reddit.com/r/totalwar/comments/uljr1b/does_anyone_else_struggle_to_get_the_same_results/) 高赞回复（43 赞）“There are also battles where I can handily destroy an opponent that the autoresolver thinks I'll take heavy casualties on. Usually siege battles”。
- 高价值单位被弱敌击杀（WH2）：[r/totalwar 7xqj7d](https://www.reddit.com/r/totalwar/comments/7xqj7d/autoresolve_functionality_in_wh2/) 评论（10 赞）称“a full 20 stack against 3 units of infantry and an archer”仍会让两个炮兵单位全灭；CA 在同一帖里承认“more main stream problems especially related to #4”（保护高价值单位）并说正在处理炮兵（见 1.3）。
- 战场地图与位置不符：[Steam WH2 2020-05-02](https://steamcommunity.com/app/594570/discussions/0/2266943550310521480) “dwarfs get attacked in the hills and I get a map with no hills in it”，回复称地图按省份预制；[r/totalwar 1938geo](https://www.reddit.com/r/totalwar/comments/1938geo/why_are_so_many_battle_maps_in_total_war/)（291 赞）高赞回复（38 赞）把这点当作 Rome 1 更好的地方：“Where you fought your battle was determined by the actual campaign map”。
- 增援与撤退：[r/totalwar psh6y2](https://www.reddit.com/r/totalwar/comments/psh6y2/not_being_able_to_retreat_your_army_when_a_nearby/)（Rome II，89 赞）“FORCED to reinforce against 4 stacks and no retreat button in sight”；[r/totalwar 14tigym](https://www.reddit.com/r/totalwar/comments/14tigym/auto_resolve_vs_actual_fight/) 的回复称“the auto is taking into account units that aren't arriving on the field yet”。

**Bannerlord**

- 高阶兵死于弱敌：[r/mountandblade g3s7q9](https://www.reddit.com/r/mountandblade/comments/g3s7q9/what_you_should_know_about_auto_calc_kind_of_sad/)（1376 赞，2020-04）一个改模拟的 mod 作者解释逐单位对打的机制。评论（12 赞）称“99% of the in-game fight results are dictated by this mechanic, this makes the map/factions evolve in stupid ways”；另一条（6 赞）称手动冲锋可以几乎无损地打掉 200 名敌人，自动结算却会损失 40 到 50 人；[r/Bannerlord sg73e0](https://www.reddit.com/r/Bannerlord/comments/sg73e0/auto_resolve_open_field_is_broken/) 称对 10 个劫匪自动结算时“one tier 6 troop will die”“every time”，高赞回复（56 赞）“If you want to rely on auto resolve, leveling up tactics is a must.”
- 高 Tactics 或守城时模拟太划算：[r/Bannerlord 15pytju](https://www.reddit.com/r/Bannerlord/comments/15pytju/send_troops_vs_actually_playing/) 评论 “I've had 300 people kill 2800 just by simulating.”；[r/Bannerlord 1hflhqk](https://www.reddit.com/r/Bannerlord/comments/1hflhqk/send_troops_results_in_fewer_losses/) 100 人的战斗自动结算伤亡 2 到 3 人，手动 7 到 8 人以上。
- 要塞战丢兵：[r/Bannerlord 1pes6ux](https://www.reddit.com/r/Bannerlord/comments/1pes6ux/the_actual_reason_we_keep_losing_the_battles_for/)（396 赞，2025-12-05）称游戏无视玩家选的兵、只派最低阶兵进主堡，导致整军输掉；官方 v1.3.13 在约两周内修复（见 5.1）。
- AI 也依赖模拟：[r/Bannerlord xic27u](https://www.reddit.com/r/Bannerlord/comments/xic27u/one_of_the_biggest_holes_in_this_game_is_that_the/)（269 赞）“the AI only engage in combat when the simulation calculation says that they are practically guaranteed to win.”
- 战场规模与增援：[r/Bannerlord 13potib](https://www.reddit.com/r/Bannerlord/comments/13potib/higher_battlesizelimits_giving_you_an_advantage/) 上限调到 1000 后“defensive siege battle became nearly impossible to win”，低上限时“my reinforcements arrived way later than the opponents”；[r/Bannerlord vig5cj](https://www.reddit.com/r/Bannerlord/comments/vig5cj/) “Retreat Cheesing”帖中，回复（13 赞）称撤退后再进入会“Resets unit health, ammo and starting positions of both armies”，玩家用它在兵力悬殊时反复消耗对方。
- 地形：[r/Bannerlord 1pj4rhh](https://www.reddit.com/r/Bannerlord/comments/1pj4rhh/battlefield_not_matching_up_with_terrain/)（2025-12）“9/10 cases, I just get a flat steppe battlefield”。
- 社区反推的公式（BL v1.3.2 beta，gist）与反编译代码一致：[gist](https://gist.github.com/jzebedee/be076d28f162c8d05fd7d2d72109a46f)。

**Warband**

- [Steam 2018-09-18](https://steamcommunity.com/app/48700/discussions/0/1732087824982805535) “the auto-calc severely undervalues quality”；[Steam 2018-09-09](https://steamcommunity.com/app/48700/discussions/0/3338745825901040494) 900 人对 250 人，“I lost 150 troops and the enemy lost 0.”；[r/mountandblade 34ibzu](https://www.reddit.com/r/mountandblade/comments/34ibzu/how_is_autoresolve_calculated_and_can_i_use_this/) 高赞回复（46 赞）“Auto-resolved is designed to punish the player for not participating in battles, it's never a wise idea unless you absolutely can't be asked to fight the enemy.”
- 被击倒后战斗结束：[r/mountandblade dm8h5y](https://www.reddit.com/r/mountandblade/comments/dm8h5y/why_does_the_battle_end_when_i_get_knocked/) 回复（11 赞）“In vanilla, the battle automatically ends when you're knocked unconscious.”并说几乎所有 mod 都加了延续战斗的功能。
- 增援波数有限：[r/mountandblade b5l2oo](https://www.reddit.com/r/mountandblade/comments/b5l2oo/just_found_an_amazing_little_trick_for_late_game/)（2019-03）攻城时进攻方最多 5 波，玩家用 Tab 撤退强制再开一场。
- 维基只写到“公式逐轮、偏向大队伍、单位在模拟里比实战弱”，没有出处。[Fandom Battle](https://mountandblade.fandom.com/wiki/Battle)

### 5.3 重复出现的模式

均为 **[推断]**，依据写在括号里。

- 高价值单位被弱敌击杀，三个系列都有至少两个独立来源（Warband：Steam 2018-09、34ibzu；Bannerlord：g3s7q9、sg73e0；Total War：7xqj7d、官方 “Protect unique high quality units” 目标）。
- 偏差方向随版本、难度、技能而变，玩家据此分成“尽量自动结算”和“尽量手动”（WH3 偏向玩家的 Steam 2026-06 与 1jrsx5g，对比 3K/WH2 偏向 AI 的帖；Bannerlord 高 Tactics 与守城对低 Tactics）。
- 同一套结算也驱动 AI 的开战判断和 AI 之间的战斗，偏差因此影响整个战役（xic27u、CA 论坛 2024-08-30、WH3 8.1 官方说明）。
- 读档后重打或重算很常见（WH3 2.2.0 的修复；Bannerlord 玩家“we always save before we send in troops”；Warband 的开发者注释）。
- 战斗数量多使人倾向自动结算：[Steam WH3 2026-07-17](https://steamcommunity.com/app/1142710/discussions/0/570417860285011076) “wouldn´t want to fight 170 battles per campaign manually”；Bannerlord 1pes6ux “sieges have become a real chore”；CA 目标 “Don't make it tedious”。
- 战场地图是否对应大地图位置，各作做法不同且都有人批评（Rome II 手册的“直接呈现”，WH2/WH3 的预制地图，Bannerlord 的分块场景及 2025 年的错配报告）。

## 可借鉴的做法

这些是从上面的事实归纳出的做法，不构成项目决定，均为 **[推断]**。

1. **把一次遭遇做成独立对象，手动战斗与自动结算都向它提交结果。** Bannerlord 的 `MapEvent`、Total War 的待结算战斗对象（含 `is_auto_resolved`）都是这样；Bannerlord 的两条路径共用同一组写回方法。（2.2）
2. **两条路径共用伤亡判定和奖励规则，把差别限制在少数显式系数上。** Bannerlord 的经验系数（0.9 对 1.0）、钝器致昏概率；Warband 共用胜利菜单。官方为此做过补丁（e1.1.0、e1.6.3）。（1.3）
3. **自动结算只读摘要信息，大地图位置的地形同时服务自动结算和选场景。** Bannerlord 的自动结算按大地图位置的地形和天气选兵种修正（`GetContextForPosition`），战斗场景按同一位置的分块和地形选择，读的是同一份大地图位置数据。（1.2、3）
4. **手动战斗设规模上限和增援波，没打完时有出口。** Bannerlord 1.0 回到遭遇菜单、兵力悬殊时转自动结算；Warband 手册的分阶段与脱离。（1.1、2.1）
5. **选场景只用少量大地图信息：所在分区、地形类型、接近方向、聚居地、时间和天气。** Bannerlord 的 `MissionInitializerRecord`、Attila 的 catchment 与禁行图、Warband 的地形类型映射。（3）
6. **战前给预测，并给玩家少量影响自动结算的手段。** Total War 的结果等级与损失估计、战斗姿态；Bannerlord 的选兵与 Tactics。预测误差本身也可以作为参数（WH3 的 5 档）。（1.3）
7. **战斗期间冻结大地图，战斗中不存档，存档点放在战斗之外。** Warband 和 Bannerlord 如此；Total War 是回合制，存档在玩家回合里，敌方回合中的战斗之后没有存档点（玩家为此提过请求）。（4.2、4.4）
8. **参战方在开战前确定，增援只来自这份名单。** Warband 手册与 Bannerlord 的波；Total War 把增援做成战场边缘入场、带延迟的机制，自动结算里不计延迟。（4.5）
9. **随机数状态进入存档，使“同一存档、同一操作”得到同一自动结算结果。** Bannerlord 如此；Total War 把它当作应有行为并修复过破坏它的缺陷；Warband 的开发者承认可以读档改写历史。（4.3）
10. **把自动结算的设计目标写成可检查的条目。** CA 公开的四条目标（有合理损失、不繁琐、玩家总能打得更好、保护高价值单位）和它们互相冲突的事实，可直接用来评估自己的结算。（1.3）

## 与 #198 各问题的对应

只列本文哪一节有可用的事实，不给结论。

| #198 的问题 | 本文位置 |
| --- | --- |
| 核心模拟层提供给场景的信息 | 2.1、3 |
| 观察时场景结果是否写回；由玩家选择自动结算还是进入场景 | 1.1、1.3、2.2、4.1 |
| 同一位置每次生成的场景是否相同 | 3（各游戏的 [推断]） |
| 确定性范围、读档后结果是否复现 | 4.3 |
| 打开场景是否成为带模拟位置的输入、场景打开时能否存档 | 4.2、4.4 |

## 资料缺口与不确定之处

- **Total War 的自动结算公式没有官方公开。** WH3 的数值来自第三方解包的 9.0 数据，变量含义由名称推断；Rome II、Attila、3K、Pharaoh 的自动结算数值没有找到。Shogun 2、Medieval II、Troy、Thrones of Britannia 没有专门查。
- **Total War 官方没有写明**：战斗中途存读档，战中撤退后没撤出的单位怎么算，战场随机种子，自动结算与手动结果差距的统计，对“败方全歼”和“战场地图与位置不符”的公开回应。Steam 手册（WH1、WH2、WH3、3K、Rome II EE、Attila）查过，不含自动结算、增援、撤退内容。
- **Bannerlord 的反编译是 v1.4.7，当前稳定版是 v1.4.8**；1.4.8 的公告里与战斗相关的只有一条多人模式修复，官方 API 文档的方法列表与反编译一致，但实现没有逐行比对。时间冻结、战中不能存档、读档后复现是代码推断，没有实测。`RandomTerrainSeed` 在 `NeedsRandomTerrain` 为 false 时引擎是否使用未核实。mod 文档（Litauen）称索引图的绿色通道决定入场方向，与代码把第二字节当作格内位置、方向取自双方统帅位置不一致。
- **Warband 的引擎侧数值**（伤亡抽样、地形种子、存档、时间）不在 Module System 里，只有第三方的 Android 版反编译和 PC 版逆向笔记，未在 PC 1.174 验证。TaleWorlds 论坛被拦截，开发者在论坛里对自动结算和增援的解释没有找到。原版 1.011 与 Warband 的差异没有查，只知道自动结算公式在 1.125、1.126 改过。
- **玩家评价**多数为单一来源；“低难度约 300% 加成”只出自玩家说法；Reddit 的赞数和评论数是存档值；Steam 帖没有显示年份的按搜索元数据推定。Troy、Attila 近年的自动结算评价只搜到零散帖，未采用。
- 未覆盖：Bannerlord 的海战、藏身处、领主大厅、加入他人军团的流程。
