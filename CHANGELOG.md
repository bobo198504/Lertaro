# 更新日志 · Changelog

本仓库（fork）的本地发行记录。**每次更新中英双份。**
Local release log of this fork. **Every entry is bilingual.**

---

## 5.8.4.2 — 2026-10-06

**中文**

- 平滑滚动改用 **模型 3.0（MODEL 3.0）**：收多少发多少、只把时间摊开；每个收到的量开一个窗口，窗口重叠相加，总量**精确守恒**，与速度无关。
- 新增**应用侧规则**：速度预算决定"一格走多远"（出厂 `SlowStep=5` / `RampUp=1000` / `TopSpeedMul=1.0` ✓）、release 模型决定窗口长度（单格 `GlideMs`，滚动中再加固定 `ReleaseMs=200ms`）、缓动形状用应用自己的规则（不平铺时全缓动）。
- 接递层用**两条轴**（lone / roll 各固定窗口长度），避免滚动起步时窗口被重新摊开造成的顿挫。
- **格子滚轮与自由滚轮走缓动**；**触控板不缓动**，单独按 `TouchpadSpeed` 降速（WPF 把每条滚轮消息都当整行，原生放行会过快）。
- **设备分类**：OS 触摸标记 `0xFF515700` 优先；无标记时按子格幅值的规律性区分自由滚轮与触控板。
- 修复：服务持有 `Data\Machine` 后 App 读不到 `machine-settings.json`，改由服务经管道提供（已提上游 **PR #332**）。
- **版本号规则**：本仓库版本号**不得高于上游已发布的版本线**；上游已发布 5.8.4，故本版为 **5.8.4.2**。

**English**

- Smooth wheel scrolling now uses **MODEL 3.0**: take how much, give how much, spread over a stated time. One window per received amount, overlapping windows add up, and the total is **exactly conservative** whatever the speed.
- Added the **app-side rules**: the speed budget decides how far a notch travels (`SlowStep=5` / `RampUp=1000` / `TopSpeedMul=1.0`), the release model decides the window (the Glide setting alone for a lone message, plus a fixed `ReleaseMs=200ms` inside a roll), and the payout shape is the app's own rule (fully eased unless the windows tile).
- The delivery layer runs **two axes** (lone and roll, each with a fixed window length), so a roll's start cannot re-spread windows already in flight.
- **A notched mouse and a free-spinning wheel are eased; a touchpad is not** -- it is scaled by `TouchpadSpeed`, because WPF treats every wheel message as a whole line step.
- **Device classification**: the OS touch marker `0xFF515700` wins; without it, the regularity of the sub-notch magnitudes separates a free-spinning wheel from a touchpad.
- Fixed: the App could not read `machine-settings.json` once the service held `Data\Machine`; the service now hands its copy over the pipe (upstream **PR #332**).
- **Version rule**: this fork never numbers itself above the released upstream line. Upstream has released 5.8.4, hence **5.8.4.2**.

---

## 早期本地版 / Earlier local releases

- **5.8.2.1** — 本机在 5.8.2 基线上运行的版本（内联搜索 scope 时序、对话框跟随与定位、直接子项排序、Core 测试基础设施）。The tree this machine ran on the 5.8.2 line.
