# 搜索排序（现行 V3）

当前生产策略：`RankPolicy.Version = 3`（单调证据分级模型，ranking-event schema 4）。V2 基线冻结在 `Tests/Core/SearchRanking/OfflineRankingPolicies.cs`，V2/V3 对照门槛在 `Tests/Core/SearchRanking/ShadowV3EvaluationTests.cs`，演进记录见 [search-ranking-intuition-plan.md](search-ranking-intuition-plan.md)。

## 范围

GUI 有三条搜索路径共用同一 V3 排序原则：

范围是两个 GUI 窗口（快速、完整）与内联窗口的全局段。CLI（`Cli/Search/…`）与 Everything 提供者的名称排序仍借用旧比较器 `SearchResultRankComparer`，不在本文范围内。

## 结论速览

- 两个窗口共用一个内核 `Core/SearchIndex/RankPolicy.cs`：强/弱判定、分值、四段、段内键序与公共尾。快速窗口把候选装饰成 `RankKey[]` 后 `Array.Sort`；完整窗口把同一答案写在 `SearchResult` 的三个字段上，比较器只读字段。
- 先分段，再在段内排序。段序固定：Pinned（始终置顶）→ Boosted（收藏强匹配、H5/H3、正常或高优先的未提升行）→ Low（低优先且未被提升）→ HistoryTail（纯历史、有效分 < 3）。分段是必需的：没有任何一组权重能同时让低优先整段高于历史尾部、又让六档分值保持单调。
- 分值 `S = 10*C + 8*P`：强匹配 C=1；高优先 P=2、正常 P=1、低 P=0。五档 26/18/16/8，收藏强匹配 44，历史 H5 34、H3 22，纯历史尾部 6。0.8 的含义是“一个优先档抵 0.8 个匹配档”：高优先弱匹配（16）排在正常优先强匹配（18）之后。
- 历史以打开次数为主，时间只做小幅修正：`Effective = Count * (1 - 0.15 + 0.15 * 0.5^(days/365))`。时间最多只削掉 15%，且永不归零 —— 打开过一次的东西，隔多久都还带着正分。H5/H3 名额只用于历史行之间的分层与 Tail 判定，不再是历史分能否参与排序的门槛。快速窗口 H5 最多 3 条、H3 最多 10 条；内联窗口降到 1/3；完整窗口只按阈值判定、不设名额，也不加历史行。
- 收藏只在强匹配时置顶；弱匹配不加分。Windows Recent 的历史分恒为 0，只按匹配质量竞争，不会因为“最近用过”被抬。
- 索引规则的“始终置顶”只对高优先生效，且不是加行：命中的行仍须匹配查询，只是进 Pinned 段。
- 引擎截断（每盘缓冲 → 排序 → `Take`）用 `SearchCandidateComparer`，只读匹配质量与公共尾，不读优先级/历史/收藏。“改截断不改终局”是刻意解耦，免得规则一改，被截掉的候选也跟着变。

## 两条管线与两窗差异

加行来源：

| 来源 | 快速窗口 | 完整窗口 |
| --- | --- | --- |
| 快速窗口 | 索引（含跨盘与快捷方式补召回）、已确认历史关联、Windows Recent、收藏、可搜索项；插件即时结果/动作作为前缀 | `RankedCandidateOrdering`（预计算 `RankKey[]`）；默认最多 50 个排序候选 |
| 内联窗口 | 当前文件夹近邻段 + 快速窗口的全局段 | 同层近邻内直接命中优先于学习关联；全局段沿用同一内核（H5=1, H3=3） |
| 完整窗口 | 索引全量；内容 provider 作为前缀 | 到达时 `SearchResultRanking.Decorate`，`RankedSearchResultComparer` 增量合并 |

CLI 与 Everything IPC 的名称排序仍使用 `SearchResultRankComparer`，不在个性化模型内。

## 召回与合并边界

1. **召回与排序解耦**：缺失候选只能在 provider、索引或检索预算层修复，绝不用放大终局排序权重补召回。
   - 选中类型筛选栏的标签时，凡是带类别身份的行（见 `entry-categories.md`）会把收窄下推到索引遍历本身，预算因此花在所选类别内部；自定义规则行没有类别身份，只能继续按行过滤已返回的页。
   - 本地多盘有界查询按 `SearchCandidateComparer` 全局合并后再取 `limit`（`SearchCoordinator`）；网络盘/目录索引多源有界查询同样全局排序截断（`NetworkIndexerSearchExtensions.SearchSnapshots`）。
   - 快速/内联无范围全局查询在主查询流完之后，仅当主结果里一条 `*.lnk`/`*.url` 都没有时才补跑 `ScopedSearchRunner.RunShortcutSupplementAsync`（`fileNameFilter: "*.lnk;*.url"`），避免每次按键都付第二遍索引扫描、第二次设置读取与第二份排除规则；补召回行仍按规范化路径在 `SearchStreamRenderer` 去重。
2. **单一路径实体合并**（`RankedCandidateOrdering.MergeByPath`）：
   - 同一规范化路径只保留一行，可执行的 `Application` 表示优先持有 UI 行与执行委托。
   - 合并后保留更强的直接文本 `Match`/`MatchEvidence`、路径规则输入、收藏并集、最大打开次数、更优类型秩与全部诊断 `RankingCandidateSource` 位；`LearnedAlias` 永不借给直接命中行。

## V3 单调排序内核

