# 不建模轨道几何的线路容量与调度

研究票：[研究：不建模轨道几何的线路容量与调度](https://github.com/odradekk/train/issues/197)

研究日期：2026-10-03

本文只记录有来源的事实、来源链接和可借鉴的做法，不替[列车在图上的运行与调度](https://github.com/odradekk/train/issues/203)做决定。

## 来源分级与读取说明

每条事实后标注类型：

- **一手**：标准、法规、官方规章与手册、同行评审论文和学位论文、官方文档、开发者官方文章、官方源码。
- **社区**：维基（含游戏社区维基）、百科、课件、手册转载页。
- **推断**：本文作者的归纳，句内写明依据。

UIC 406 原文需要购买，没有读到。本文对它的描述来自论文对它的转述，均在句内说明。国内标准大多没有公开全文，国内公式取自百科和课件（社区）。没读到或没核实的内容列在最后一节。

本文用"占用时间窗"表示"某个资源在一段时间内被某列车独占"，对应票中的"时隙"。

## 结论

1. **容量不是一个固定的数。** UIC 认为容量取决于列车数、平均速度、车流异质性和稳定性之间的平衡，没有唯一的真实定义（一手，论文转述）。欧盟指令把容量定义为安排列车运行线的可能性，把运行线定义为在给定时段内在两地之间运行一列车所需的基础设施容量（一手）。见 2.1。
2. **同向间隔来自闭塞时间。** 两列同向列车的最小间隔，是它们的闭塞时间阶梯在某个"关键闭塞分区"刚好相接时的间隔；混跑线上它主要取决于两车速度差（Pachl，一手）。国内称追踪间隔时间，设计下限为高速铁路 3 min、双线旅客列车 5 min（一手）。见 2.2、2.3。
3. **对向和越行靠方向锁和车站。** 单线区间一次只容许一个方向，会车和越行在车站或会让线进行；双线每条线有规定方向，逆向行车需要授权或双向信号，改变闭塞方向前要确认区间空闲（Pachl、技规，一手）。Huisman 等和 Cacchiani 等的宏观模型都把每条轨道设为单向，越行只能在车站或借平行轨道进行（一手）。见 2.4、2.5。
4. **三类计算方法的共同输入是占用时间。** UIC 406 压缩运行图后用占用时间除以时段，建议上限 60%–85%；国内用运行图周期和扣除系数算每日对数；排队模型用占用时间算等待。见 2.6。
5. **车站按"到发线是排他资源"建模。** 一次停靠占用进站进路、到发线和出站进路，前后各留缓冲；到发线满时列车在站外排队。Huisman 等把车站写成先到先服务的多服务台，每条到发线一个服务台；国内技规用"站外停车"和接车次序规则，另有 9 级列车运行等级。见第 3 节。
6. **宏观图模型是成熟做法，局限有明确记载。** 一手来源写明：结果不保证微观无冲突，最小追踪间隔要由微观模型得出，保守聚合会低估容量，道岔占用冲突在宏观层算不了。见 4.1、4.3。
7. **游戏和模拟器里既有"边上带容量"的先例，也有显式块的先例。** HOI4 的铁路等级 1–5 对应吞吐 15–35，整条路径取最低一段（社区）；War in the East 2 的单轨容量为双轨的 40%（社区）；OSRD 用"资源 + 占用时间窗"检测冲突（一手）；Create 的信号块一次只容一列车，不分方向（一手，源码）。见 4.2。
8. **封锁和限速在容量方法里有四种表达。** 改基础设施参数（轨道数、限速）后重算；把养护算作附加占用时间；从参考时段里扣除预留时间；把资源置为时间窗内不可用。见 5.3、5.4。
9. **断轨的运营处置按严重程度分级。** Network Rail 发现断轨先封锁线路，经合格人员检查才放行；FRA 的补救措施允许目视检查后以不超过 10 mph 通行，每次最多 24 小时。轨道电路会让断轨区段显示占用，但有检不出的情形。见 5.2。
10. **没有找到"所有断轨修复时间相同"的现成写法。** 来源里的持续时间有起止已知、事先未知、规章时限和按资源（HOI4 修复队列）几种。见 5.5。

## 可借鉴的做法

下列做法在来源中已有，只作参考，不构成决定。

1. **并行轨道当作互不连接的服务台。** Huisman 等的区间由若干互不连接的平行轨道组成，同一轨道可同时容纳多列同向车，但不能越行，进入时受最小追踪间隔限制（2.3、4.1）。
2. **用最小间隔表代替信号。** 国内用追踪间隔时间 I 和车站间隔时间 τ，宏观模型用最小追踪间隔，Trafikverket 用"车种时间槽 + 运行时间差"（2.2、2.5、2.6）。
3. **冲突定义为资源占用时间窗重叠。** OSRD 的 `SpacingRequirement`、Pachl 的闭塞时间、北京交通大学学报论文对"分区"的定义是同一类做法（2.2、4.2）。
4. **对向用方向锁。** SUMO 的铁路信号使双向轨道一次只用一个方向；技规要求改变闭塞方向前确认区间空闲；路签闭塞同一区间只发一枚路签（2.4）。
5. **车站用到发线数量限制同时停靠，满则站外排队。** 先到先服务是排队模型的基线；国内规程另有接车次序和列车等级（3.2）。
6. **容量取路径上最弱的一段。** HOI4 按路径上最低的铁路等级取吞吐；Krueger 取最受限的区段；TCRP 取各限制间隔中的最大值（2.1、2.2、4.2）。
7. **封锁和限速先改基础设施参数，再用同一套容量算法重算。** Landex 的做法是只改基础设施布局（轨道数、限速等）和所分析的运行图；Trafikverket 是从 24 h 里扣除预留时间（5.3）。
8. **断轨先封锁，经检查后限速放行。** Network Rail 和 FRA 的规则都是分级处置；轨道电路把断轨区段置为占用（5.2）。

## 1 术语对照

| 概念 | 国内用语 | 英文 | 出处与说明 |
| --- | --- | --- | --- |
| 区间、闭塞分区 | 区间（站间区间、所间区间）、闭塞分区 | station-to-station section, block section | 《铁路技术管理规程》（2006 版，以下称技规）第 243 条（一手） |
| 闭塞制式 | 自动闭塞、自动站间闭塞、半自动闭塞；电话闭塞为代用 | block system | 技规第 244 条（一手） |
| 同向最小间隔 | 追踪间隔时间（I） | headway | 《铁路主要技术政策》第五条、第七条（一手）；定义见[百度百科](https://baike.baidu.com/item/%E8%BF%BD%E8%B8%AA%E5%88%97%E8%BD%A6%E9%97%B4%E9%9A%94%E6%97%B6%E9%97%B4/9069695)（社区） |
| 闭塞时间 | 闭塞时间；进路建立、反应、接近、占用、出清、解锁时间 | blocking time | 《超高速磁悬浮列车闭塞分区设计与优化模型》（北京交通大学学报 2022，一手，磁浮语境，非标准） |
| 车站内最小间隔 | 车站间隔时间：连发 τ连、不同时到发 τ到发、不同时发到 τ发到、会车 τ会、不同时到达 τ不 | station interval times | 符号表见彭超凡、吕红霞（2024，一手） |
| 会让、越行 | 会让站、越行站；会车；越行 | crossing or passing station, crossing, overtaking | 技规第 184 条"办理列车接发、会让"（一手）；国铁路网术语表称只办理接发列车的车站，单线称会让站，双线称越行站（社区，未注明出处） |
| 单线、双线 | 单线、双线；论文和百科另用"复线" | single track, double track | 技规第 183、243 条（一手） |
| 到发线 | 到发线（属站线） | platform track, halting track | 技规第 30 条（一手）；"站台线"没有找到通行译法，本文统一写到发线 |
| 通过能力 | 通过能力、区段通过能力 | line capacity | 技规第 175 条（一手） |
| 运行线 | 列车运行线 | train path | 技规第 175 条用词（一手）；与 train path 对应是推断 |
| 运行图周期 | 运行图周期（T周）、平行运行图、扣除系数 | timetable cycle, parallel timetable, deduction coefficient | 百度百科、课件（社区） |
| 天窗、封锁、慢行 | 天窗（施工天窗、维修天窗）、封锁、慢行 | possession, closure, speed restriction | 《铁路营业线施工安全管理办法》第六、七条，技规第 305–307 条（一手）；"possession"对应"天窗"或"封锁"是推断 |
| 反方向行车 | 反方向行车；双线改按单线行车 | wrong-direction working, single line working | 技规第 183、246 条（一手） |
| 行车许可 | 行车凭证（技规）；移动授权（列控） | movement authority | 技规第 280 条；北京交通大学学报 2022 论文（一手） |

## 2 线路容量

### 2.1 容量没有唯一数值

- （一手，论文转述）Khadem Sameni、Landex 和 Preston 引述 UIC 的结论："Capacity as such does not exist"，"Railway infrastructure capacity depends on the way it is utilised"，并写明 UIC 把容量看作列车数、异质性、平均速度和服务质量（稳定性）之间的权衡。[Developing the UIC 406 Method for Capacity Analysis](https://backend.orbit.dtu.dk/ws/files/5598194/Developing%20the%20UIC%20406%20Method%20for%20Capacity%20Analysis.pdf)
- （一手，学位论文转载 UIC 2004 的图）Landex 把这四项称为"容量平衡"（balance of capacity）：列车数、运行图稳定性、平均速度、运行异质性，四项通常相互关联。[Landex 2008，DTU 博士论文](https://backend.orbit.dtu.dk/ws/portalfiles/portal/4958696/Methods+to+estimate+railway+capacity+and+passenger+delays%28al%29.pdf)
- （一手，法规）欧盟指令 2012/34/EU 第 3 条：`infrastructure capacity` 是 "the potential to schedule train paths requested for an element of infrastructure for a certain period"；`train path` 是 "the infrastructure capacity needed to run a train between two places over a given period"。即容量按各条运行线占用的基础设施来计量。本文读取的是英国 legislation.gov.uk 收录的指令文本。[Article 3](https://www.legislation.gov.uk/eudr/2012/34/article/3/data.xht?view=snippet&wrap=true)
- （一手，会议论文）Krueger（加拿大国家铁路，1999）区分四种量：理论容量（所有列车相同、优先级相同、全天均匀、无干扰的上限，由最受限的区段决定，单线看最长的会让线间距）；实用容量（代表性车流在规定的途中时间上限内能通过的量，可仿真求得，或近似取理论容量的 2/3）；已用容量（实际车流）；可用容量（实用容量减已用容量）。2/3 是该文模型的经验值，不是通用标准。[Parametric Modeling in Rail Capacity Planning](https://www.informs-sim.org/wsc99papers/173.PDF)

### 2.2 同向间隔：闭塞时间与追踪间隔

- （一手）Pachl 把闭塞时间（blocking time，德语 Sperrzeit）定义为某段线路（通常是一个闭塞分区）专属分配给一列车、对其他列车封锁的时间区间（原文：the time interval in which a section of track (usually a block section) is allocated exclusively to a train and therefore blocked to other trains）。它通常比列车实际占用该分区的时间长得多。无计划停车的列车在一个分区的闭塞时间由六段组成：开放信号的控制时间、司机确认信号的时间、接近时间、信号机之间的运行时间、出清时间（必要时含保护区段）、解锁时间。Pachl 说明这一模型也适用于司机室信号和移动闭塞。[Railway Signalling Principles](http://joernpachl.de/rsp.pdf)
- （一手）把一列车经过各分区的闭塞时间画在时间—距离图上，得到闭塞时间阶梯。单个分区的闭塞时间给出该分区的信号间隔；线路间隔要看整条阶梯：两列同向列车的阶梯至少在一个分区（关键闭塞分区）刚好相接，没有余量。[同上]
- （一手）固定闭塞下，两列车的间距等于后车的制动距离加闭塞分区长度再加安全余量。移动闭塞只去掉"分区内运行时间"这一项，闭塞时间的其他各项仍在。Pachl 认为多数线路上其余各项之和远大于被去掉的部分，所以移动闭塞对容量的提升常被高估。[同上]
- （一手）国内设计下限：高速铁路列车追踪间隔时间最小按 3 min，双线铁路旅客列车追踪间隔时间最小按 5 min。[《铁路主要技术政策》](https://www.nra.gov.cn/xxgk/gkml/ztjg/gfzd/bmgz/202206/t20220624_327442.shtml)
- （社区）三显示自动闭塞区段按前后两车相隔三个闭塞分区计算：I追 = 0.06 × (l列 + 三个闭塞分区长度之和) / V运，单位为分钟。长度取 m、速度取 km/h 时系数 0.06 把结果换成分钟，这一单位解读是推断。[百度百科](https://baike.baidu.com/item/%E8%BF%BD%E8%B8%AA%E5%88%97%E8%BD%A6%E9%97%B4%E9%9A%94%E6%97%B6%E9%97%B4/9069695)
- （一手）国内期刊把"分区"定义为"同一时刻仅允许一列列车占用、具有独占性的区段，如区间的一个闭塞分区、车站的一个道岔区段或一个停车股道"，并借用 UIC 406 压缩运行图的思路：平移各列车的闭塞时间阶梯，直到两两达到无冲突的最小间隔。[超高速磁悬浮列车闭塞分区设计与优化模型](https://jdxb.bjtu.edu.cn/CN/article/downloadArticleFile.do?attachType=PDF&id=3890)
- （一手）城市轨道交通：非干扰间隔 h_ni = 列车控制间隔 t_cs + 控制站平均停站时间 t_d,crit + 运营余量 t_om。控制间隔取各限制间隔（含非干扰间隔、路权类型、道岔联络点、供电）中的最大值，线路容量 T = 3600 / 控制间隔（列/h）。运营余量推荐 15、20 或 25 s，客流宽松时可取 35 s，最小可用 10 s。[TCRP Report 165 第 8 章](https://onlinepubs.trb.org/onlinepubs/tcrp/tcrp_rpt_165ch-08.pdf)

### 2.3 同向跟车与速度差

- （一手）Pachl："On lines with mixed traffic, the minimum line headway depends significantly on the speed differences between trains." 在速度基本相同的城市轨道线路上，关键分区通常是含站台停站时间的车站分区。[Railway Signalling Principles](http://joernpachl.de/rsp.pdf)
- （一手）Landex：异质车流的关键闭塞分区通常在快车追上慢车的位置。时刻表中只要有列车追上前车，就是异质的；异质时刻表单位时间内能开的列车比同速同停站的同质时刻表少。[Landex 2008](https://backend.orbit.dtu.dk/ws/portalfiles/portal/4958696/Methods+to+estimate+railway+capacity+and+passenger+delays%28al%29.pdf)
- （一手）Khadem Sameni 等的算例：哥本哈根 Holte—Hellerup 段有 B（站站停）和 E（跳过 5 站）两条线路，各每 10 分钟一班，6:30–18:30 的容量占用率为 75.5%。再加开列车时，只加 B 可加 62 列，只加 E 可加 45 列，B 与 E 交替加只能加 43 列。作者的结论是 UIC 406 的结果对加入哪类列车高度敏感，交替加入最少是因为异质性。[同 2.1 第一条](https://backend.orbit.dtu.dk/ws/files/5598194/Developing%20the%20UIC%20406%20Method%20for%20Capacity%20Analysis.pdf)
- （一手）Huisman 等（2002）：同一轨道上越行在物理上不可能，快车紧跟慢车时必须减速，直到能在车站越过。[EJOR 142](https://ris.utwente.nl/ws/files/6761182/Huisman92solvable.pdf)
- （一手）Trafikverket 的"与运行图无关"的做法（经 Weik 等转述）：每条运行线的占用 = 随车种而定的固定时间槽 + 平均最小间隔。一列车的间隔由它的运行时间与后续各列车运行时间之差求平均得出（推断：运行时间差越大，间隔越大）。区段内有交叉进路或越行时，每列车另加 4 min（Weik 等写作 crossings or overtakings，瑞典语报告写作"交叉进路"）。[Weik 等 2020](https://www.diva-portal.org/smash/get/diva2:1452676/FULLTEXT02)
- （一手）多线区段：Landex 提出，线路段超过两条轨道时，要注意线路段两端的列车顺序，否则可能多出越行；轨道越多，各轨的容量占用越可能不均，建议在差别大时允许列车换轨（第 3.7 节）。以每小时列车数计，断面容量 = 最大交通强度 × 轨道数（公式 4.1，引自 Kaas 1998）。[Landex 2008]
- （一手）两个宏观优化模型对平行轨道的写法：Louwerse 和 Huisman 事先指定每列车使用哪些轨道，并假设改点后的用轨与原运行图相同；Cacchiani 等的意大利 RFI 枢纽模型规定越行只允许在车站或借平行轨道进行（原文：overtaking between trains is only allowed at stations or by using parallel tracks），理想运行图里还给出偏好用轨。[Louwerse & Huisman](https://repub.eur.nl/pub/37246/EI2012-23.pdf)；[Cacchiani 等预印本](https://optimization-online.org/wp-content/uploads/2013/11/4136.pdf)

### 2.4 对向占用与双向使用

- （一手）Pachl：只有一股轨道的线路叫单线，两条平行轨道且各有规定正常运行方向的叫双线。单线区间的保护方式之一是路签闭塞：每个区间一枚实物路签（staff、key 或 tablet），只有持有路签的人员可以放行列车进入该区间，改变允许方向时由司机把路签带到对向车站。电气路签系统给每个区间分配固定数量的路签，同一时刻只能放出一枚，所以区间中间不能再设闭塞分区。[Railway Signalling Principles](http://joernpachl.de/rsp.pdf)
- （一手）Pachl：没有双向信号的双线，逆向行车要凭专门指示、由人员负责；装有双向信号的，可以开放主体信号办理逆向行车。临时单线运行（一条线封闭）时，剩下那条线的方向几乎每列车都要变，所以区间中间的闭塞信号对能力没有作用。[同上]
- （一手）技规第 183 条：双线区间列车按左侧单方向运行，仅限于整理列车运行时方可反方向运行。第 246 条：无双向闭塞设备的双线区间反方向发车或改按单线行车时，改用电话闭塞法。第 247 条：设有双向闭塞设备的自动闭塞区间改变闭塞方向，车站值班员必须先确认区间空闲，并依据列车调度员命令操作。[《铁路技术管理规程》](https://www.nra.gov.cn/xxgk/gkml/ztjg/gfzd/bmgz/202206/t20220624_327439.shtml)
- （一手）SUMO（德国航空航天中心的开源交通模拟器）官方文档：双向铁轨用两条方向相反的边表示。在双向轨道两端设铁路信号后，信号会把它限制为一次只用一个方向，并自动保证：一个信号块同时只进一列车；不同支线的车不进入同一段；对向车不同时进入双向区段（防迎面碰撞）；防止双向区段死锁。寻路时可以给每条轨道设优先方向，以减少对向冲突。[Railways - SUMO Documentation](https://sumo.dlr.de/docs/Simulation/Railways.html)
- （一手）Huisman 等的区间模型假设每条轨道只单向使用，并因此排除单线区间（原文称荷兰极少）；Cacchiani 等模型的轨道也是单向的（mono-directional）。[Huisman 等](https://ris.utwente.nl/ws/files/6761182/Huisman92solvable.pdf)；[Cacchiani 等预印本](https://optimization-online.org/wp-content/uploads/2013/11/4136.pdf)
- （一手）Schlechte 的宏观轨道除同向间隔外，还给"对向"的轨道另一张间隔表：一列车发车后，对向列车要等它通过整条轨道各分区的运行时间之和，再加末端分区的两项时间，另加缓冲，才能发车；他指出这时相关的闭塞分区总是最后一个。[Schlechte](https://www.zib.de/userpage/schlechte/phdthesis.pdf)
- （一手）Lidén 与 Joborn 的线路模型：同向列车至少相隔一个最小追踪间隔 λ；单线上对向列车只能在会让线相遇（原文：opposing trains can only meet at meet/pass loops），会让线间距 l 带来停车和运行时间附加。[Lidén & Joborn 2016](https://www.diva-portal.org/smash/get/diva2:916954/FULLTEXT03)
- （推断）以上两个宏观模型的每条轨道固定单向；"轨道双向"在运营规则里对应单线（一次一个方向）和双向信号下的逆向行车（改变方向要先锁定），两处都是"一次一个方向"。依据：Pachl、技规第 247 条、SUMO 文档。

### 2.5 越行与会让

- （一手）Hansen 与 Pachl：UIC 把路网看作由线路（lines）连接的节点（nodes）；较复杂的节点是可以越行、交会或换向的车站区域。原文："Different from block sections of the open line, station tracks may be used for overtaking, crossing, and direction reversal of trains."[Railway Timetabling & Operations 第 2 版试读样章](https://www.trackomedia.com/media/e3/40/15/1742414199/978-3-96245-089-2_Railway-Timetabling_Leseprobe.pdf)
- （一手）技规第 184 条：车站"办理列车接发、会让和客货运业务"。（社区）国铁路网术语表：只办理接发列车的车站，单线区段称会让站，双线区段称越行站，表中未注明出处。[铁路名词术语](http://www.chnrailway.com/index.php/index/shows?catid=23&id=1731)
- （一手）国内运行图编制模型把冲突写成最小间隔约束：双线自动闭塞区段同向列车要满足追踪间隔时间 I；单线区段同向连发要满足连发间隔时间 τ连，对向列车另须满足不同时到达间隔时间 τ不和会车间隔时间 τ会；任何时刻车站被占用的到发线数不得超过该站到发线数。[NFD 2010 会议论文](https://www.scirp.org/pdf/13-1.62.pdf)；符号说明见[彭超凡、吕红霞 2024](https://pdf.hanspub.org/ojtt2024136_52760850.pdf)。
- （社区）车站间隔时间的两个定义：连发间隔时间 τ连是"自列车到达或通过前方站时起，至由车站向该区间发出另一同方向列车时止的最小间隔时间"；会车间隔时间 τ会是"自列车通过或到达车站时起，至该站向原区间发出另一对向列车时止的最小间隔时间"。取自百度百科词条的搜索摘要，词条页本文没能打开。
- （一手）Trafikverket 的单线占用公式（KTH 受托报告，瑞典语）：每列车的占用 = 运行时间 + 会车时间（3–5 min，随速度）+ 不同时进站附加（2 min，按 50% 的列车计）+ 无远程闭塞附加（1 min）。双线的间隔取 3–5 min（随速度），交叉进路另加每列车 4 min。[KTH 报告](https://trafikverket.diva-portal.org/smash/get/diva2:1747208/FULLTEXT01.pdf)
- （社区）国内课件定义扣除系数："因铺画一对或一列旅客列车、快运货物列车或摘挂列车，须从平行运行图上扣除的货物列车对数或列数"。[课件，链接带会话号，可能失效](http://cc.bjtu.edu.cn:81/meol/analytics/resPdfShow.do;jsessionid=111511DC142EEBAD767DC56A487175BC?resId=110868&lid=18315)

### 2.6 计算方法

| 方法 | 容量的表示 | 主要输入 | 来源 |
| --- | --- | --- | --- |
| UIC 406 压缩法 | 占用率 K = (tA + tB + tC + tD) / tU。tA 为压缩后的基础设施占用时间，tB 缓冲时间，tC 单线补充时间，tD 养护补充时间，tU 所选时段 | 运行图、各分区闭塞时间 | Landex 2008 第 2 章转述 UIC 2004（一手） |
| 国内运行图周期法 | 平行运行图 N = 1440 / T周；电力牵引区段 N = (1440 − t网) / T周；规定施工封锁空隙时 N = (1440 − t封) / T周；非平行运行图用扣除系数折算 | 区间运行时分、起停车附加时分、车站间隔时间或追踪间隔时间 | [百度百科"运行图周期"](https://baike.baidu.com/item/%E8%BF%90%E8%A1%8C%E5%9B%BE%E5%91%A8%E6%9C%9F/10240449)（社区）；技规第 175 条要求按区段通过能力确定列车对数（一手） |
| 城轨公式 | T = 3600 / 控制间隔 | 控制间隔 | TCRP 165（一手） |
| 与运行图无关的占用 | Σ(运行线时间槽 + 平均最小间隔) + 会车补充；日占用率的分母为 24 h 减预留养护时间 | 车种、运行时间 | Trafikverket（一手） |
| 排队模型 | 平均等待时间，不需要运行图 | 到达强度、占用时间分布 | Huisman 等 2002（一手） |

- （社区）运行图周期是"一组列车占用区间的总时间"。非自动闭塞区段由区间运行时分、起停车附加时分和列车在车站的间隔时间组成，自动闭塞区段为追踪间隔时间。周期最大的区间是限制区间。（百度百科同上；课件把周期按单线成对非追踪、单线不成对非追踪、双线追踪、单线成对追踪分类。）
- （社区）课件定义 t固 为"进行线路养修等作业须预留的固定占用区间时间，以及必要的列车慢行和其他附加时分"。
- （一手）行业标准 TB/T 2110-1990《单线铁路区间通过能力计算方法》列于国家铁路局标准目录，正文没有读到。[标准目录](https://www.nra.gov.cn/xxgk/gkml/ztjg/bzgf/bzgg/201410/t20141013_319389.shtml)
- （一手，经论文转述）UIC 2004 的建议占用率上限见下表（Landex 表 5.1）。Landex 的解释：高峰值更高，是因为短时段内可接受的恢复晚点余地更小，且高峰客流更大；混跑线更低，是因为快车追上慢车带来连带晚点的风险。同页引用 Pachl（2008）：高峰 4 小时不应明显超过 80%，24 小时不应超过 50%。UIC 与 Pachl 取的时段长度不同，数值不能直接比较。

| 线路类型 | 高峰小时 | 全天 | 说明 |
| --- | --- | --- | --- |
| 郊区客运专线 | 85% | 70% | 可取消部分列车，所以允许更高 |
| 高速专线 | 75% | 60% | |
| 混跑线 | 75% | 60% | 每小时少于 5 列且异质性强时可更高 |

- （一手，经论文转述）UIC 406 第 2 版（2013）把压缩法扩展到车站：车站拆成站台区和道岔区分别压缩，用进路排斥矩阵给出各进路对的最小间隔，并分别给出建议上限。Weik 等和 Landex 等认为应按整个车站压缩。第 2 版的上限数值没有读到。[Weik 等 2020](https://www.diva-portal.org/smash/get/diva2:1452676/FULLTEXT02)
- （推断）表中各方法的基本量都是"某资源被某列车占用多长时间"，资源可以是闭塞分区、车站股道或整个区间。依据：上表各公式的输入列。

## 3 车站与站台容量

### 3.1 站内占用

- （一手）Hansen 与 Pachl：进站信号机以内、由联锁信号机限定的线路段叫 station track，对列车间隔的作用等同闭塞分区，但可用于越行、交会和换向。站台与 station track 不一定一一对应。[试读样章](https://www.trackomedia.com/media/e3/40/15/1742414199/978-3-96245-089-2_Railway-Timetabling_Leseprobe.pdf)
- （一手）Huisman 等对一次进站的描述：列车进站需要同时得到一条到发线和通往它的进路；到站后进路释放，只占到发线，直到停站结束；之后若出站进路被占，列车继续等在到发线上。[Huisman 等 2002](https://ris.utwente.nl/ws/files/6761182/Huisman92solvable.pdf)
- （一手）伦敦的 RfL(I) CCOS Timetable Planning Rules 2026（线路使用 CBTC）：站台再占用时间是同一站台、同方向上前车离站到后车到站的间隔，默认取当前线路间隔但不超过它，与停站时间分开计量。它由三段相加后向上取整到半分钟：前车从计时点出发到车尾出清相关区段；CBTC 重置进路并向后车发出移动授权；后车起动到到达计时点。标准值：同方向 60 s，反方向 120 s（Plumstead 折返线 180 s）。停站时间是车轮停稳到车轮起动的时间。这些值适用于该线的 CBTC，不是通用值。[TfL 规则](https://content.tfl.gov.uk/rfli-ccos-tpr-2026.pdf)
- （一手，经论文转述）UIC 406 第 2 版的站台区和道岔区拆分、进路排斥矩阵见 2.6。
- （社区）国内课件把车站通过能力分为咽喉通过能力和到发线通过能力两部分（课件同 2.5 最后一条）。

### 3.2 到发线占满时的等待与次序

- （一手）Huisman 等：模型只建模到发线，并假设每列车可用任一条；车站是先到先服务的多服务台排队系统，每个服务台是一条到发线，服务时间是到发线占用时间。到站时到发线全被占用的列车排在站前虚拟队列的队尾，等到发线空出后进站。作者列出的局限：虚拟队列容量无限，会低估等待时间；只考虑先到先服务，不含优先级规则，这会影响各车种的等待时间，对总体影响不大。[Huisman 等 2002](https://ris.utwente.nl/ws/files/6761182/Huisman92solvable.pdf)
- （一手）技规第 279 条："相对方向不能同时接车时，应先接后面有续行列车的列车、停车后起动困难的列车或不适于在站外停车的列车。遇两列车不能同时接发时，原则上应先接后发。"第 281 条：旅客列车应接入固定线路；特快旅客列车应在正线通过；进路临时变更来不及预告司机时，"应使列车在站外停车后，开放信号机，再接入站内"。第 182 条列出 9 级列车运行等级，特快旅客列车最高，货物列车和路用列车靠后，"开往事故现场救援、抢修、抢救的列车，应优先办理"。技规里没有"先到先接"的条文。[《铁路技术管理规程》](https://www.nra.gov.cn/xxgk/gkml/ztjg/gfzd/bmgz/202206/t20220624_327439.shtml)
- （一手）Factorio 的站台"列车上限"（train limit）：车站记录"在站内或在途的列车数"（称预约）；列车选下一个目的地时检查同名车站的上限，已满就跳过，全部已满则等待，并显示 "Destination full"。上限只在选目的地时检查，已有预约的列车不受影响，所以不是严格的硬限制；开发者说明原因是强制重新寻路可能让列车停在轨道中间。[Friday Facts #361](https://factorio.com/blog/post/fff-361)

### 3.3 宏观优化模型里的车站

- （一手）意大利 RFI 枢纽的运行图模型：每个车站有容量 c_i，表示可同时停靠的列车数（原文：the number of trains that can simultaneously stop at the station）；任一时刻的停靠列车数不得超过 c_i。同一车站同一轨道上相邻两次到达（出发）要满足最小间隔 h+（h−）。道岔联络点有占用时间 o_j 和不相容进路对。两地之间可以有单条或平行的轨道。[Cacchiani 等预印本](https://optimization-online.org/wp-content/uploads/2013/11/4136.pdf)（作者署名未在读取页面核对）

### 3.4 终点站折返

- （一手）TCRP 165 第 8 章：终点站折返的最坏情形是到站列车被扣在渡线接近信号机前，等发车列车离开；它起动穿过渡线并完整进站后，下一列出站车才能走；出站车出清渡线、联锁复位后，才能放下一列进站。间隔减去这些作业时间（两个泊位时再乘 2）就是可用于折返的时间，折返时间要容纳乘客上下车、司机换端、检查车辆和制动。设计准则："Correctly designed and operated turnbacks should not be a constraint on capacity."[TCRP 165 第 8 章](https://onlinepubs.trb.org/onlinepubs/tcrp/tcrp_rpt_165ch-08.pdf)
- （一手）FRA 对纽约 Penn Station 的研究报告（2026-07）：停站时间是车站容量的主要限制因素；列车每多占用站台一分钟，该站台就少一分钟服务下一列车；折返作业在受限条件下使列车占用站台和关键联锁容量多出 15–25 分钟以上。[FRA 报告](https://railroads.dot.gov/sites/fra.dot.gov/files/2026-07/2026.07.13_Penn%20Station%20SOS_Phase%20I%20Report_FINAL.pdf)
- 机车换端和推挽对站线占用的影响，没有找到一手来源。

## 4 抽象图模型与游戏、模拟器

### 4.1 研究里的宏观模型

- （一手）Bešinović 学位论文的宏观网络：节点是时刻表点（车站、道岔联络点等），边是区间，网络图上标注站内和区间的轨道数。荷兰 Utrecht—Den Bosch—Eindhoven 走廊约 80 km，28 个微观时刻表点聚合成 15 个宏观点和 15 条弧，多数区间为双线，Boxtel—Eindhoven 为四线。宏观优化模型从微观模块得到的输入有：聚合后的基础设施（含弧的容量和宏观时刻表点）、列车线及其进路、列车线在时刻表点相遇时的最小追踪间隔、名义和最大的运行时间与停站时间、衔接要求；线路间隔按"走廊"计算，走廊可以是一条弧或相邻的几条弧。[Bešinović 博士论文](https://pure.tudelft.nl/ws/files/19195941/NBesinovic_PhD_thesis.pdf)
- （一手）Schlechte 博士论文：微观模型对数学优化来说太大、太细，所以 "most optimization approaches consider simplified, so called macroscopic, models"。他的宏观表示 "is based on minimal headway times for entering tracks of train routes"，并把微观模型的大部分内容转为 "aggregated resource consumption in space and time"。两个宏观站之间可以有多条轨道，例如聚合后的两个宏观站之间有两条都可供同向列车使用的轨道。他要求同向间隔表"顺序安全"，即轨道上不发生超越（no passing on tracks）。[Railway Track Allocation](https://www.zib.de/userpage/schlechte/phdthesis.pdf)
- （一手）Huisman 等把区间写成互不连接的平行轨道，每条只单向使用。同一条轨道可同时容纳多列车，有两项容量限制：不能越行；信号系统把轨道分成闭塞分区，一次只容一列车，于是相邻两车至少相隔"最小追踪间隔"。模型只在轨道的入口和出口两端考虑追踪间隔：入口和出口各一个先到先服务的单服务台，服务时间等于最小追踪间隔；中间部分用串联的 M/M/1 队列表示自由运行时间。[Huisman 等 2002](https://ris.utwente.nl/ws/files/6761182/Huisman92solvable.pdf)
- （一手）RailTopoModel（UIC 的铁路拓扑数据模型）预设四层：nano、micro、meso、macro。中观层的节点是运营点，边是连接它们的轨道（SectionOfTracks）；宏观层的节点是边界和主要运营点，边是线路区段。聚合会丢失细节，高层数值按规则计算，如取最小值或求和。[Levels of detail](https://wiki.railtopomodel.org/wiki/Levels_of_detail)
- （推断）"两个聚居地之间带轨道数的边"介于 RailTopoModel 的中观层（逐条轨道）和宏观层（线路区段）之间；该页面没有规定宏观层怎样记录轨道条数。依据：上一条的层级描述。

### 4.2 游戏与模拟器

| 对象 | 表示 | 容量的单位与规则 | 冲突与等待 | 类型 |
| --- | --- | --- | --- | --- |
| Hearts of Iron IV | 省份图，铁路等级 1–5 | 枢纽到首都的路径吞吐：1 级 15，每升一级加 5，5 级 35；整条路径取最低一段的等级。供应网还需要与供应量相关的火车数。每格铁路造价 300，每已有一级再加 130，从零建到 5 级每省 2800 | 不模拟具体列车 | 社区，[Logistics](https://hoi4.paradoxwikis.com/Logistics) |
| Victoria 3 | 州的数值 | 基础设施供给与需求：供给不小于需求时市场可达性 100%，需求超出时按未满足的比例下降（例：供给 45、需求 90，可达性 50%）；铁路是供给的主要来源 | 无列车 | 一手，[开发日志 #10](https://web.archive.org/web/2022id_/https://forum.paradoxplaza.com/forum/threads/victoria-3-dev-diary-10-infrastructure.1485696/)（Wayback 存档） |
| War in the East 2 | 六角格上的铁路 | 每格记录吨位：双轨 30,000 吨，单轨 12,000 吨（单轨为双轨的 40%）；使用量按阶梯扣战略移动点，双轨每 5,000 吨一档、单轨每 2,000 吨一档，到 +6 封顶；超过上限仍可通过，但按最高档计费 | 无列车 | 社区（手册转载），[规则](https://dornshuld.com/rules/wite2/22-0.html) |
| OSRD（SNCF 的开源铁路设计器） | 区段（zone）图 | 没有容量数；资源需求带 `[begin_time, end_time]`。间隔需求是排他的：同一资源的时间窗重叠即冲突。进路与道岔需求可共享：配置相同则兼容 | 冲突检测"不得漏报"；生成需求仍依赖信号模拟 | 一手，[冲突检测设计文档](https://osrd.fr/en/docs/reference/design-docs/conflict-detection) |
| SUMO | 边图，双向轨道为两条反向边 | 信号块同时一列车；双向轨道一次一个方向 | 铁路信号自动防迎面碰撞和死锁。文档列出的未建模项：道岔转换延时、保护区段、远距离信号、调车限制 | 一手，[文档](https://sumo.dlr.de/docs/Simulation/Railways.html) |
| Create（Minecraft 模组） | 轨道图（节点 + 边）；列车位置 = 边 + 沿边距离 | 信号块是 `SignalEdgeGroup`，记录块内列车集合和一个预约，没有方向字段，块内已有别的列车即算占用（见表后说明）；`TrackEdge` 没有容量或轨道数字段 | 每个 tick 先清空各块的列车和预约再重建，等待最久的列车先处理；在读取的 10 个源码文件中搜索 deadlock、gridlock，0 处命中 | 一手，源码（提交 `588124239141029c5321afd38d20c2eaf872b32b`）：`content/trains/signal/SignalEdgeGroup.java`、`entity/TravellingPoint.java`、`graph/TrackEdge.java`、`GlobalRailwayManager.java` |
| OpenTTD | 格子 + 轨道位 | 路径预约的单位是"格 + 一条轨道"（`TryReserveRailTrack`）；传统块信号把信号之间所有可达的格算成一个块 | wiki 称快车追上慢车时必须在下一块前完全停下再起步；游戏不会让慢车让到侧线，除非用时刻表和路径点手动安排。路径信号更能避免路口死锁 | 一手（源码）加社区（[wiki](https://wiki.openttd.org/en/Manual/Signals)）；源码读取自 master 分支 `src/pbs.cpp` |
| Factorio | 轨道实体；段（segment）和块（block） | 块是列车预约的基本单位，由无信号分隔的相连或相碰的段组成；站台上限是预约计数（见 3.2） | 寻路里块被占用只是附加代价，不是硬约束 | 一手，[FFF-331](https://factorio.com/blog/post/fff-331) |

- （一手）Create 判断块是否被占用的代码是 `SignalEdgeGroup.isThisOccupiedUnless(train)`：`reserved != null || trains.size() > 1 || !trains.contains(train) && !trains.isEmpty()`，其中没有方向参与。（推断）所以对向列车和同向列车一样被拦在块外，块一次只容一列车。
- （推断）若把一条边当作一个闭塞分区，边上某条轨道的占用时间窗就相当于 Pachl 定义的闭塞时间：从最晚必须给出进入许可起，到可以给下一列车同样许可止。依据：Pachl 的定义；OSRD 的 `SpacingRequirement` 同样是 `[begin_time, end_time]` 的排他时间窗。

### 4.3 用容量或时间窗代替信号与几何的取舍

- （一手）宏观模型不保证微观无冲突。Bešinović："most of the current models assume a macroscopic representation of infrastructure and do not include microscopic details ... generated solutions are not always feasible, i.e., conflict-free, and thus would directly induce certain delays when operated in practice." 他引用的做法是先用微观数据聚合出运行时间和最小追踪间隔，再在微观层模拟检查可行性。[Bešinović 博士论文](https://pure.tudelft.nl/ws/files/19195941/NBesinovic_PhD_thesis.pdf)
- （一手）保守取值会低估容量。Schlechte："Because of the conservative rounding macroscopic models tend to underestimate the capacity."，精度可通过时间离散化调节。他还指出，只取"列车在起点和终点都停车"这一最坏情形，会低估容量并错误识别瓶颈，所以他的运行时间随车种和事件而定。[Schlechte](https://www.zib.de/userpage/schlechte/phdthesis.pdf)
- （一手）RailTopoModel："switch occupation conflicts cannot be computed at the level describing the European high speed network. This will be done at the most accurate level." [Levels of detail](https://wiki.railtopomodel.org/wiki/Levels_of_detail)
- （一手）信号制式本身会改变容量。Goverde 等在荷兰 Utrecht—Den Bosch 线上比较：按 UIC 压缩法算，计划状态下的容量占用从旧制式 NS'54/ATB 到短闭塞的 ETCS Level 2 有改善；晚点时旧制式的容量占用变差，ETCS 因低速时制动距离缩短而有明显收益。该文还用先到先服务和分支定界两种调度规则做了对比。[Goverde 等 2013 摘要](https://resolver.tudelft.nl/uuid:41bd7541-a8f6-4e99-bdf2-4e9b5f633da6)
- （一手）Landex 指出，针对单线运行的运行图模型和优化模型以列车数最大为目标，"insufficiently consider the impacts of the constraints on the capacity consumption due to the track layout and signalling system"，可能低估连带晚点。[Landex 2008 第 6 章](https://backend.orbit.dtu.dk/ws/portalfiles/portal/4958696/Methods+to+estimate+railway+capacity+and+passenger+delays%28al%29.pdf)
- （一手）Huisman 等承认的简化：虚拟队列容量无限会低估等待时间；只含先到先服务；指数分布假设会高估等待时间；因荷兰最繁忙路段负荷一般低于 30%，误差小。[Huisman 等 2002](https://ris.utwente.nl/ws/files/6761182/Huisman92solvable.pdf)
- （一手）OSRD 把"不得漏报"列为设计约束："if no conflicts are detected, a multi-train simulation of the same timetable must not yield any slowdowns"。[OSRD](https://osrd.fr/en/docs/reference/design-docs/conflict-detection)
- （一手）死锁：SUMO 把防止双向区段死锁列为铁路信号的自动功能；Create 的源码里没有搜到死锁处理（搜索结果为 0 命中，不等于不存在）。（社区）OpenTTD wiki 称路径信号更能避免路口死锁。

## 5 断轨与施工封锁

### 5.1 运营规则中的封锁、天窗与限速

- （一手）《铁路营业线施工安全管理办法》第七条："天窗是指列车运行图中不铺画列车运行线或调整、抽减列车运行线为施工和维修作业预留的时间，按用途分为施工天窗和维修天窗。"编制运行图应当明确各条线路天窗的时间和位置。第三十四条：维修作业原则上纳入天窗，不得利用列车间隔时间作业。[办法全文](https://www.nra.gov.cn/xxgk/gkml/ztjg/gfzd/gfxw/bumen/zhs/202110/t20211012_327953.shtml)
- （一手）技规第 305 条：影响行车的施工、维修作业不得利用列车间隔进行（特别规定的慢行施工除外），都必须纳入天窗。第 306 条：封锁线路、慢行施工等必须纳入月度施工方案，施工单位前一个月提出计划。第 307 条：封锁区间施工须登记并经调度命令封锁或开通；如不能按时开通或不能按规定的开通速度运行，要提前通知，要求列车调度员延长时间或限速运行。[技规](https://www.nra.gov.cn/xxgk/gkml/ztjg/gfzd/bmgz/202206/t20220624_327439.shtml)
- （社区）国铁路网术语表：区间封锁是"由于施工或区间发生事故等原因，根据调度命令，除指定列车外，禁止其他列车进入区间"。
- （一手）Network Rail 词汇页：possession 是线路因维修需要而不能行车时，由运营方移交给工程方，期间用专门防护措施阻止未授权列车进入；ESR（紧急限速）是未列入周运行通告、在指定地点之间临时降低列车速度的非计划限制；pilot 是唯一有权批准列车通过反向行车或封锁区域的人员。[Jargon Buster](https://safety.networkrail.co.uk/jargon-buster)
- （一手）Lidén 与 Joborn：瑞典交通局的"维修窗口"是编制运行图之前预先划定的 2–6 小时无车时段，而 possession 是对具体作业的实际预约（原文：maintenance windows are predetermined train free slots in the timetable, while possessions are the actual reservations for specific work tasks）。[论文](https://www.diva-portal.org/smash/get/diva2:916954/FULLTEXT03)

### 5.2 断轨的检测与处置

- （一手）FRA 研究报告：钢轨折断且两段分离到不再接触时，轨道电路使该区段显示占用，保护列车；轨道电路只知道断裂在该区段内，不知道具体位置。检不出的情形有两种：断口没有使电路断开；断口落在跨接线之间的死区。电路自身故障（导线断开、断电）时也会显示停止，即使钢轨没断。修复并恢复回路后，区段自动恢复显示空闲。[Evaluation of Fiber Optic Broken Rail Detection Systems](https://railroads.dot.gov/sites/fra.dot.gov/files/fra_net/18261/Evaluation%20of%20FO%20Broken%20Rail.pdf)
- （一手）Network Rail 标准 NR/L2/TRK/001（2012 版）："On discovery of a broken rail, block the line"。断裂在可动的道岔或辙叉钢轨上，或断点距严重滚动接触疲劳 2 m 以内时，线路须保持封锁。是否解除封锁由合格人员检查评估；其中 rail defect nominee 只被允许让列车以不超过 5 mph 通过断轨。[标准](https://www.thepwi.org/wp-content/uploads/2021/02/nr_l2_trk_001_00_inc_disclaimer.pdf)
- （一手）FRA 对 49 CFR 213.113 缺陷钢轨补救措施的说明：A，指定人员目视监护每一次通过；A2，目视检查后可不再连续监护，最高 10 mph，最多 24 小时，钢轨未修复则再开始下一个 24 小时周期；B，限速由指定人员授权，不超过 30 mph 或该轨道等级允许的速度。[FRA 钢轨缺陷手册](https://railroads.dot.gov/sites/fra.dot.gov/files/fra_net/15669/Final%20FRA%20Rail%20Manual%20July%2029%202015_031716.pdf)

### 5.3 容量方法中的表达

- （一手）Landex 第 6 章用 UIC 406 评估封锁和应急运行，并写明与常规容量分析相比，唯一的差别是所分析的基础设施布局（轨道数、限速等）和运行图（运行时分、用轨、列车数）。施工、列车故障、信号或供电故障使双线只能封一条线、另一条线双向使用时，站内渡线的位置决定能否快速切换；哥本哈根郊区线（固定 10 min 间隔）的做法是每最多 5 分钟运行时间设一处渡线。双向使用时反方向通常闭塞分区更少、限速更低，容量大幅下降；渡线之间行车时间越长、最小追踪间隔越大，容量越低。应对办法有两种：取消部分列车直到占用率回到 UIC 建议上限以内，或先开一个方向的列车成组再开反方向（捆绑运行）。[Landex 2008](https://backend.orbit.dtu.dk/ws/portalfiles/portal/4958696/Methods+to+estimate+railway+capacity+and+passenger+delays%28al%29.pdf)
- （一手）Landex 把故障按持续时间分三类：临时故障（立即排除）；长期降低容量（须改道，或双线改单线）；全线封闭（全部轨道阻断，故障排除前无法恢复行车）。长期受限时，把受限后的系统当作新的基础设施重做容量分析；临时故障则在原系统中观察延误如何传到后续列车。[同上]
- （一手）Landex 第 6.2 节：线路局部限速（如施工或轨道故障）会使闭塞占用时间变长，甚至得到无法运行的运行图。例图中某闭塞分区限速后容量占用率超过 100%，不减少列车就无法运行，延误持续增长。[同上]
- （一手）UIC 2004 的占用公式带单线补充 tC 和养护补充 tD 两项（见 2.6）；Trafikverket 的日占用率分母是 24 h 减 T_kval，最繁忙 2 h 的分母是 2 h 减 T_ban，两者都是"为预防性养护等预留的时间"，默认值分别为 6 h 和 0 h。[KTH 报告](https://trafikverket.diva-portal.org/smash/get/diva2:1747208/FULLTEXT01.pdf)
- （社区）国内公式 N = (1440 − t封) / T周 和 N = (1440 − t网) / T周 从一昼夜 1440 分钟里扣除施工封锁时间和接触网检修封锁时间（见 2.6）。

### 5.4 调度与优化模型中的表达

- （一手）Louwerse 与 Huisman：把"大型干扰"定义为一个区段的全部或一半线路被阻断至少 1 小时（荷兰平均每天约 3 次）。完全阻断：区段内所有线路被阻断，无法行车，阻断两侧的列车要在阻断前的车站折返，并接替反方向的服务。部分阻断：只有部分线路被阻断，仍可限量行车。部分阻断的模型决定双线中只剩一条线可用时取消哪些列车、不取消的列车晚点多少分钟。作者指出，干扰的持续时间和对基础设施的影响在发生时很难预测。[工作论文](https://repub.eur.nl/pub/37246/EI2012-23.pdf)；正式发表于 [EJOR 235(3)](https://www.sciencedirect.com/science/article/abs/pii/S0377221713010047)。
- （一手）Zhan 等（2015）：完全阻断时区段内所有线路在一段时间内不可用；文中假设被阻列车不折返，在站内等到阻断结束，并说明这是中国等国家的调度策略，而荷兰的列车可以折返。模型问题是：哪些列车取消、在哪些车站等待、阻断结束后以什么顺序发车。[Transportation Research Part B 78](https://pure.uvt.nl/ws/portalfiles/portal/83710083/1-s2.0-S0191261515000697-main_1_.pdf)
- （一手）Lidén 与 Joborn 的评估模型：取一段长度 L 的线路和长度 H 的时间范围，两个方向的列车均匀运行。养护窗口使线路关闭，为窗口腾出无车时段时，原定发车要么被拒绝（取消），要么被移动，并可能连带推迟后续列车；单线例子中一对列车被拒绝，两次发车调整约 1.6 h，又推迟了两对列车约 0.2 h。[论文](https://www.diva-portal.org/smash/get/diva2:916954/FULLTEXT03)
- （推断）以上做法对封锁的共同表示是：某资源（区段、一条轨道或整个区间）在起止时间之间不可用，或可用轨道数减少；对列车的处理是取消、延误、改道、折返或在站内等待。依据：Louwerse 与 Huisman、Zhan 等、Lidén 与 Joborn、Landex。

### 5.5 修复时间与持续时间的表达

- 起止已知：Zhan 等的问题描述设干扰在时刻 h1 发生、到 h2 结束（一手）。
- 事先未知：Louwerse 与 Huisman 指出持续时间在发生时难以预测（一手）。
- 规章时限：FRA 的补救措施 A2 以 24 小时为一个周期（一手）；技规要求施工封锁按批准的施工方案和调度命令的起止时刻执行，延长要提前通知（一手）。
- 按资源：HOI4 中受损建筑进入建造列表，分配到修复队列的民用工厂使修复速度获得 +200% 加成，没有分配工厂时每天仍推进 1%（社区，[Construction](https://hoi4.paradoxwikis.com/Construction)）。没有找到 HOI4 铁路损坏的专页。
- （推断）"所有断轨修复时间相同"属于"起止已知"一类：时长固定，只是不同断点共用同一个值。来源里没有这种写法的现成先例。

## 6 资料缺口与不确定之处

- **UIC 406 原文**（2004、2013 两版）没有读到。2004 版的公式和建议上限来自 Landex 的转述，2013 版的车站方法来自 Weik 等的转述；2013 版的建议上限数值没有读到，两版之间是否改动未核实。
- **国内标准和现行规章。** TB/T 2110-1990 和 1984 年《铁路区间通过能力计算办法》正文没有读到；技规读到的是 2006 版，现行的 2014 版（普速）和 2017 版（高速）全文没找到，条号只对应 2006 版。运行图周期 T周 的具体组成式、车站间隔时间 τ 的取值表、到发线通过能力和咽喉通过能力的公式，没有找到可核对的文本。国内公式取自百度百科和课件（社区），课件链接带会话号，可能失效。扣除系数的算例（如胶济客专）所在论文的文本层缺失，未引用。
- **车站间隔时间 τ** 的定义只读到百度百科词条的搜索摘要，词条页没能打开；τ 的取值表没有找到。
- **欧盟指令。** EUR-Lex 官方页面抓取失败，读取的是 legislation.gov.uk 的文本。
- **机车换端、推挽**对站线占用的影响，没有一手来源。
- **游戏。** Victoria 3 每级铁路的具体数值和开发者对"抽象容量与显式轨道信号"取舍的直接说明没有找到；War in the East 2 的数值来自手册转载页，没有与官方 PDF 逐字核对；HOI4 的铁路被炸损坏与修复没有找到专页；OpenTTD 和 Create 的轨道故障机制没有找到，不能证明不存在。Create 的"没有死锁处理"只覆盖读取的 10 个源码文件。
- **术语。** 到发线与站台线、站台再占用时间、运营余量、容量占用率等词没有找到国内通行译法的来源；"闭塞时间"六段的名称来自磁浮领域的期刊论文，不是标准。
- **版本与来源状态。** Network Rail NR/L2/TRK/001 读取的是 2012 年发行版，现行性未确认；Cacchiani 等的预印本作者署名未核对；Louwerse 与 Huisman 读取的是工作论文版，与正式版措辞可能不同；OpenTTD 的源码读取自 master 分支，未固定提交。
