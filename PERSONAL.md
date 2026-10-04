# 个人分支：`personal/ui`

本分支**不是给上游的**。它只承载作者个人偏好、上游明确不接受的改动，永久保留，并在上游每次发新版后重新应用。

上游对应关系：跟踪 `upstream/main`（`Lertaro/Lertaro`），推送到 origin（自己的 fork）。

## 这里有什么

| 提交 | 内容 | 上游为何不要 |
|---|---|---|
| `feat(search): show the lunar date beside the quick window's clock` + `refactor(calendar): let the plugin own the clock's lunar switch` | 快速搜索框为空时，在日期后显示农历（含节气、节日），例：`2026/10/4（八月廿四）周日15:15` | PR #313 评审要求回退，理由是宿主侧开关无法表达「插件被禁用」 |
| `feat(ui): restyle the window resize grip` | 窗口右下角拖拽抓手改为三条斜线 | PR #313 评审认为没有改的必要 |

两处都只作用于个人使用，不影响上游行为。

## 设计要点（改动前先读）

**农历**：开关属于 **Calendar 插件自己的配置**（`LunarInClock`，Boolean，默认开），不在宿主的「设置 → 通用」里。宿主不持有任何农历数据，也不持有开关，只通过 `ICalendarTextProvider` 询问**已启用**的插件：

- 插件没装、被禁用、或组件被关 → `PluginManager` 的启用过滤让它根本不会被问 → 不显示
- 插件装好且启用 → 默认就显示，无需任何设置
- 中文以外的界面语言 → 插件自己返回空串（农历/节气/节日名没有翻译）
- 用户在插件配置里关掉 → 返回空串

所以宿主侧**不应**再出现 `ShowLunarCalendar` 之类的开关，也不要往宿主里加农历表。

**抓手**：`App/Resources/Styles/Windows/SearchWindow.xaml` 末尾一段**无 key** 的隐式 `Style TargetType="{x:Type ResizeGrip}"`。无 key 是必须的——该字典被设置窗口、搜索窗口、快速面板共同合并，一条即可三处生效。不要改成 `x:Key`，否则不生效。

## 上游发新版后怎么重新应用

前提：`git remote` 里 `upstream` 指向 `Lertaro/Lertaro`。

```powershell
git fetch upstream
git checkout personal/ui
git rebase upstream/main          # 个人改动整体挪到新上游之上
# 有冲突就逐个解决；两处改动都可独立重做，见下
git push origin personal/ui --force-with-lease
```

若 `rebase` 冲突太多，按提交逐个重做反而更快：

```powershell
git checkout -B personal/ui upstream/main
git cherry-pick <农历两个提交>      # 或按「设计要点」手工重做
git cherry-pick <抓手提交>
```

历史上用过的提交号（上游 rebase 后可能失效，仅供定位）：

- 农历：`9f561ce7`（显示）、`203b32e5`（开关移进插件）
- 抓手：`b5e93367`

重做时的**唯一硬要求**：农历开关必须留在插件配置里、宿主不得持有；抓手样式必须保持无 key。这两条是上游评审否掉原做法的原因，也是这个分支存在的意义。

## 不需要重新应用的东西

`personal/ui` 只放上面两项。从 PR 分支带来的其它工作（快速面板体验修复、磁贴缩放、窗口尺寸记忆、托盘图标、热键、**提醒时间双格控件**、`DEVELOPMENT_GUIDE` 的部署规则）都走正常 PR 流程，合并进上游后自然就有，不要重复搬到这里。

提醒时间控件（`[09] : [30]`，滚轮/方向键各调一格）虽然在日历插件里，但上游评审**没有**要求回退，因此留在 PR 侧，不属于本分支。

## 本地构建与部署

共享工具链在 `D:\Projects\Code\_tools\`，用完整路径调用（系统自带的 `dotnet` 只有 runtime、没有 SDK）：

```powershell
D:\Projects\Code\_tools\dotnet10\dotnet.exe build Plugins\Calendar\Calendar.csproj
D:\Projects\Code\_tools\dotnet10\dotnet.exe publish Lertaro.Plugins.slnx -c Release -o <publish>\Plugins
powershell -ExecutionPolicy Bypass -File Deploy-Local.ps1 -Full
```
