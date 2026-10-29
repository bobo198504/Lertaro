# 搜索响应优化的历次改动与排查记录

这份文档记录 fork 在"搜索响应"上做过的改动：每个问题当时的表现、定位到的原因、改在哪里、验证到什么程度。
它的用途是**回溯**——上游继续演进之后，同类问题可能再次出现，这里能让人很快找到当初是从哪个方向解决的、哪个方向试过但不成立。

新条目追加在对应小节末尾，并注明日期与版本。

---

## 一、单字符查询延迟（引擎层）

**表现**：输入单个字母时，结果列表先空白，1~3 秒后一次性出现；多字符查询正常。5.11.1.4 的引擎是即时的。

**根因（已确认的一部分）**：`FzfAlgorithm.MaskBit` 只有 64 位掩码，非 ASCII 字符被折叠进 `36 + (lower % 28)` 这 28 个桶，中文文件名大量碰撞，掩码预筛形同失效，几乎每个 unique name 都要走完整的 `MatchOne`。

**修复**：掩码从 `ulong` 扩到 `UInt128`，落盘拆成 `UniqueMasksLo` / `UniqueMasksHi` 两列，AVX2 预筛改为两组 `Vector256`。

**涉及文件**：

```
Core/SearchIndex/Fzf/FzfAlgorithm.cs          GetCharMask / MaskBit 返回 UInt128
Core/IndexV2/Persistence/Snapshot.cs          UniqueMasks → UniqueMasksLo + UniqueMasksHi
Core/IndexV2/Persistence/SnapshotFormat.cs    Version 14 → 16，新增一行 section
Core/IndexV2/Persistence/SnapshotWriter.cs    写两列
Core/IndexV2/Search/SearchMatcher.cs          RequiredMask / OrSetMasks 与 AVX2 块
Core/IndexV2/Search/PathMode/SearchMatcherPath.cs
Core/IndexV2/Alias/AliasGenerationUtf8.cs
```

**实测**：`a` 26915ms → 1731ms，`s` 30076ms → 1457ms，`z` 3545ms → 1781ms，`q` 2068ms → 1124ms。

**注意**：`SnapshotFormat.Version` 变了会强制全量重建索引，大容量 exFAT 盘要几分钟。切换部署版本时若版本号在 14/16 之间来回，就会反复重建。

---

## 二、一级缓存（QueryPageCache）

缓存挂在每个盘的 `LiveIndex` 上，键是 `(query, directoryFilter, fileNameFilter, category)`——**分类参与键**，所以切标签必然换键。

**问题 1：窄分类永远无法命中**

原逻辑"页短于请求就拒绝"，而快速窗请求 204 行。匹配数少于 204 的分类（音频、压缩包、设置）每次都被拒绝重放，等于缓存对它们不存在。

**修复**：加 `Exhausted` 标志。扫描**取尽**（`scannedLimit <= MaxRows && rows.Count < scannedLimit`）的页可以回答任意请求量；只有被请求量截断的页才拒绝。

**问题 2：某盘无匹配时每次重扫整份索引**

原 `Store` 遇到 `rows.Count == 0` 直接返回，不建页。于是"这个盘对这个查询没有结果"这个**要扫遍全表才能得出**的结论，每次按键都要重新付一遍代价。

**修复**：空结果也建页，标记 `Exhausted = true`。

**问题 3：60 秒过期太短**

`RecentGraceMs` 从 60 秒改到 10 分钟。原值下切分类、隔一会儿回来就重建，预热也刚建好就过期。

**问题 4：重启丢全部缓存**

新增 `QueryPageCacheDiskStore`，用 `SourceKey` + `Revision` 做标识，服务停止时落盘、启动后逐盘恢复（`SearchEngine.RestoreSearchPages` 在索引 `IsComplete` 后幂等恢复，`UsnService` 每 3 秒轮询一次、最多 10 分钟）。

**涉及文件**：

```
Core/IndexV2/Search/QueryPageCache.cs             Exhausted / Snapshot / Import / 容量 512
Core/IndexV2/Search/QueryPageCacheDiskStore.cs    新增，二进制落盘，版本 1
Core/SearchEngine.cs                              RestoreSearchPages / PersistSearchPages
Service/UsnService.cs                             启动后轮询恢复
```

**注意**：每页存 204~512 个 `SearchResult` 对象，约 50~100KB，512 页就是每盘 30MB 上下。容量不能随便再放大。

