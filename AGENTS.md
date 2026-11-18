# AI Agent Development and Collaboration Guidelines for C# Projects

## Ponytail, lazy senior dev mode

You are a lazy senior developer. Lazy means efficient, not careless. The best code is the code never written.

Before writing any code, stop at the first rung that holds:

1. Does this need to be built at all? (YAGNI)
2. Does the standard library already do this? Use it.
3. Does a native platform feature cover it? Use it.
4. Does an already-installed dependency solve it? Use it.
5. Can this be one line? Make it one line.
6. Only then: write the minimum code that works.

## Rules

- No abstractions that weren't explicitly requested.
- No new dependency if it can be avoided.
- No boilerplate nobody asked for.
- Deletion over addition. Boring over clever. Fewest files possible.
- Question complex requests: "Do you actually need X, or does Y cover it?"
- Pick the edge-case-correct option when two stdlib approaches are the same size, lazy means less code, not the flimsier algorithm.
- Mark intentional simplifications with a `ponytail:` comment. If the shortcut has a known ceiling (global lock, O(n²) scan, naive heuristic), the comment names the ceiling and the upgrade path.

Not lazy about: input validation at trust boundaries, error handling that prevents data loss, security, accessibility, the calibration real hardware needs (the platform is never the spec ideal, a clock drifts, a sensor reads off), anything explicitly requested. Lazy code without its check is unfinished: non-trivial logic needs real MSTest coverage as part of the same change --- see rule 10 for what counts as worth testing and rules 11/14 for how to write and structure it. Trivial one-liners and pure UI/XAML glue need no test.