快速窗口（`RankPolicy.BuildKey` + `RankKeyComparer`）与完整窗口（`SearchResultRanking` + `RankedSearchResultComparer`）比较同一组结构化键，不使用手工加减的跨档总分。段内键序并非一条通用链，而是每段各一条，两窗只在“完整窗口没有类型秩”这一点上不同（完整窗口的 `TypeOrder` 比较恒等，也不按类型分组）：

```text
Pinned       AlwaysTop ↑ → FavoritePin ↑ → Grade ↑ → Priority ↑ → Band ↓ → Tie ↓ → Placement ↑ → TypeOrder ↑ → Detail ↑ → Tail ↑
Boosted      Grade ↑ → IsFavorite ↑ → Priority ↑ → Band ↓ → Tie ↓ → Placement ↑ → TypeOrder ↑ → Detail ↑ → Tail ↑
Low          Grade ↑ → Band ↓ → Tie ↓ → Placement ↑ → TypeOrder ↑ → Detail ↑ → Tail ↑
HistoryTail  Band ↓ → Tie ↓ → TypeOrder ↑ → Tail ↑
```

`Quality` 在快速窗口被拆成两半：`Placement`（连续性 → 形状 → 左连续 → 起点，`MatchRank.CompareQualityPlacement`）与 `Detail`（覆盖率 → 读音层级，`MatchRank.CompareQualityDetail`），`TypeOrder ↑` 插在两者之间。也就是说，查询落在名字里的**位置**仍压过类型顺序，但名字**长短**（覆盖率）不再压过用户配置的“应用先于文件”——否则搜索 `wand` 时，`wand_gfx` 文件夹（覆盖率 0.5）会压过标题为 `Wand (WeMod)` 的开始菜单应用（0.33）。类型只比较“应用 / 系统设置 / 文件”三个内建桶；第三方 provider 或未列类型共享排在三者之后的同一个秩（不能按对跳过比较，否则排序会成环）。它不跨段、不跨 `Grade`、不压 `Priority`/`Band`/`Tie`/`Placement`。完整窗口没有这一层（恒等，`Placement` 与 `Detail` 直接相连），其余键序一致，并把 `Quality ↑` 换成打包后的 32 位质量键——两者只在量化精度上不同（连续性 1/255、覆盖率 1/511），因此两窗对“差异小于一个量化格”的两行可能给出不同名次。

质量键里的连续性按**正词**计算，而不是把整个匹配跨度当成一个 run：多词查询允许每个词各自连续（如 `cf wj` 对 `cf 桌面文件`，`Contiguity = 1`），再由覆盖率区分；单个词内部散点仍会降低连续性。这个定义只作用于 `Quality`；`Grade`/历史仍先于质量键，类型只在 `Placement` 相等后、覆盖率之前参与。

### 四段硬边界（`RankSection`）

| 段 | 编号 | 进入条件 | 语义 |
| --- | ---: | --- | --- |
| `Pinned` | 0 | `High && AlwaysTop`，或 `IsFavorite && Direct && Match.IsStrong` | 显式置顶：`AlwaysTop` 排在强匹配收藏之前；段判定先于 `Low`，因此被规则降权的强收藏仍然置顶 |
| `Boosted` | 1 | 非 `Low` 的直接命中与次要别名命中（`Grade 0..3`） | 主结果区 |
| `Low` | 2 | `Priority == Low` 的直接/次要命中 | 系统缓存、`AppData\Local/Roaming`（Start Menu 显式豁免回 `Normal`）与用户降权目录；高频打开不跨出 `Low` |
| `HistoryTail` | 3 | `IsHistoryOnly` 学习关联行（`Grade 4`） | 仅在所有直接与次要文本命中之后补充召回 |

### 五级单调证据分级（`MatchEvidenceGrade`）

普通偏好（`High/Normal` 目录优先级、历史打开次数）只能在**同一 `EvidenceGrade`** 内微调，绝不跨级反超；结果类型顺序更窄，只在其他键全部相等后决胜：

| 等级 | 名称 | 判定条件 | 普通类型顺序参与度 |
| ---: | --- | --- | --- |
| `0` | `ExactIdentity` | 文件或应用的直接名称/显示标题完整命中（`.lnk/.url/.appref-ms` 容器后缀或普通文件扩展名按 stem 归一），且 `IsStrong`；系统设置/应用项声明的 `Keyword` 干净前缀命中（如 `env` → `envvar`）；目录不算身份，精确同名目录降为 `DirectStrong` | 在 `Placement` 平局后、覆盖率之前参与（序数比较，无加分） |
| `1` | `DirectStrong` | 直接名称/显示标题命中且 `Match.IsStrong`（词首、词中连续、整词、普通全拼/简拼） | 在 `Placement` 平局后、覆盖率之前参与（序数比较，无加分） |
| `2` | `DirectApproximate` | 跳跃子序列、生僻多音读音等弱直接命中 | 在 `Placement` 平局后、覆盖率之前参与（序数比较，无加分） |
| `3` | `SecondaryField` | 干净（`IsStrong`）的非标题表示：`ShortcutTarget`、`ProductMetadata`、`CuratedAlias`、`ProviderAlias`；以及引擎无名回退（如仅父路径解释）的命中 | 在 `Placement` 平局后、覆盖率之前参与（序数比较，无加分）；副标题显示本地化来源（如 `别名：…`、`目标：…`） |
| `4` | `LearnedAlias` | `IsHistoryOnly` 查询→路径关联召回 | 固定落入 `HistoryTail` |