---

## 三、排序

**问题**：历史记录和"应用优先"的权重上不去——常用项、所选类型都被"匹配位置更靠前"的行压过。

**根因**：`RankKeyComparer.Compare` 的比较顺序是 `Match.Start → Contiguity → Section → Score → HistoryTie → TypeRank`，也就是**匹配位置先于使用权重的比较**。

**修复**：把两个使用权重的比较提到匹配质量之前。

```csharp
var byHistory = y.HistoryTie.CompareTo(x.HistoryTie);
if (byHistory != 0) return byHistory;
var byType = x.TypeRank.CompareTo(y.TypeRank);
if (byType != 0) return byType;
```

这两个键对绝大多数行都是 0 或相等，所以只决定"习惯 vs 巧合"，不会打乱普通结果。

配合把 H5/H3 的分数从 44/22 提到 **60/50**（高于收藏强匹配的 44）。

**涉及文件**：`Core/SearchIndex/RankPolicy.cs`（`RankKeyComparer.Compare`、`HistoryFiveScore`、`HistoryThreeScore`）。

---

## 四、快速窗渲染（UI 响应）

三个参数，都影响"按键到看到结果"的体感：

| 参数 | 原值 | 现值 | 原因 |
|---|---|---|---|
| `SearchStreamRenderer.FirstRenderDelayMs` | 16 | **30** | 16ms 那一帧常常还是空的，随后收尾帧又整批重绘一次；等一帧让常见查询只画一次 |
| 渲染回调的 `DispatcherPriority` | Background | **Normal** | Background 排在上一帧的布局/绘制之后，数据在手却迟迟不上屏 |
| `ProgressiveRenderPlan.MinimumFirstRender` | 9 | **1** | 原来要凑够 9 行才肯画，匹配 1~2 个的查询在搜索完成前完全不显示（实测 100~200ms 空白） |

**故意没搬的**：`quickWindowDelay` 10→15、完整窗防抖 30→150。这两个是"用响应速度换合并按键"的权衡，不是纯优化，在上游版本里没有采用。

**涉及文件**：`App/ViewModels/Search/SearchStreamRenderer.cs`、`App/ViewModels/Search/ProgressiveRenderPlan.cs`。

---

## 五、设置窗口

**问题 1：打开窗口先顿一下**

`SettingsWindow.Loaded` 里同步设 `SelectedIndex = 0`，会在窗口显示的同一帧构造默认页（XAML 解析 + DataContext 接线）。

**修复**：延迟一个空闲时段再选，窗口先出现、内容下一帧填；预热器的 `Begin()` 也挪到首页之后，免得抢同一个空闲槽。

**问题 2：首次进插件页"闪一下像大刷新"**

`LoadAsync` 原来是 `foreach (plugin) Plugins.Add(...)`，每条 Add 触发一次 `CollectionChanged`，列表按插件数量重排 N 次。

**修复**：一次性替换集合（`Plugins = new ObservableCollection<>(list)`），排序的 `Move` 发生在重绑定之前（无消费者），最后只发一次 `Plugins` 属性变更。

**问题 3：插件页卡 30 秒**

`BuildPluginList` 会对每个插件调 `IConfigurable.GetConfigSchema()`，那是**插件自己的代码**，部分实现很重。28 个插件走一遍就是几十秒，且只在第一次扫描时发生。

**注意**：不要在打开设置窗时预热 `LoadAsync()`——那等于把这次扫描提前到窗口打开那一刻，实测比不预热更糟。它在点进插件页时才该发生。

**涉及文件**：`App/Views/Settings/SettingsWindow.xaml.cs`、`App/ViewModels/Settings/Plugins/PluginManagementViewModel.cs`。

---

## 六、启动与热键

**问题 1：双击 Ctrl 要等几秒才响应**

链路：`HookIpcClient` 请求服务启动 hook → 服务拒绝/未就绪 → **退避 5 秒**后重试。

实测时间线（冷启动）：App 启动 → 0.46s hook 进程起来 → 0.7s 进程内初始化 → 就绪。而"服务刚起来时第一次请求失败"会额外加 5 秒。

**修复 1**：`LaunchRetryDelayFirstMs` 从 5_000 改到 **500**（上限仍 60 秒，防坏安装刷日志）。hook 就绪从约 6 秒降到 **0.93 秒**。