(Yes, this file also applies to agents working on this repo's own tooling and guidelines --- including edits to `AGENTS.md` itself. Especially to them.)

---

## 更新日志 / 发行说明：规则的家在哪（2026-10-07 补）

**通用规则不写在本文件里**，以两处为准：

1. `bobo198504/.github` 的 `CONTRIBUTING.md`（账号级默认文件，所有仓库可见）；
2. 本机全局偏好 `~/.dsh/AGENTS.md` 的「更新日志 / 发布说明」一节。

跨项目工程流程参考：`D:\Projects\Code\_Process optimization`（本项目档案 = `projects/lertaro.md`）。

**本仓库专属的那部分留在这里，不要搬走：**

- **发行说明的"源" = 本仓库 `CHANGELOG.md`**；GitHub 上的正文是它的**投影**（中 / 英 / 哈希三栏、最新在上、
  只写与上游的差异；`+ 基线：…` 这类非改动行不写）。
- **`v5.12.6.1` 起的条目结构**（用户 2026-11-05 定，替换此前的单栏列表）：
  `上游：` 与 `本fork：` 两栏，各自一个代码块，条目仍用 `*` `+` `#` 标性质。
  - `上游：` **照抄上游发行说明里列出的条目**，不加也不减。它通常是 GitLab release 里自动生成的
    提交列表（例如 `bump: version v5.12.6`、`fix(ci): retry to fix`），条目少是正常的 —— 不要据提交
    自行归纳补全，也不要因为「看起来没内容」而改写。
    **唯一例外：`bump: version x.y.z` 这类版本号提交不是更新内容，不进这一栏**（2026-11-05 用户指出）。
  - `本fork：` 列**当前与上游存在的全部差异**，只要还在就写，不是只写本版新增。
    两条判据，写每条前都要过一遍：
    1. **上游也有的改动不进这一栏** —— 从上游 cherry-pick 过来的修复属于上游，写进「上游」或不写，
       不能算 fork 的差异（2026-11-05 用户抽查：预览编码、卷句柄、inline 名字单元格三条被误列）。
    2. **已移除的功能不进这一栏** —— 把上游没有的东西加上又拿掉，移除后与上游无差异；移除那次
       在当版写一次就够了，之后不再重复（一级缓存即此）。
  - `附：` 一栏写用户额外交代的事，**没有则整段省略**。
  - 中英各一份，结构对应（`Upstream:` / `This fork:`），其后仍由 workflow 追加 `## SHA-256 Checksums`。
- 正文 = `## <tag>` 那一条（去掉版本标题本身）＋ `## SHA-256 Checksums`（**只取该发行自己的**资产 digest，
  跳过 `.sig`；**绝不取上游 release 的哈希** —— 两者构建签名不同）。
- **一次写对**：`.github/workflows/fork-release-notes.yml` 是这条规则的**可执行实现**（上游工作流先覆盖正文，
  它作为最后一个写入者再写对）。它必须存在于**默认分支 `main`** 上。
- fork 的三条本地规则与上游差异说明，见 `_Process optimization\projects\lertaro.md` §9.22 / §9.21。

- **fork 每个版本的更新日志都必须含一条「保留鼠标平滑滚动」**（中文 `+ 保留鼠标平滑滚动`，英文 `+ Keeps the smooth wheel scrolling`），
  位置与其它条目同框；**只要上游还没把这个功能接回去，以后 fork 所有版本都带这条**（用户 2026-10-09 明确）。
- **fork 每个版本的更新日志都必须含一条「自动更新只检查本仓库的发行」**（中文 `+ 自动更新只检查本仓库的发行`，英文 `+ Auto-update only checks this repository's releases`），位置与其它条目同框。本 fork 已是**正式分支**：自动更新若指向上游，上游未发版的改动会被装进便携安装（2026-10-11 拖慢搜索的权限检查即此），而便携安装无法回退。**以后 fork 所有版本都带这条**（用户 2026-10-11 明确）。
- **Ctrl+G（Quick Switch）是本 App 最重要的功能之一，不能再坏**：任何涉及活动路径提供者、对话框跟随、
  `GlobalHotkeyDetector`、`ExplorerActivePathPoller`、`ExplorerTracker.LastActiveExplorerPath` 的改动，
  都必须验证"在文件管理器里换目录后按 Ctrl+G 立刻跳到当前目录"，且**两个文件管理器同时开着**时也要成立。
---

# Lertaro 本机工程规则（2026-10-19 写入；新对话请先读完这一节再动手）

## 环境与命令（固定，不要临时换）
- SDK 只用共享工具链：`D:\Projects\Code\_tools\dotnet10\dotnet.exe`（裸 `dotnet` 解析到无 SDK 的 runtime）。
- 先设 `$env:NUGET_CONFIG_FILE='D:\Projects\Code\_tools\nuget.config'`。
- 构建：`... build Lertaro.slnx --nologo -v:q`（默认 Debug）。
- 测试：**`Lertaro.slnx` 不含任何测试工程**，`dotnet test Lertaro.slnx` 是**空跑**（exit 0 无意义，绝不能当作"测试通过"）。
  真实测试工程在 `Tests\**\*.csproj`（30 个，例如 `Tests/Core/Core.csproj`、`Tests/App/App.csproj`、`Tests/Plugins/PinyinAlias/PinyinAlias.csproj`）。
  跑单个：`... test Tests\Core\Core.csproj --nologo -v:q`（不加 `--no-build`；要过滤用 `--filter 'FullyQualifiedName~关键字'`）。
- **本机 `dotnet test` 目前跑不起来**（2026-10-19 实测，已核实）：testhost 对**父进程 `vstest.console`** 调
  `OpenProcess(..., SYNCHRONIZE)` 时被拒 → `Win32Exception (5) 拒绝访问` @ `ProcessManager.OpenProcess`。
  注意：崩溃栈里的 `testhost.x86` 是**符号误导** ✗，架构是 x64，与架构无关；也**不是** shell 完整性级别的问题 ✗。
  用 `explorer.exe` 拉 `.vbs` 绕开工具进程树**也没成功** ✗（无输出、无 trx）。
  因此：**不要声称"测试通过"**；`dotnet build Lertaro.slnx` 也**不包含 `Tests/**`**，测试工程从未被编译过 ✗。
  可行验证 = 构建 exit 0 + 部署后人工验证；要跑测试需先解决 testhost 的该问题（待查：是否 SRP/EDR 拦截或 job/句柄限制）。
- **`Tests/**` 与 fork 的偏差**：`Tests/Core/Hook/Ipc/OpenedFolderSnapshotPublisherTests.cs` 在 `1bff6429` 上引用
  `OrderByWindowRank` / `WindowRanks`，而 fork 的 `OpenedFolderSnapshotPublisher` 里没有这两个成员 ✗（fork 用桌面窗口过滤替代）。
  要让测试真正可用，必须先让 `Tests/**` 编译通过（对齐 fork 实现），否则任何"跑测试"都是空谈。

## 部署（本机便携安装 `D:\App protable\Lertaro`）

### 唯一正确入口（不要再手抄插件/服务步骤）

在仓库根目录执行：

```powershell
$env:NUGET_CONFIG_FILE = 'D:\Projects\Code\_tools\nuget.config'
& 'D:\Projects\Code\_tools\dotnet10\dotnet.exe' build Lertaro.slnx -c Release --nologo -v:q
& 'D:\Projects\Code\_tools\dotnet10\dotnet.exe' build Lertaro.Plugins.slnx -c Release --nologo -v:q
& '.\Deploy-Local.ps1' -Full -Publish
```

- `Deploy-Local.ps1 -Full` 会自动弹 UAC，不需要先手工开管理员终端。
- `Deploy-Local.ps1` 按既有规则由 `.git/info/exclude` 设为**本机工具，不进 Git**。部署在
  `D:\Projects\Code\Lertaro` 主工作区执行，不要进入临时 worktree 后因文件不存在又重新写一份。
- `-Publish` 现在分别 publish `Lertaro.slnx` 和 `Lertaro.Plugins.slnx`；后者不能再漏。`Lertaro.slnx`
  不含插件工程，App 也不会自动把全部插件带出来。
- 脚本按完整路径停 App、停服务、复制文件，再通过 `explorer.exe` 以普通完整性级别启动 App；
  App 的 `AppStartupServiceBootstrapper.EnsureServiceStarted()` 会自己拉起服务。**不要手工先 `Start-Service`。**
- 新版把部分插件及依赖放在 `Plugins\<AssemblyName>\`。旧的根目录 DLL 会抢先加载；脚本会把 publish
  根目录不存在的旧 DLL 移到同级的 `Lertaro.stale-plugins-<时间>`，不删除且不碰 `Data\`。
- 停进程只能按可执行文件完整路径判定；禁止按名字或命令行子串批量终止。
- 改完直接部署，不必请示。部署后以 `deploy-local.log` 最后一行的 `done (all checks passed)` 为准；
  若失败，先看同文件的 copy/plugin/service FAIL，不要在不稳定状态上继续叠改。

### 部署后必须核对

1. `deploy-local.log` 无 FAIL；安装版 `Lertaro.App.dll` 的 ProductVersion 是目标版本。
2. `Lertaro.App` 进程正在运行，`LertaroService` 为 `Running`，且存在本安装目录的 `Lertaro.Service.exe --hook`
   子进程。
3. 最新用户日志 `Data\Users\<SID哈希>\logs\app.log`、`hook.log` 和 `Data\Machine\logs\service.log`
   有本次启动时间线，无 Error/Unhandled/CRITICAL。**工具进程的用户和桌面 App 用户可能不同**：不要用工具
   自己的 SID 去找日志，也不要用 `C:\Users\<工具用户>\AppData` 猜测；按目录最后写入时间找最新用户。
4. `service.log` 应出现 `Loaded N/N per-drive caches`，Hook 应出现 `Hooks and ExplorerTracker initialized successfully`。
5. 关键文件 `Lertaro.App.dll/.exe`、`Lertaro.Service.dll/.exe`、`Lertaro.Core.dll`、`Lertaro.PluginSdk.dll`
   的 install 与 `publish\x64\Lertaro` SHA-256 必须一致；只数 DLL 数量不足以证明版本正确。

本机常见错误与处理：

- `Cannot open LertaroService`：这次部署没有管理员权限；重新走上面的入口，让脚本自己弹 UAC。
- `plugin count mismatch`：旧根目录 DLL 还在。查看 `deploy-local.log` 中 quarantined 的路径；
  确认新版插件确实位于各自子目录后重跑，不要用手工删除整个 `Plugins`。
- `app pid` 为空但 `Lertaro.App` 进程存在：旧脚本读错了 `ProcessId` 属性；现在日志应显示 `Id`。
  判断运行状态始终看进程路径，不要只看窗口是否可见。

## fork 更新、打包发布与上游 PR（新对话先看这里）

### 远端、分支和权限边界

- fork：`origin = https://github.com/bobo198504/Lertaro.git`，主线是 `local/main`。
- 上游：`upstream = https://github.com/Lertaro/Lertaro.git`，主分支是 `main`。
- GitLab 远端是历史遗留，不是 fork 的更新或发布目标；自动更新只认 `bobo198504/Lertaro`。
- 所有 `gh` 命令都显式带 `--repo bobo198504/Lertaro` 或 `--repo Lertaro/Lertaro`。不要依赖当前目录推断，
  本机曾把 fork 的 Release 命令解析到上游仓库。
- `git commit`、`git push`、创建 tag、创建/修改 Release、向上游提 PR，都必须由 owner 在本轮明确授权。
  构建、测试、本地部署不需要另行确认。
- 开始合并或发版前先看 `git status`。工作区有其它未收口改动时，不要直接在坏/脏状态上 merge、bump 或打包；
  先让 owner 处理，或使用隔离 worktree。

### 1. 选择性同步上游到 fork

**本 fork 不再整版合并上游 Release。** 默认只吸收明确有价值、经过审查的修复或功能；上游新增或重新接回的功能
不会因为出现在新 Release 中就被自动接受。只有 owner 在本轮明确要求恢复，才可改变本节的永久禁用项。

1. 确认当前版本、分支和未提交改动：

```powershell
git status --short --branch
git log -5 --oneline --decorate
```

2. 获取 fork 与上游 tag，切到 fork 主线并快进：

```powershell
git fetch origin --prune
git fetch upstream --tags --prune
git switch local/main
git pull --ff-only origin local/main
```

3. 先列出目标上游 Release 相对 fork 的候选提交和差异，再逐条决定是否吸收：

```powershell
$upstreamTag = gh release view --repo Lertaro/Lertaro --json tagName --jq '.tagName'
git log --oneline HEAD..$upstreamTag
git diff --stat HEAD..$upstreamTag
```

   - 审查提交的目的、行为变化、冲突面和相关测试；没有明确收益的提交默认不收。
   - 只 cherry-pick 或手工移植选中的提交；不要执行整版 `git merge` 默认接受全部上游内容。
   - 上游提交依赖大范围重构时，优先最小手工移植；不要为了拿到一个小修复而吞入无关模块。

4. 以下 fork 契约永久保留，任何上游更新都不得接回；对应源码即使保留，也只能处于注释或不可达状态：

   - **打开/保存等文件对话框不自动弹出内联搜索卡片**。Ctrl+G 和切换窗口自动跳转必须保留；
   - **快速启动面板永久禁用**，包括启动项、数据源、空状态展示及设置页；
   - **快速面板永久禁用**，包括窗口、管理器、热键、进程黑名单及设置页；
   - 左侧优先、历史权重、便携应用召回排序；
   - 128 位名称掩码及查询实际使用半区的扫描；
   - 连续前缀快路径与查询页缓存增强；
   - 鼠标平滑滚动和自定义应用搜索目录；
   - `App/Services/Update/UpdateChecker.cs` 只检查
     `https://api.github.com/repos/bobo198504/Lertaro/releases/latest`。

   `Tests/App/Helpers/ForkDisabledFeaturesTests.cs` 是前三项的合并守卫；运行 `Tests/App/App.csproj` 时必须通过。
   同步后还要逐文件核对本节的其它契约，不能只看编译是否通过。

5. 版本号规则：上游 `X.Y.Z` 对应的 fork 首版是 `X.Y.Z.1`；同一上游基线上的后续 fork 修订递增为 `.2`、`.3`。
   只改 `App/App.csproj` 的 `<Version>`，不要改上游版本语义。这里只借用上游基线的版本层级，不代表整版内容全部进入 fork。

6. 在 `CHANGELOG.md` 最上方增加对应版本段，保持现有中英双栏结构：

   - `上游：` 只写本版实际审查并吸收的上游条目；不是当前 Release 的全部条目，`bump: version ...` 不写；
   - `本fork：` 写当前仍存在的全部 fork 差异；三项永久禁用功能作为 fork 契约保留记录；
   - 每个 fork 版本必须有 `+ 保留鼠标平滑滚动` / `+ Keeps the smooth wheel scrolling`；
   - 每个 fork 版本必须有 `+ 自动更新只检查本仓库的发行` /
     `+ Auto-update only checks this repository's releases`。

7. 构建、测试、部署，确认稳定后才进入发版。不能把“候选提交已移植”当成“可发布”。

### 2. 打包并发布 fork

只在 `local/main` 的发布提交上执行；`dist/` 会被 `make.bat` 整体重建。下面命令保持在同一 PowerShell
会话，`$tag` 会在创建 tag 时从 `App.csproj` 生成。

1. 构建两个解决方案：

```powershell
$env:NUGET_CONFIG_FILE = 'D:\Projects\Code\_tools\nuget.config'
& 'D:\Projects\Code\_tools\dotnet10\dotnet.exe' build Lertaro.slnx -c Release --nologo -v:q
& 'D:\Projects\Code\_tools\dotnet10\dotnet.exe' build Lertaro.Plugins.slnx -c Release --nologo -v:q
```

2. 用 unsigned 模式生成 x64/ARM64 安装包和 ZIP。fork 不处理真实签名，`.sig` 是 8 字节 `unsigned` 占位：

```powershell
$oldPath = $env:PATH
$env:PATH = "D:\Projects\Code\_tools\dotnet10;$env:PATH"
$env:LERTARO_ALLOW_UNSIGNED = '1'
cmd /c make.bat
$env:PATH = $oldPath
Remove-Item Env:LERTARO_ALLOW_UNSIGNED
```

3. 必须存在且上传六个资产：

```
dist/Lertaro-Setup.exe
dist/Lertaro-Portable.zip
dist/Lertaro-Portable.zip.sig
dist/Lertaro-Setup-arm64.exe
dist/Lertaro-Portable-arm64.zip
dist/Lertaro-Portable-arm64.zip.sig
```

   发布前至少核对两个 ZIP 内的 `Lertaro.App.dll` ProductVersion、App/Service exe 架构为 x64/ARM64，
   以及四个主资产 SHA-256。不要上传 `publish/`、`bin/` 或安装后的 `Data/`。

4. 创建并推送 tag。tag 必须是四段 fork 版本，不能复用上游三段 tag：

```powershell
[xml]$project = Get-Content App/App.csproj
$tag = 'v' + $project.Project.PropertyGroup.Version
git tag -a $tag -m "Lertaro fork $tag"
git push origin local/main
git push origin "refs/tags/$tag"
```

5. tag 触发的 `Build and Release` 有 36 小时节流；`build-and-release` 被 skipped 很正常。先检查 Release：

```powershell
gh release view $tag --repo bobo198504/Lertaro
```

   若自动构建因 fork 没有上游 `SIGNING_KEY` 而失败，不要修改应用的签名验证来绕过；按本机 unsigned
   流程手动发布即可。

   不存在时手动发布已校验的本地产物：

```powershell
gh release create $tag --repo bobo198504/Lertaro `
  --title $tag --verify-tag --latest `
  --notes 'Release notes are generated from CHANGELOG.md.' `
  dist/Lertaro-Setup.exe dist/Lertaro-Portable.zip dist/Lertaro-Portable.zip.sig `
  dist/Lertaro-Setup-arm64.exe dist/Lertaro-Portable-arm64.zip dist/Lertaro-Portable-arm64.zip.sig
```

6. 手动建立的 Release 不会自动补正文。运行固定工作流：

```powershell
gh workflow run 'Fork Release Notes' --repo bobo198504/Lertaro --ref main -f "tag=$tag"
$runId = gh run list --repo bobo198504/Lertaro --workflow 'Fork Release Notes' --limit 1 `
  --json databaseId --jq '.[0].databaseId'
gh run watch $runId --repo bobo198504/Lertaro --compact --exit-status --interval 10
```

   如果 `workflow_run` 自动实例在手动 Release 之前启动，会报 `release not found`；这正是重跑
   `workflow_dispatch` 的原因，不要删 Release 或重新打 tag。

7. 最终验收：

```powershell
gh release view $tag --repo bobo198504/Lertaro --json url,isDraft,isPrerelease,body,assets
gh api repos/bobo198504/Lertaro/releases/latest `
  --jq '{tag_name,html_url,draft,prerelease,published_at}'
```

   Release 必须非 draft、非 prerelease、正好六个资产；正文必须来自 `CHANGELOG.md` 且带本 Release
   四个主资产的 SHA-256。unsigned 包会被自动更新拒绝，这是 owner 已接受的行为，使用本版需手动替换。

### 3. 向上游提 PR

1. 只提交适合上游的改动。用户历史、机器路径、特定应用名、私人文件名、实际查询词、日志数值都不能进 PR；
   对外文字规则见下面同名章节。

   - 一个 PR 只解决一个主题；独立的修复/性能优化分开提，方便上游逐条接受或拒绝。
   - 上游 PR 默认不带 fork 的版本号、`CHANGELOG.md` fork 段、`AGENTS.md`、本机部署脚本、
     fork 更新源和 fork 专属排序/滚动；除非上游明确要求。

2. 从 `upstream/main` 建临时分支，不要从带 fork 私有行为的 `local/main` 直接开 PR：

```powershell
git fetch upstream --prune
git switch -c codex/<short-name> upstream/main
```

   主工作区有未收口改动时，用独立 worktree 从 `upstream/main` 开分支，不要覆盖当前工作。

3. 最小改动 + 对应测试；默认同时验证：

```powershell
& 'D:\Projects\Code\_tools\dotnet10\dotnet.exe' build Lertaro.slnx -c Release --nologo -v:q
& 'D:\Projects\Code\_tools\dotnet10\dotnet.exe' build Lertaro.Plugins.slnx -c Release --nologo -v:q
```

   再跑受影响的 `Tests/**` 工程。`dotnet test Lertaro.slnx` 是空跑，不算测试证据；testhost 的已知限制见
   下文环境章节。

4. 提交信息用中性、可搜索的 conventional 格式；不要带入本机数据。推送临时分支：

```powershell
git push -u origin HEAD
```

5. PR 指向 `Lertaro/Lertaro:main`，head 是 fork 的临时分支；标题和正文用中文，正文至少包含：
   问题、根因、改动、验证、兼容性/性能影响。不要让 `--fill` 代替人工检查：

```markdown
## 问题
## 根因
## 改动
## 验证
## 兼容性与性能
```

```powershell
gh pr create --repo Lertaro/Lertaro --base main `
  --head bobo198504:<short-name> `
  --title '<中文标题>' --body-file <中文正文文件>
```

6. 评审要求修改时，只在 PR 临时分支继续修；推送后检查 review/comments：

```powershell
gh pr view <number> --repo Lertaro/Lertaro --comments
gh pr view <number> --repo Lertaro/Lertaro --json reviews,reviewDecision,statusCheckRollup
```

   PR 分支可以 `git commit --amend` 后 `git push --force-with-lease`；已经发版并被 tag 锁定的主线提交不要改。

7. PR 被拒绝或未采纳时，先读具体理由再决定：能改成上游可接受的通用修复就继续；明确属于 fork
   使用习惯的改动可以保留在 fork，但不要把它伪装成上游已采纳，也不要因此删除 fork 的既有行为。

8. PR 推送完成后立刻切回 fork 主线：

```powershell
git switch local/main
```

   上游合并后再把实际合入的上游提交 cherry-pick/同步回 fork；不要假设 PR 内容已经进入
   `upstream/main`。fork 专属的滚动、排序和更新源不要顺手提交给上游。

## 搜索语义（已与 owner 逐条确认，**不要擅自"优化"**）
- 排序梯子（2026-10-25 定稿，**两侧同序**）：`历史/收藏`（有效分降序，`Count` 为主、时间只占 15% 且永不归零）→ **以下按匹配质量** `左侧连续`（Start==0 且连续度>=1）→ `Contiguity` → `覆盖率`（Weight/Contiguity）→ `Tier` → `TypeRank` → `IsDir` → `NameOrderComparer` → `Name.Length`（**名字长度在最后**）。
  - **匹配是门槛，使用次数是首要排序键**：能匹配到才进候选，进候选后按用过多少次排。这样短名字不再靠“覆盖率天然高”压掉用户天天用的长名字。
  - `Low` 段例外：`RankSection.Low` 是用户**显式**降权，历史不在该段参与比较 —— 显式指令优先于隐含统计。
  - 历史名额（H5 3 条 / H3 10 条）只用于历史行之间的分层与 Tail 判定，**不再是历史分能否参与排序的门槛**。
- 引擎键 `SearchResultRelevance.HighKey` 同步为：`contiguity(10) | start(6) | coverage(10) | tier(4) | folder(1)`。
- ⚠️ 多词查询**不再**强制 `Contiguity=0`（那会把空格查询的位置/覆盖率信号全抹平）：空格也按左侧优先排序。
- `NameOrderComparer`：数字 < 字母 < 符号；字母内**大写在前**；数字按数值（a2 < a10）。
- LINQ 里只能有一个 `OrderBy`，其余必须 `ThenBy`（`OrderBy` 会重置前面的排序）。

- 空格 = 分词 + **AND**；空格本身不可匹配；多词查询**保留**位置/覆盖率信号，按左侧优先排序。
- 混拼：只有完整读音或声母可作非末段；**只有韵母不参与**（`AliasMatchRules.AllowsMatchSpan*` + `PinyinTermMatcher`）。
- 内联卡片在以上规则之外再加三层目录邻近：当前目录 → 其子目录 → 全局。

## 版本、提交与发行
- 版本号在 `App/App.csproj`；`CHANGELOG.md` 一条一行、中英对应；**每个版本**必须含「+ 自动更新只检查本仓库的发行」与「+ 保留鼠标平滑滚动」。
  - ⚠️ **往已有的 `## v…` 段追加条目时，必须落在该段的代码块内部**（``` 之间），不能追加到块外。
    `fork-release-notes.yml` 会**原样投影**那一段，块外内容会变成正文里散落的文字（2026-10-19 踩过）。
    改完必须打印该段前后 30 行，确认每个条目都在块内、中英条目一一对应。
- `git commit` / `git push` 必须 owner 明确授权；部署、构建、测试不需要。
- **fork 主线分支 = `local/main`**（2026-10-20 从 `local/5.9.1.3` 改名而来）。分支名**不再带版本号**：它跟着上游一直往前走，
  版本只看 `App/App.csproj`。发布与 CI 只认 tag（`release.yml` 触发于 `push: tags: v*`），分支叫什么与发行无关。
  - 做 fork 的事只用 `local/main`；**提 PR 时临时切分支，推完立刻切回 `local/main`**（曾因停在 PR 分支上，每次读版本号都在错的分支上）。
- 回滚：`git reset --hard 1bff6429`（回滚后**也要重编插件**）；二进制备份 `D:\App protable\Lertaro.bak-5917`（5.9.1.7）。

## 工作方式
- 先拿事实再改（加计时/日志/单测复现），每改一处立刻构建；测试见上文（`Tests/**` 单跑，本机 testhost 目前需用 explorer 起的独立进程）。
- PowerShell 按行号改文件切坏过多次：优先整段/子串替换，改完必须打印上下文核对。
- 状态可疑就回退到上一个可用状态，不要在坏状态上叠加改动。
- 临时文件只放 `%TEMP%`；删除前先列内容核实绝对路径。

## UI 性能方案研究（2026-11-11 起持续待办）

- owner 已明确：**UI 要尽早优化**，但当前不锁定具体框架；会继续收集更好的方案。不要把 MyGo 或任何单一方案当成已定方案。
- 已初步评估 MyGo 0.8.3：原生 UI 是潜在候选（Go 视图、Direct3D 11、虚拟化长列表）；WebView 前端不适合作为性能方案。
- 当前迁移的主要阻碍不是渲染 API，而是 Lertaro 的插件 SDK、插件窗口和部分 Shell/Explorer 集成与 WPF 深度绑定。整体换 UI 不能只看单独窗口的帧耗时，也不能用 Go 重写搜索服务来替代 UI 评估。
- 任何候选框架先做独立 PoC，只接现有 C# 搜索结果，不改生产 UI。必须在本机 Windows 上对比：
  - 冷启动到首帧、按键到首行、标签切换重新查询的端到端延迟；
  - 高频连续输入、长列表滚动 p95/p99、结果页高亮和更新；
  - 工作集与峰值内存；
  - IME、高 DPI、多屏、焦点/全局快捷键、拖放、无障碍；
  - 插件窗口兼容路径。
- 先测量现有 WPF UI 占“按键到可见结果”总延迟的比例。若 UI 不是主要瓶颈，优先继续优化现有 WPF、桥接和结果交付，不为框架而迁移。

## 对外文字不写用户的具体数据（2026-10-20 用户明确要求）
- **PR 正文、issue、提交信息、更新日志里，一律不写用户真实搜索的词、机器上的应用名/文件名/目录名、日志或探针里的具体数值。**
  这些都不是技术论据，只是把用户的私人使用记录带进公开仓库（2026-10-20 一次 PR 正文把实际查询词、应用名和覆盖率数值写了进去，
  用户当场指出"下次注意"）。
- 用中性描述替换，并保留可检验的技术事实：用"一个短查询""某个只能靠拼音首字母命中的应用""覆盖率满值""覆盖率很低"
  这类说法；要说明因果时描述**形状**（如"名字与查询文本完全相同 → 覆盖率满值"），不要举例。
- 自己写完后 grep 一遍：反引号包住的短串、文件后缀、中文应用名、两位小数，都是可疑点。
- 已经推出去的提交信息若要改，用 `git commit --amend` 原地重写后 `git push --force-with-lease`（PR 分支可以改；
  已被 tag 锁定的主线提交不要动，改了要重打 tag 重发，代价大于收益）。

## 流程骨架（从改一行到可以发版；每一步都要给出**证据**，没有证据就写"未验证"）

0. **环境自检（每次新会话第一件事，只做一次）**
   `... build Tests\Core\Core.csproj --nologo -v:q` → 必须 exit 0（证明测试工程能编）。
   然后跑一次带 trx 的测试：
   `... test Tests\Core\Core.csproj --nologo -v:q --logger trx --results-directory %TEMP%\lertaro-trx`
   **只看 trx 的内容，不看 exit code**，三态判据（2026-10-19 实测）：
   · **无 trx / 无测试文件** → 命令根本没进测试阶段 ✗（参数或路径问题）
   · **trx 存在但 `total=0`，`ResultSummary/@outcome=Error`（"中止"）** → **testhost 起不来** ✗
     （本机当前状态：`Win32Exception (5) 拒绝访问` @ `ProcessManager.OpenProcess`，testhost 对 `vstest.console` 取的句柄打不开）
     → 明确写"本机测试环境不可用、未验证" + 给人工验证清单；**不要静默降级，也不要说"测试通过"**
   · **`total>0` 且 `failed=0`** → 真通过 ✓（这才是唯一可引用的"通过"证据）
   ⚠️ `dotnet test Lertaro.slnx` 与上面无关：`Lertaro.slnx` 没有测试工程，它是**空跑**，exit 0 永远不是证据。

1. **改动落点**：先 grep 到"真正的入口"（哪一行决定了这个行为），把 `文件:行号` 写进回复里。说不出入口就还没到能改的程度。

2. **改动**：最小改一处；有语义的地方（排序/匹配/范围）先写一条会红的单测，再改到它绿。

3. **编译**：`... build Lertaro.slnx -c Release --nologo -v:q` → exit 0（这是**唯一**能证明"代码能编"的证据）。
   另跑 `Lertaro.Plugins.slnx -c Release`；`Lertaro.slnx` 不含插件工程。

4. **测试**：按第 0 步的方式跑**受影响的测试工程**（`Tests/**`），给出 `total/passed/failed`。
   跑不起来 → 明确写"本机 testhost 不可用，未验证"，并说明用什么代替（人工步骤清单）。

5. **部署**：按「部署（本机便携安装）」的唯一入口执行 `Deploy-Local.ps1 -Full -Publish`。
   不要把旧的“手工同步插件三件套”再抄一遍。证据仍看部署日志末行、App/Service/Hook、ProductVersion 和哈希。

6. **人工验证**：把"用户该看到什么"写成 3~5 条可勾选的观察项（具体到查询词与文件名），交给 owner 确认。

7. **提交/发版（需 owner 明确授权）**：按「fork 更新、打包发布与上游 PR」执行；不要看到 tag 工作流
   被节流就以为发布失败，也不要忘了手动运行 `Fork Release Notes`。

8. **收尾**：删掉临时探针与 `%TEMP%` 草稿；把踩到的坑写回本文件（按**流程位置**写，不要按事故追加）。

## 待办补记（来自另一对话的实测，本机环境特有）

- 本机 `dotnet test` 起不来的可能原因：**本会话的沙箱/作业对象限制**（另一对话环境不同、可能正常）→ 所以第 0 步必须**实测一次**再下结论，别假定不可用。
- `Deploy-Local.ps1` 已包含插件 publish、旧 DLL 隔离、服务状态与版本自校验；不要再手工拆成多步。
- `~/.dsh/AGENTS.md` 里那份"测试命令固定用法"应**只留指针**指向本文件，避免两处规则分叉（该文件的修改需 owner 确认）。
- 工作区可能带着另一对话的**接缝修复**（`Core/Hook/Ipc/OpenedFolderSnapshotPublisher.cs` 的 `OrderByWindowRank/WindowRanks`）。
  它能让 `Tests/Core` 编译通过；**接受它，或先 `git checkout --` 退回再改** —— 不要在两份状态上叠改。

## 已修复（保留作历史）：`<4+ 字母的词> + e 开头的短词` 多词查询"失效"（2026-10-19 由待办③顺带修好）

owner 判定：太反常、影响面小、其它组合都正常 → **先记录，不动代码**。

**现象**：`lert exe` 这类查询只有 `exe` 生效，`lert` 那半被吞掉；`lert` 后面接更多字也一样。

**已验证的矩阵**（同一台机、同一版）：
```
lert            ✓ 单独能中
ler exe         ✓ 能中
ler ro          ✓ 能中
le aro          ✓ 能中
lert ro         ✓ 能中          ← 第一词 4 字母没问题
lert e          ✗ **不中**
lert ex         ✗ 不中
lert exe        ✗ 不中
lerta exe       ✗ 不中
lertar exe      ✗ 不中
```

**已排除**（有证据）：
- 解析阶段没问题：探针显示 `raw=lert exe → parsed=[lert] | [exe]` ✓，`raw=le exe → [le] | [exe]` ✓（**空格没被吃** ✗）；
- 第一词长度没问题（`lert ro` 能中 ✓）；
- 不是 `--no-build`/测试环境问题（与本条无关）。

**待定的一步**（下次接手第一件事）：搜 `ler e`
- 能中 ✓ → 阈值在"第一词 4 字母 + e 开头的短词"这个**组合**；
- 不能中 ✗ → 就是"**单字母第二词**"本身。

**最可疑落点**：`MixedQueryMatcher.TrySegmentPattern`（`Core/IndexV2/Search/SearchMatcherRow.cs:75` 的兜底分支）
把两个词**跨空格拼起来**（`lert e` → `lerte`），拼出来的串又恰好像拼音片段 → 被吞进别名通道 → 英文名没有别名 → 只剩 `exe` 命中。
`lert ro` 之所以正常，是因为 `lertro` 不像可用片段、退回普通 AND。

**附**：查询语法**没有通配符** ✗（`*` `?` 会被当普通字符）。位置语义（完全一样 → 前缀 → 包含 → 散落）是**自动**的，不需要写通配符。

7. **多词查询按输入顺序匹配**（owner 2026-10-19 要求）：`le app` 只能命中"le 在 app 之前"的名字，`apple` ✗ 不再命中。
   落点：`Core/SearchIndex/Fzf/FzfPattern` 或多词收口处校验各词 `MinBegin` 递增；约 20~40 行 + 单测。
   副作用（owner 已知悉）：`mp3 歌` 这类顺序写反的查询会失效。
## 已知行为：多词查询里的某个词可能被"剥词"（2026-10-19 记录，**暂不修**）

实测：`szz mp` 会返回 `D:\…\世的牽掛版本8.mp3`（名字与路径里**没有任何 `z`** ✗，不可能靠 `szz` 命中），
而单独搜 `szz` 能正确找到 `S:\…\树枝折断声.mp3` ✓。
推测 `DispatchSearch` 里 `FileFilterScopeResolver` 或 `PluginTriggerQuery.Strip` 把 `szz` 当成了过滤器/插件触发词剥掉，
真正搜索的只剩 `mp` → 于是任何 `.mp3` 都命中。

owner 判定（2026-10-19）：`szz` 单独搜能用、影响面小 → **暂不修**，不要再重开排查 ✗。
若以后要修：触发词/过滤器关键字应只在"**它是查询里唯一的前缀词**"时生效，而不是任意位置的首词。