次要别名（provider 声明的 `ShortcutTarget`、`ProductMetadata`、`CuratedAlias`）要求 `MatchRank.IsStrong`，拒绝零散子序列（防止 `#440` 类误召回）。用户别名功能已彻底移除，不再读取旧 `ResultAliases` 数据，也不提供动作菜单绑定/清除入口。不支持全局同义词表。

`Keyword` 是 provider 声明的同义词（当前用于系统设置的 `System.Keywords`），不是零散猜测：只有每个查询词都干净前缀命中一个声明词时才成立，质量按最短匹配词计算（`env` → `envvar` 覆盖 0.5），并按 `ExactIdentity` 参与类型顺序。2026-10-24 决策；若将来其他 provider 也提供 `Keywords`，需确认其同义词质量是否同样可信。

同一天的第二条裁决给这类命中加了降级边界：默认规则里的 `__SEARCHABLE_ITEM__:SystemSettingsItemProvider:*`（`IndexRuleDefaults.SystemSettingsLowPattern`）把整组系统设置行放进 `TextualLow`。`Keyword` 仍负责跨语言召回（英文查询能找到中文设置项），但不再越过任何 `Boosted` 行：Windows 会给多个控制面板任务挂同一个关键词——`vpn` 同时在「查看网络连接」和「设置虚拟专用网络(VPN)连接」的 `System.Keywords` 里——关键词身份不足以压过可见名称的直接命中。早先「让 `env` 排在 `env.h` 前面」的写法由此作废；设置项现在整体排在直接命中之后，`ExactIdentity` 只在 Low 段内部生效。合成路径的 provider 段用 CLR 类型名而不是本地化显示名（`SearchableItemMapper.SyntheticPath`），所以这条默认规则在 7 种界面语言下都成立。

同一候选的标题强命中优先于任何别名；别名只在标题未强命中时参与最佳证据选择。否则较长的别名可能以覆盖率优势覆盖标题的拼音直接命中，把 `DirectStrong` 降成 `SecondaryField`。

common-only 把每个字表内频率最高的两个读音都视为常见（如 `重` 的 `zhong/chong`、`乐` 的 `le/yue`），不查词表，因此 `重复` 和 `重要` 都会接受 `chong`；第三个及以后读音仍只属于 all-readings 分支，才可能落入 `DirectApproximate`。

目录是容器而非身份：精确同名目录仍是强直接命中（`DirectStrong`），但不占用 `ExactIdentity`。否则搜索 `powershell` 时，`F:\Documents\PowerShell` 会以 Grade 0 压过标题为 `PowerShell 7 (x64)` 的快捷方式；类型顺序只能在 `Placement` 平局后决胜，不能替目录跨级。

上表的 0..4 是文档语义号；枚举 `MatchEvidenceGrade` 另用 `LegacyUnknown = 0` 表示“未知”，真实等级从 1 开始，未知值永不参与排序。回放夹具因此用名字声明等级（`"expectedV3Grade": "ExactIdentity"`），避免编号口径分歧。

公共尾（`RankTail`）依次比较：文件优先于目录、自然名序、名称长度、路径长度、盘符、忽略大小写路径、Ordinal 路径、引擎 tie，保证严格反对称与传递全序。

## 历史频率与查询关联分离

存储：`SearchHistoryStore` 的 `StoredEntry` 带 `Count`（打开次数，权重来源）与 `Score`（0–10 的运行总分，仅作存储，不参与排序）。

权重：`Effective = Count * (1 - RecencyWeight + RecencyWeight * 0.5^(days/365))`（`SearchHistoryBucketSupport.EffectiveScore`），`RecencyWeight = 0.15`；未来时间不放大。次数线性计入且不封顶，时间最多只占 15%、且 `Count = 0` 时才为 0 —— 打开过一次的行永远带正分。

打开一次：`Count += 1`，`Score = min(10, 上一个 Score + 1)`，`Time` 更新。`Score` 只做存储，衰减在读取时算，不写回；把衰减后的值喂回这里是双重衰减、且会立刻撞上 `ScoreCap`。

迁移：旧 JSON 没有 `score` 字段时，`Score = min(Count, 10)`；`SaveEntries` 在锁内保留旧记录的分数，缺分时才用 `Count` 兜底。

- 至少两个无操作符 ASCII 拼音正词同时评估多词分支与同 provider 拼接分支（`PinyinNameEvaluator`），返回真实胜出 witness（provider、tier、span）。排序路径把它直接交给画刷，画刷不再重复打分；只有手上没有 witness 的显示路径（`HighlightMask.Compute`）自己求一次。两者对同一 pattern 与同一文本调用同一确定性函数，因此判定、高亮与解释始终涂同一段。
- 完整窗口只有打包后的质量键，键里没有“胜出分支”这一位，所以 `SearchResultRanking` 一律按 `Original` 变体解等级：带空格的拼音查询在完整窗口最高只到 `DirectStrong`，即使拼接分支真的胜出。单词整词干读音与规范化同名字符串仍判 `ExactIdentity`。残留缺口是已知的：拼接胜出的那一行在快速窗口为 0、在完整窗口为 1，两窗名次可能不同；闭合它需要把该位带过引擎/管道边界（见实施计划的未做项）。
- 应用不写任何排序日志文件。唯一的排序反馈出口是用户在快速窗口右键选择“导出本页排序详情”，一次性导出当前显示页的明文 `.txt`；文本首行自带 `# plaintext:` 声明其中的查询、显示名与路径未做哈希。开关位于“设置 → 通用 → 系统 → 允许导出本页排序详情”，默认开启；关闭后既不构建解释，也不显示该菜单项。离线回放夹具严禁包含真实用户路径（`RankingCaseFixtureLoader` 校验）。
- 排序反馈只需要“预览、不打开”：搜索出错误顺序后，右键任意结果选“导出本页排序详情”，会一次写出当前显示页所有行的明文 `.txt`；不需要打开任何结果，也没有单行复制入口。提交该 `.txt`，并在 issue 中写明原始查询与期望顺序。导出前请检查文件内容：查询、显示名与路径都是明文。

