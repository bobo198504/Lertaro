# 更新日志

<!--
格式：一条一行、符号标性质、中英双份、外层用代码块包住。
符号：* 核心功能　+ 小功能添加　# 修复。只写"改了什么"。
-->

## v5.8.4.2

## 更新内容

```
* 平滑滚动改用模型 3.0（收多少发多少，只把时间摊开）
+ 新增应用侧规则：速度预算决定一格走多远、窗口长度由 release 模型决定、缓动形状用应用自己的规则
+ 滚动的接递改成两条轴（单格 / 滚动中），各自固定窗口长度
+ 触控板不缓动，按 TouchpadSpeed 单独降速
+ 新增设备分类：OS 触摸标记优先，其次按子格幅值的规律性
# 修复 App 读不到 machine-settings.json（改由服务经管道提供）
+ 版本号 v5.8.4.2（不高于上游已发布的版本线）
```

## What's changed

```
* Smooth wheel scrolling now uses model 3.0 (take how much, give how much, spread over a stated time)
+ New app-side rules: the speed budget sets how far a notch travels, the release model sets the window
  length, and the payout shape is the app's own
+ The scroll delivery now runs two axes (lone / rolling), each with a fixed window length
+ A touchpad is not eased; it is scaled down by TouchpadSpeed
+ New device classification: the OS touch marker first, then the regularity of the sub-notch magnitudes
# Fixed the App being unable to read machine-settings.json (the service hands its copy over the pipe)
+ Version v5.8.4.2 (never above the released upstream line)
```

## v5.8.2.1

## 更新内容

```
+ 内联搜索：召唤时读实时目录，目录到位后重跑搜索
+ 内联搜索：结果里直接子项靠左命中优先，同位置时目录优先
+ 对话框跟随：打开/保存对话框自动定位到当前文件夹
+ 定位：非资源管理器宿主走它自己的适配器
+ Core 测试工程改用 Microsoft.Testing.Platform
```

## What's changed

```
+ Inline search: reads the live folder when summoned and re-runs the search once it arrives
+ Inline search: a direct child that matches at the left wins, and a folder wins at the same position
+ Dialog follow: an open/save dialog locates to the current folder by itself
+ Locating: a non-Explorer host goes through its own adapter
+ The Core test project runs through Microsoft.Testing.Platform
```