**修复 2**：`HookLaunchBroker.LaunchAsync` 在 elevated 请求被拒后**立即回退**请求普通 hook。原来只用一种，被拒返回 null 靠 backoff 循环等下一轮。

**修复 3**：`GlobalHotkeyDetector.TryHandleQuickSwitchNavigation` 移除 `Monitor.TryEnter(StateLock, 50)`。tracker 线程会跨 2 秒的插件读取持锁，热键在争用中直接丢弃按键；改为无锁读（bool/引用读取本身原子），`RememberedPath` 移进后台任务。

**涉及文件**：`Core/Hook/Ipc/HookIpcClient.cs`、`Core/Hook/Ipc/HookLaunchBroker.cs`、`Core/Hook/InlineSearch/GlobalHotkeyDetector.cs`。

**部署注意**：停 App 时要**连 `--hook` 实例一起停**（按 `--hook` 命令行 + 安装目录路径双重核对）。只停 App 会留下孤儿 hook，服务要等它失主退出才允许启动新的，那段时间热键完全不响应（实测 34 秒）。

---

## 七、分类

- 分类采用上游最新的**九类**，新增 `Archive = 8`：`Core/Wire/SearchCategoryFilter.cs`、`App/ViewModels/Search/SearchResultCategorizer.cs`、`QuickSearchViewModel.Categories`、`CategoryTranslationKey`。
- 翻译键 `Search_Category_Archive` 需同时补进 `Plugins/CoreExtensions/Resources/Translations/*/App.json` 的 7 个语言文件。
- **注意**：`Search_Category_*` 这组键在 5.12.2.2 的插件翻译里**被删过**，回退插件时要一并确认，否则整个标签栏显示成键名。
- 标签相关设置项（隐藏类型过滤栏、记住上次分类、循环切换键）随 `App/Views/Settings` + `App/ViewModels/Settings` 一起搬过来，翻译键需补齐（`Hotkeys_CycleCategory` 等 20 个）。

---

## 八、排查方法（很重要，能省掉重复踩坑）

**1. 不要用 `lff --search` 衡量快速窗延迟**

`lff` 走完整窗口（`limit = int.MaxValue`），而 `QueryPageCache.TryReplay` 会拒绝比请求短的页，所以完整窗查询**永远不命中缓存**。用它测出来的耗时不能代表快速窗。

**2. 用日志时间线定位，不要猜**

进程的 `CreationDate`、`hook.log` 的 `Log initialized` / `initialized successfully`、`app.log` 的启动行，三者对齐就能算出每一段各花了多久。

**3. 提权脚本里的 `$env:TEMP` 可能不是你预期的目录**

`Start-Process -Verb RunAs` 起的进程，`$env:TEMP` 会解析到别处，用它拼路径会让 `Copy-Item` **静默失败**（脚本成功退出但什么都没复制）。部署脚本里一律用**绝对路径**。

**4. 插件是单独一步发布的**

`make.bat` 里是两步：

```batch
dotnet publish Lertaro.slnx        -o <OUT>
dotnet publish Lertaro.Plugins.slnx -o <OUT>\Plugins
```

只发前者，插件 DLL 永远不会更新，而且 `dotnet build Lertaro.slnx` 也不会编译插件项目（错误要单独 `build Lertaro.Plugins.slnx` 才能看到）。

**5. 有几个插件是嵌套输出的**

`BrowserData` / `Calendar` / `ContentSearch` / `FlowLauncherBridge` 的 DLL 在自己的子目录（`Plugins/<AssemblyName>/`），不在 `Plugins/` 根目录。部署时若根目录还留着旧同名 DLL，加载器会先扫到旧的并报 "Unable to load one or more of the requested types"。

**6. 诊断探针本身会制造卡顿**

在热路径上做同步 `File.AppendAllText` 会把它正在测量的问题变成问题本身（曾经 20 个探针让一次搜索多出 110ms）。要测就测一次，不要留在代码里。

---

## 九、尚未解决的问题

**单字符查询在 V3 排序内核上仍然慢**，根因未定位。

已排除：页面缓存命中率、暖机、排序追踪开销、App 侧可搜索项收集（各字母耗时完全对称）。

已上报：<https://github.com/Lertaro/Lertaro/issues/482>

当前决策：fork **停在 5.11.1.4 的引擎与排序**（`IndexV2` + 旧排序，不带 V3），其余改动照常跟进。

如果上游之后解决了这块，上面第一到第七节的改动都是可以直接叠加的（它们不依赖具体哪一代引擎）。