档位：`Effective >= 5` → H5，`>= 3` → H3；资格是“纯历史行”或“强匹配的索引行”。快速窗口按有效分降序（同分按路径 Ordinal）取前 3 条进 H5、接下来 10 条进 H3；第 4 条 5+ 掉进 H3 而不是掉出提升；3–5 分的行不会补 H5 的空位；没进 H5/H3 的纯历史行落 Tail。内联窗口名额 1/3；完整窗口只看阈值，没有 Tail。

排序：历史的有效分在任何非 `Low` 段内都是第一个键（`CompareSectionRank` 的 `HistoryTie` 提前比较），排在分值、左边界、覆盖率之前 —— 匹配决定“能不能进候选”，使用次数决定“谁在前面”。`Low` 段例外：那是用户显式降权，显式指令优先于隐含统计。

scope：`HistorySearchCandidateMapper` 只收集当前 scope 内的历史行；scope 外的路径不参与预算，不会被提升。

Recent：`WindowsRecentCandidateMapper` 的行 `HistoryCount = 0`，历史分为 0，因此永不提升。

历史加行本身仍会经过 `IsHistoryOnly` 标记，排序时按 `NoMatch` 消费——它不冒充文件名匹配（见下文“学习型历史别名”）。

UI：进入 H5/H3 的行在快速窗口名称右侧显示「常用」徽标（`AppSearchResult.IsFrequent`，由 `RankedCandidateOrdering` 按预算结果回填）；Tail 与未入档的行不显示。徽标不改变排序，只是把“为什么这行在前面”显示出来。

### 收藏

`IsFavorite` 由候选自带的标记（快速窗口）或查询级 `RankContext.FavoritePaths`（完整窗口的索引行）提供。只有强匹配给 44 并进 Boosted；弱匹配不加分、也不因收藏进 Boosted。完整窗口的收藏只抬索引行，不加行。

### 始终置顶（AlwaysTop）

`IndexRuleSetting.AlwaysTop` 只在高优先规则上生效（`IndexRuleSyntax.TryParse` 里 `alwaysTop && effect == High` 才保留）。命中的行进 Pinned 段，但**不会凭空出现**：它必须是这次查询的候选，且仍然要匹配查询。

继承规则（`ExclusionRuleSet.GetPriorityVerdict`）：

- 路径规则：最近的一层显式路径规则整体替换祖先，包括“没勾置顶”。子级显式 High 未勾选、或子级显式 Normal，都会清掉祖先的 pin。
- 模式规则（glob/regex）：沿目录链累积取最强；同档时“任一 pinned”胜出。
- 路径规则与模式规则同档平局时路径规则优先——路径规则是文件夹级的显式决策，否则用户关不掉一条全局 pinned 的 pattern。
- `GetPriority(path, isDirectory)` 是 `GetPriorityVerdict(...).Effect` 的薄包装，旧调用点语义不变。

`AlwaysTop` 不产生补搜根：`HighPriorityRoots` 只从路径规则取。所以一条 pinned 的 glob 规则若命中的文件被引擎截断挡在候选池外，这一行不会出现（best-effort）。

### 引擎截断与优先档补搜

`SearchCoordinator` 在 `limit < int.MaxValue` 时按盘缓冲，然后 `OrderBy(SearchCandidateComparer.Instance).Take(limit)`。`SearchCandidateComparer` 只比 `RankSortKey` 高 32 位的匹配质量与公共尾，与“空历史、无收藏、无优先档解析器”的旧比较器逐位等价；它看不到规则与历史，所以截断结果不会因优先级而变。

高优先路径规则另走补搜：`HighPriorityRoots` 给出的每个根单独搜一次（上限 50 条），保证档位高的目录不会因为全局预算不够而整段消失。这是“路径型置顶可靠、模式型置顶 best-effort”的原因。

## 候选预算与显示截断

快速窗口的截断分两层：引擎先按候选预算截，App 排序后再按显示上限截。两层的数字不同，不能混。

引擎侧（`SearchService` → 本地管道/网络源 → `NameSearch`）：

- App 给 `SearchService` 的 `fileLimit`：默认 51；带 token、文件过滤范围、内联文件夹专用范围、filename-like 查询，或存在任意高档规则时是 1000（`SearchDispatchController.cs`）。完整窗口是 `int.MaxValue`（`SearchQueryDispatchController.cs`）。
- `SearchService` 对小于 `int.MaxValue/4` 的有界限额乘 4，给排除过滤留余量：51 → 204，1000 → 4000。本地管道的多盘协调器再按同一上限做一次全局 `Take`，网络源也共享同一上限。需要实时扫描的目录另按 10000 条枚举，不在这条链内。
- 每块本地盘的 `NameSearch` 用管道限额 `L`：保留 `keep = min(max(L,8)*8, 全部行)` 条候选，模式查询再按 `keep*5`（即 `L*40`）放大扫描余量，最后仍只发 `L` 条。默认查询的 204 因此变成每盘扫描上限 8160；4000 变成 160000。
- 截断比较器是 `SearchCandidateComparer`，只看质量与公共尾，所以优先级始终在引擎截断之后才参与。

App 侧（`SearchResultMapper.BuildQuickResults`）：

- 插件即时结果与搜索动作行先作为固定前缀放进列表，不占下面的候选名额。
- 候选池不只是索引行：历史、Windows Recent、收藏、可搜项也进同一个 `RankedCandidateOrdering` 列表，按同一套段与分值竞争下面的 50 个名额，来源本身不保留名额。
- 排序前按规范化路径合并（`PathKey = NormalizedPath.TrimEnd('\\')`）：同一路径的索引行、历史行、收藏行只留一行，信号取并集（收藏取或、历史次数取最大、`TypeRank` 取最小、`IsHistoryOnly` 取与），代表行按 Application → 收藏 → 非历史 → 质量 → `TypeRank` → 名字的顺序挑。合并发生在历史预算之前，所以索引行能吃到自己那条历史；同一路径不会出现“文件一行、历史又一行”。
- `skipDisplayCap` 只在 token、文件过滤范围、filename-like 查询时为真。为真时不截断；否则，若「即时行 + 候选行」少于 10，全部显示；否则候选行先补足到 8 行，再最多显示 50 条（即时行不占这 50 个名额）。候选超过 50 条时追加一行「N more」动作行（`SearchResultHelper.AddShowMoreResult`），点击进完整窗口。
- token 模式虽然跳过 mapper 的截断，文件行在截断前还会经过 `QueryTokenDispatcher`（可过滤/重排），随后 `QueryTokenResultComposer.Compose` 按 `DisplayLimit = 50` 再截一次，即时行与文件行共用这 50 个名额。文件过滤范围与 filename-like 没有这道后置截断。
- 内联窗口的文件夹专用范围（`folderScope`）只加宽预算；没有 token/文件过滤范围/filename-like 时仍按 50 条截。当前文件夹段另有 50 条上限（`ExplorerSearchHelper.CreateLocalSnapshot` 的 `limit = 50`），全局段来自同一个快速窗口结果。
- 完整窗口对索引行没有 App 侧显示上限；索引侧也不截断（`int.MaxValue`）。内容 provider 的 `FullSearchFileRowLimit = 2000` 与首批 60 条是另一套预算，不属于索引排序。

尺寸速记：默认 App 51 → 管道 204 → 每盘扫描上限 8160 → 显示 50；token/文件过滤范围/filename-like 1000 → 4000 → 显示 50（token）或不截（文件过滤范围/filename-like）；内联文件夹范围与「有高档规则」的普通查询也走 1000 → 4000，但显示仍按 50 条截。

## 学习型历史别名（“屏幕截图” → FSCapture.exe 案）

机制。`SearchHistoryStore` 把“搜索框文字 → 打开过的路径”存成键值对；一个路径同一时刻只属于一个 keyword，最近一次从哪个查询打开它就归到哪个 keyword（`SearchResultHelper.HistoryKeywordOf` 记录 `result.SearchQuery`，插件行是剥掉触发词后的文字）。`HistorySearchCandidateMapper` 用 `fuzzy.BestMatch(entry.Keyword)` 拿历史 keyword 匹配查询，不要求文件路径或文件名包含查询文字，命中后用 `entry.Path` 造行，造行前只检查路径是否存在。用户在“屏幕截图”这次查询里打开过 FSCapture，历史就永久建立了“屏幕截图 → FSCapture.exe”的关联；之后每次输入“屏幕截图”，这一行都被加回快速窗口，再打开它又以同一 keyword 写回历史，形成自我维持。完整窗口只按阈值把历史分注入索引行，不会加行。

这解释三个现象：只有快速窗口能搜到（完整窗口的结果域是索引返回的行）；路径/文件名都不含“屏幕截图”（匹配的是历史 keyword）；文件夹改名后快速窗口也搜不到（路径失效，存在性检查丢弃）。最小复现（`HistorySearchCandidateMapper.Collect`）：entry = `("屏幕截图", "D:\tools\smalltools\FastStone Capture\FSCapture.exe")`，fileExists=true 时 rows=1、name=FSCapture.exe、keywordMatchStart=0，fileExists=false 时 rows=0。

历史行标记 `IsHistoryOnly`，排序时按 `NoMatch` 消费，所以 keyword 与查询完全匹配也不会让它冒充文件名匹配；它能不能排前，取决于历史档与段（H5/H3 → Boosted，< 3 分 → HistoryTail）。

同现象的其他来源，按可能性排序：Windows Recent（`WindowsRecentCandidateMapper` 匹配 Recent 快捷方式的文件名 `TargetName`，命中后用快捷方式的目标路径造行；若 `%APPDATA%\Microsoft\Windows\Recent` 里存在名为 `屏幕截图.lnk`、目标为 FSCapture.exe 的快捷方式，会出现同样的现象，但优先怀疑历史）。可搜索项的可能性较低：开始菜单项的 `Title` 是快捷方式名，但 `FullPath` 会是 `.lnk` 路径而不是用户看到的 exe 路径。插件即时结果（尤其 FlowLauncherBridge）可以返回任意路径；当前 Flow 的普通查询只分发给全局插件，且 manifest 里没有截图类插件，不能完全排除，需看结果行的来源标识或禁用插件验证。

别名与收藏夹与此无关，依据以下机制事实：`PinyinAliasProvider.GetAliases` 当前只输出“繁体名的简体写法”一种别名，没有按文件/按路径的用户别名存储；拼音层的 `CanHandle` 要求候选文本里至少有一个汉字，`FSCapture.exe` 是纯 ASCII，不进入拼音层。精确模式（`EnableFuzzyMatch=false`）下“屏幕截图”与 FSCapture 之间不存在文本/拼音匹配。

用户侧定位步骤：

1. 关闭 Lertaro，检查用户数据目录的 `search-history.json`：安装版在 `%LOCALAPPDATA%\Lertaro\search-history.json`，便携版在 `<程序目录>\Data\Users\<用户哈希>\search-history.json`。搜索 `"屏幕截图"`，看对应 `path` 是否为 FSCapture.exe。若是，即为根因；在“设置 → 历史 → 搜索历史”删除该条，或关程序后编辑 JSON。
2. 历史里没有的话，检查 `%APPDATA%\Microsoft\Windows\Recent` 下是否有名为 `屏幕截图.lnk`、目标为 FSCapture.exe 的快捷方式。
3. 两者都没有，再排查插件即时结果：临时禁用 FlowLauncherBridge/第三方插件，或看结果行的来源标识。

## IsMatch 与 Rank 的分裂（已修）

这是一个通用接缝问题，审查时点的反例：

```text
query="!zzz"   name="abc.txt"    IsMatch=True   Rank.IsMatch=False
query="!zzz"   name="防弹笔记法"  IsMatch=True   Rank.IsMatch=False
query="atlas !code" name="code-atlas.txt"
                               IsMatch=False  Rank.IsMatch=True
```

原因：`HighlightMask.ComputeRank` 只能给“被画出来的正词”算秩，纯排除查询没有正词、mask 为空，返回 `NoMatch`；反过来它只画正词、不做排除裁决，所以“正词命中但排除词也命中”的名字照样算得出秩。

修法是把“是否命中”与“如何高亮/打分”彻底分开。`FuzzyMatcher.ComputeMatchRank` 先做整查询 `IsMatch` 门（多词/排除/OR 时），纯 inverse 查询返回显式的 `MatchRank.NoPositiveTerms`（该秩留在质量梯子底部，不能冒充名字匹配），两个窗口对它比较一致；`WindowsRecentCandidateMapper` 与 `HistorySearchCandidateMapper` 都改用 `BestMatch`。

## 拼音与空格混杂的解析行为

当前管线：

```text
输入
  └─ SearchQueryParser：路径模式判定、盘符、Unicode 空白切分
      └─ FzfPatternParser：操作符（! ^ $ ' |）、引号合并、别名 query forms
          └─ 匹配
              ├─ 名字：SearchMatcher / SearchMatcherRow（字面 + 拼音 NFA + 别名 + 混合字母）
              ├─ 路径兜底：PathTermFallback（多词中至少一词命中名字，其余由祖先目录补）
              └─ 路径模式：PathGate / PathSearchFuzzy
          └─ 排序：上面的四段模型
```

拼音层是逐查询、逐候选的实时 NFA（`PinyinTermMatcher`），不烘焙读音；`HighlightMask` 负责最终高亮和 `MatchRank`。这部分没有“索引/查询规则两套实现”级别的分裂。

分词与折叠：只认 Unicode 空白（`Split((char[]?)null)`），转义空格用 `\u0001` 占位；查询与候选名字统一做全角 ASCII→半角、U+3000→空格并做 NFC（`SearchTextNormalizer`，通用 NFKC 不做），显示保留原文，`NormalizeInto` 返回位置映射，`HighlightMask` 用它把 mask/Start 映射回原文；查询解析、索引候选、LiveDirectory、PathTermFallback、PathGate、App 匹配边界全部接入。`SnapshotFormat.Version` 因此升到 14（`UniqueMasks` 增加折叠拼写，旧索引按既有 stale-snapshot 路径重建）。

拼音正例（真实 provider 实测，均精确模式除注明外）：

| 场景 | 查询 | 候选 | 结果 |
| --- | --- | --- | --- |
| 全拼 | `fangdanbijifa` | 防弹笔记法… | TierFull，Start 0 |
| 简拼 | `fdbjf` | 防弹笔记法… | TierInitials，Start 0 |
| 半音节+简拼 | `fdan` | 防弹笔记法… | TierFull |
| 多词拼音 | `fangdan bijifa` | 防弹笔记法… | 各词独立 |
| 汉字+简拼 | `防弹 fdbjf` | 防弹笔记法… | TierName |
| 拼音+英文 | `fdbjf Anna` | … Anna Esor | 各词独立 |
| 繁转简 | `創意市集` | 创意市集 | 字面别名 |
| 简转繁 | `创意市集` | 創意市集 | TierInitials（扁平别名） |
| 混合字母 | `电nao` | 电脑玩物 | 精确 false / fuzzy true（设计如此，仅模糊模式） |
| 单汉字 | `防` | 防弹笔记法 | 字面 |
| 数字 | `fangdan2024` | 防弹2024 | 拼音+字面 |
| 63 字母拼音词 | `a`×63 | 阿×63 | NFA 上限内 |
| 64 字母拼音词 | `a`×64 | 阿×64 | 超过 64 位状态集，false |

拼音的“单词内必须连续”语义被现有测试钉住：`fdbjf` 不会跨 `防-弹` 或 `防 弹` 里的分隔符；`fdbjfgxiao`（跨词）不命中。这是 WinShun 的既定语义，用户可能把“简拼跨空格”当成 bug，建议在设置/文档里写清。

## 边界与反例

- 长名：225 字符 ASCII 名中的 `atlas` 可以命中（Start=120，Shape=InsideWord，Weight≈0.02）；224 字符 CJK 名中的 `pingmujietu`/`pmjt` 可以命中。`PinyinTermMatcher.MaxText = 512` 是码点上限，匹配起点在 512 之后就不再看：实测 prefix=500 拼音 True，prefix=512 与 600 拼音 False（字面 屏幕截图 True）。文件名最多 255 字符，文件搜索不受影响；浏览器标签/插件标题这类可搜索项标题超过 512 码点时，拼音会静默失效。
- 拼音词长上限 63（64 位状态集）：62/63 字符可命中，64 不行；超长全拼查询只能走字面匹配。
- 单字母拼音词（`f`）被 `PinyinTermMatcher.IsEligible` 拒绝（要求 2..63），“输入一个首字母找中文名”不可用；这是 WinShun 的既定设计，对“简拼”用户是明显的手感差异。2-3 字母拼音词召回很大（如 `baj`），排序靠 tier/位置/覆盖率压低；现有测试把 `baj` 作为“半音节中名内命中”接受。
- 空格/英文分词：`fast stone`、`fast  stone`、`stone fast`、`f s c` 都按“空格分词 + 词序无关”工作。`fast stone` 能命中 `FastStone`（两个词分别落在 0 和 4），多词独立子串语义；连续短语必须加引号（`'fast stone'`），引号把它变成一个含空格的词，只匹配真正含空格的文本。只有空格/空查询返回空结果。
- 引号与操作符：`fast\ stone` 把两个词合并成一个含空格的词（实测对 `fast stone notes` 为 true），这是有意的转义。未闭合的 `'` 被当作“精确性开关”，不是字面引号；`!`/`^`/`$` 开头没有通用转义；`$` 结尾永远是后缀锚点，文件真名以 `$` 结尾时只能用单独的 `$` 查。
- 排除：`!term` 只按名字字面裁决，不走拼音/别名/祖先目录（“名字级否决”的既定规则），所以 `!简单` 不能排除一个只有拼音别名含“简单”的名字。繁体查询的排除词不会转换（`GetQueryForms` 只用于正向词），`!創意` 不会排除简体名里的“创意”。
- 名字序比较：`SearchNameOrderComparer` 按数字段显著长度 + Ordinal 比较，不调用 `long.Parse`（20 位以上数字段不会抛 `OverflowException`）。规则为数字先于字母（大写先于小写）、符号最后。
- 虚拟路径：`SearchResultHelper.NormalizePath` 对 `shell:` 与 `__SEARCHABLE_ITEM__:` 前缀有显式分支，不走 `Path.GetFullPath`（否则 app 的 `FullPath` 会被拼成当前工作目录下的相对路径）。
- 引擎截断与终局排序都读 `RankSortKey` 的高 32 位质量键（`SearchResultRelevance.HighKey`），只改其一会让两个阶段发展出不同的排序键语义。该键的每一位都是“越小越好”，比较必须升序；反过来会把最差的行排到最前。

## 有意保留的差异

- 非索引来源（历史、Recent、收藏、可搜索项、插件即时结果）只在快速窗口加行；内容 provider 行只在完整窗口（provider 集合不同）。
- 快速窗口的预算与显示上限：普通宽查询 App 要 51、管道 204、每盘扫描上限 8160，排序后显示 50 条候选行（即时行额外）；token/文件过滤范围/filename-like 要 1000、管道 4000；token 最终仍截 50 条，文件过滤范围与 filename-like 不截。完整窗口索引侧无截断，App 也不对索引行设显示上限（内容 provider 的 2000 条上限另算）。
- 历史名额：快速 3/10、内联 1/3、完整只判阈值。快速窗口的名额是“整页最优”的分配，完整窗口必须让每行到达时就有最终段位，所以放弃了名额。
- 只有快速窗口有收藏新增行与结果类型顺序；完整窗口没有类型层，收藏只作用于已有索引行。列排序只属于完整窗口。
- `RankSortKey == 0` 的完整窗口行按弱匹配处理（保守）；索引行正常情况下低 32 位由引擎填充，不会为 0。
- 只剥一次触发字符：连打两个触发字符时，“显示更多”交接的文本会被完整窗口再剥一次（边缘情形）。
- recency band 与 path depth 不参与排序；shape 位于 contiguity 之后、whole word 可压过更靠左的 word start，左端连续只在 shape 相同时生效（owner 选择的方案）。
- 通用 NFKC 不做，只做全角 ASCII→半角 + U+3000→空格 + NFC；单字母拼音、64 字母拼音、512 码点上限是既有限制。
- CLI 的交互式与非交互式搜索、Everything 提供者的名称排序仍用旧 `SearchResultRankComparer`；管道无 token 路径按设计不排序，排序留给客户端。它们不在本次迁移范围。

## 坑与教训

以下每条是审查时点（修复前）缺陷的结论，编号 F1-F8 保留用于对应回归测试。

- F1 位布局与契约不符：`HighKey` 的 start 只占 6 位（`start << 16`，bit16..21），注释写 `start(7)`、`MatchRank.StartCap=127`，实际位置 63 之后的所有命中在完整窗口完全并列。已修；改布局必须补 63 vs 64 的对比用例。
- F2 默认值吞掉分数：`NoNameFallback` 的 `Contiguity=0` 使 coverage 点永远按最差算，`pathWeight` 完全不参与键值；`MatchRank` 现在有显式 `Coverage` 字段。
- F4/F6 手工构造的 `new MatchRank(tier, start, weight)` 同样因默认 Contiguity=0 拿不到 coverage 分，受影响的不止测试。
- F3 两窗键序相反：完整窗口曾把 history priority 放在 `RankSortKey` 之前，快速窗口放在质量键之后，同一批行同一查询在两窗顺序不同。1c/1d 之后两窗共用 `RankPolicy`，历史只通过 H5/H3 档与 `HistoryTie` 参与，不再各自维护一段键序。
- F5 排序键“删而不净”：shape/recency/depth 不再参与排序后，字段生产者、注释、测试、提交信息还在承诺它们；后续任何人无法从代码判断到底按什么排。`Shape` 随后按 owner 要求重新实现，recency/depth 保持删除。
- 键位置决定设置可达性：类型顺序曾放在六项质量键之后，不同类型的行几乎不可能在质量键上打平，设置页的“结果类型优先级”实际失效；优先档若放在类型分组之内，高优先级文件仍爬不到应用组上面。现在的做法是类型顺序进段内键（Pinned/Boosted/HistoryTail 在质量前，Low 在质量后），并且只用分值而不是硬分组。给用户可见设置排键位时，用“这个键什么时候轮得到出手”验证。
- 行来源混进打分：历史行曾拿 keyword 的匹配秩当文件名的匹配秩参与排序；造行的行必须携带来源标记（`IsHistoryOnly`），梯子按 `NoMatch` 消费它。
- 同一路径的两种表示不该由偏好键裁决：`.lnk` 的索引 File 行与 provider 的 Application 行文本键全等，只有应用行能启动程序。现在由 `MergeByPath` 在排序前合并，Application 行优先，类型偏好决定“先看哪一类”，不决定“同一个路径被表示成什么”。
- 统一排序不解决“候选有没有被取到”：默认 51 的 App 限额（管道 204）让排在后面的同名/近名文件根本到不了统一排序器。filename-like 查询用 1000 预算（管道 4000）并跳过显示上限；token 查询也加宽到 1000，但显示仍由 composer 截到 50。
- `IsMatch` 与 `Rank` 是两个判断：只用 `Rank` 的候选源会绕过匹配门，只用 `IsMatch` 又打不出秩。加行来源必须走 `BestMatch`；“没有正词”要有显式的秩，借用 `NoMatch` 会把本可展示的行整个丢掉。
- 全角残留（U+3000、全角字母数字、NFD 组合形式）是中文输入法下“明明打了空格/看起来一样却搜不到”的主因；折叠必须同时接入查询解析、索引候选与高亮边界（位置映射回原文），并升快照版本号让旧 prefilter 重建。
- 排序比较器里做整数解析：`long.Parse` 在 `OrderBy` 内被直接调用且没有 try/catch，20 位数字段会抛 `OverflowException`；按数字段长度加 Ordinal 比较即可。
- 注释自相矛盾会指导错误维护：`NameOrderComparer` 一处注释写“letters before digits”、另一处写“digits first”，实现是数字在前；此类注释必须与实现统一。
- 测试用例走真服务会变成环境依赖：`EverythingSearchDataProviderTests` 曾因本机服务没在跑抛 `TimeoutException`，被层层包成 `IOException`；改法是把子树应答抽成纯函数 `AnswerFolderSubtreeSize`，查询串路由仍由 `EverythingQueryParserTests` 覆盖。
- 单测通过数要注明口径：`ExplorerStaInvokerTests` 与 `UpdaterRollbackTests` 在方法级并行下会偶发环境性红，不是排序回归；Core 的日常基线要带过滤，见下节。

## 仍然待办

- 应用内 p50/p95 未测：基准只覆盖 `RankPolicy.BuildKey`、`SearchCandidateComparer` 与 `RankedSearchResultComparer`，窗口重绘与数据到达的真实延迟要跑起应用才能取。
- 真实日志的校准未做：0.8 交换率、90 天半衰期、3/10 名额都是先验值，需要用 `ranking-events.jsonl` 的样本回放（`RankingEventLogTests` 只保证采样正确，不评价好坏）。
- 模式型 `AlwaysTop` 在引擎截断下是 best-effort：它不产生补搜根，命中的文件若没进候选池就不会出现；路径型规则由 `HighPriorityRoots` 分根补搜保证。
- “常用”徽标只做了快速窗口（H5/H3 行在名称右侧显示），内联窗口的窄行与完整窗口（纯阈值制、不加历史行）尚未显示。
- CLI 与 Everything 的旧比较器未迁移（有意保留，见上文）。
- FSCapture 类现象的最终归属（历史 vs Recent vs 插件）仍需在用户环境按定位步骤确认。
- 拼音上限（单字母、64 字母、512 码点）与“简拼不跨空格”的语义：写进用户文档/设置提示，或做多词拆分。

## 怎么验证

构建与测试（仓库根）：

```powershell
dotnet test Tests/Core/Core.csproj --filter "FullyQualifiedName!~ExplorerStaInvokerTests&FullyQualifiedName!~UpdaterRollbackTests"
dotnet test Tests/App/App.csproj
dotnet test Tests/Plugins/CoreExtensions/CoreExtensions.csproj
dotnet test Tests/Plugins/PinyinAlias/PinyinAlias.csproj
npm run site:build
```
