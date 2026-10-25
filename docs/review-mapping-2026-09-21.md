# 评审映射与整改方案：macOS App Clone 评审 → 本项目（Windows 应用分身）

- 日期：2026-09-21
- 被映射对象：`1-20182/app-clone`（macOS / SwiftUI）评审稿
- 本项目：`D:\fenshen`，AppClone（C# WinForms / .NET 10 + 沙盒 DLL，v1.2.0 基线）
- 文档性质：**外部评审的本地化裁定 + 可开工的 V1.1 方案**，不是产品 PRD

> **实施状态（2026-09-21 更新）**：P0 三项见 `docs/incremental-1.3.0-2026-09-21.md`；
> P1/P2 四项见 `docs/incremental-1.4.0-2026-09-21.md`。**F1–F7 全部落地**。
>
> | 项 | 状态 |
> |---|---|
> | F1 兼容性数据库 + 四态徽章 | ✅ 已实现（自学习 + 用户标记 + `data\compat.json`） |
> | F2 源应用更新检测 + 一键重建 | ✅ 已实现（基线回填 + 升级提示条 + 重建保留分身身份） |
> | F3 预检查 + Dry Run | ✅ 已实现（Dry Run 默认开，无头模式只做阻断级检查） |
> | F4 克隆管理中心 | ✅ 已实现（`Core/CloneOverview.cs` 装配层 + `Forms/CloneManagerForm.cs`，四维筛选 + 行内操作） |
> | F5 日志分级 + 结构化崩溃报告 | ✅ 已实现（`Core/Log.cs`，JSONL 流水 7 天轮转 + 崩溃报告 + 诊断包 + 回滚留痕） |
> | F6 角标图标落到快捷方式 / 任务栏 | ✅ 已实现（`Core/BadgeIcon.cs` 手写多尺寸 ICO；任务栏分组为实验性、默认关） |
> | F7 README 支持矩阵 / FAQ / 原理图 | ✅ 已实现（支持矩阵 + Mermaid 原理图 + 架构表 + FAQ + Security Notes + Roadmap） |
>
> 验证：`dotnet build -c Release` 0 错误（2 个既有警告）；`scripts\probe.cmd` **86 项断言全过**；
> `scripts\build.cmd` + `scripts\build_installer.cmd` 产出 `dist\AppClone.exe` 与 `dist\AppClone-Setup.exe`。

---

## 0. 结论摘要

1. 评审稿的质量不低，但它是按 **macOS .app 克隆流水线** 写的。本项目走的是**零复制的进程级隔离**路线，
   两边的技术断面几乎不重叠 —— 评审稿约 **1/3 的建议属于「不适用」**，而非「未做」。
2. 评审稿自排的五项优先整改里，**Clone Registry 与 Icon Badge 在本项目已经实现**（且实现深度更高：
   含 6 色角标 + 重命名 + 运行统计），**Batch Clone 也已实现**。照搬它的排序会导致重复劳动。
3. 真正值得吸收的是**产品面/管理面**缺口，本项目排出的优先级应为：
   **兼容性数据库 → 源应用更新检测 → 预检查 + Dry Run → 克隆管理中心 → 日志分级与崩溃报告**。
4. 本项目在隔离强度上显著领先评审对象（四层隔离叠加 + 设备指纹伪造 + 进程树 Job 跟踪），
   评审稿给出的「8/10 完成度、5/10 产品完成度」是对另一套代码的度量，不适用于本项目。

---

## 1. 逐条裁定表

判定口径：**已有** = 能力存在且可用；**部分** = 有形无实或缺关键环节；**缺失** = 不存在；**不适用** = 前提在本项目不成立。

| # | 评审条目 | 判定 | 本项目现状与证据 | 处置 |
|---|----------|------|------------------|------|
| A1 | Clone 生命周期缺失，无克隆记录 | **已有** | `StoreModel.Clones` 即注册表，分身含 Id/Name/DeepIsolation/DeviceClone/CreatedAt/Color/Hotkey（`Core/Store.cs:23-38`、`:40-44`）；另有 `CloneStats`（启动次数/累计秒数/最后启动，`Core/CloneStats.cs`，落盘 `stats.json`） | 仅需补「源应用版本快照」字段，见 F2 |
| A2 | 服务层职责不明确，建议拆 7 个 Service | **已有** | Core 已按职责拆为 20+ 个原子类（Store / Launcher / RegClone / Injector / AppDiscovery / AppIcons / Snapshot / CloneStats / Settings / LaunchRules / AppClassifier / DiscoveryCache / UWP Scanner / AutoStart / HotkeyManager / TrayContext / ConfigIo / Theme / Paths / GameLibrary）。仅 UI 编排集中在 `MainForm.cs`（~1560 行 / 53 方法） | 可选：MainForm 拆 `CloneMenuController` / `RightPaneBuilder`，优先级低 |
| A3 | Shell 命令耦合，建议抽象 CloneExecutor | **不适用** | 本项目不调用 `cp` / `PlistBuddy` / `xattr` / `codesign`；启动路径为「创建挂起进程 → 注入 DLL → 恢复」纯 Win32（`Core/Launcher.cs:120-245`、`Core/Injector.cs`）。唯一外部进程调用是 `taskkill /T /F`（`Launcher.cs:403`） | 折叠为「隔离层抽象 `IIsolationLayer`」，仅在引入 CLI/Dry Run 时做，见 F3 |
| B1 | 无克隆管理中心 | **部分** | 管理动作已齐：打开数据目录 / 桌面快捷方式 / 重命名 / 角标色 / 热键 / 深隔离 / 设备分身 / 结束进程 / 重置（先存快照）/ 快照管理 / 删除，全部集中在 `MainForm.cs:1376-1468` 的「管理」菜单。**缺**跨应用的总览视图与日志查看入口 | **F4** |
| B2 | 无更新同步（源应用升级后需重克隆） | **缺失** | `AppEntry.Version` 已从注册表 `DisplayVersion` 采集（`Core/AppDiscovery.cs:141`），但**从不比对**，克隆时刻的源信息也未留档 | **F2（最高价值）** |
| B3 | 无兼容性数据库 | **部分** | 已有三档推荐 `推荐分身 / 可尝试 / 不建议`（`Core/AppClassifier.cs:24-26`，判定见 `:103-135`）+ UWP 不可用 + 运行时/驱动黑名单 + README「已知边界」。**缺**逐应用 ✓/⚠/✗ 徽章界面展示，缺「实测结果自学习」沉淀 | **F1（最高价值）** |
| B4 | 无图标差异化 | **已有** | 分身头像 = 渐变圆角方块 + 序号（`MainForm.cs:952-975`）；6 色角标调色板（`Ui.cs:173` `CloneColors`，菜单 `MainForm.cs:1402-1410`）；分身可重命名（`:1389-1400`）。**唯一残留**：任务栏/快捷方式层仍是原 exe 图标 | **F6（低优先）** |
| B5 | 无批量克隆 | **已有** | `Store.BatchAddClones`（`Core/Store.cs:253-260`，上限 99）+ 批量条 `BatchNewClones`（`MainForm.cs:1288`） | 无需改动 |
| C1 | `cp -R` 性能一般，建议 ditto / APFS | **不适用** | 本项目不复制应用本体（`Launcher.Launch` 只建数据目录 + 注册表快照）。改为零复制后，该瓶颈不存在 | 不采纳 |
| C2 | 缺少克隆前预检查 | **部分** | 有 `ConfirmDestructive` 确认框（`Settings.cs:54`）、`_busy` 重入保护、目标 exe 存在性校验（`Launcher.cs:122`）、缺 DLL 自动降级并告警（`:191-204`）。**缺**磁盘空间 / 数据目录可写 / 权限的统一前置检查 | **F3** |
| C3 | 日志系统较弱 | **部分** | 有 `launch.log`（`Program.cs:42,116`）与 `crash.log`（`:68-77`）+ UI 线程异常兜底（`:37-46`）。**缺**分级（INFO/WARN/ERROR/DEBUG）、轮转、结构化字段 | **F5** |
| C4 | 无崩溃报告 | **部分** | 全局兜底已覆盖 `AppDomain.UnhandledException` / `TaskScheduler.UnobservedTaskException`（`Program.cs:52-66`）。**缺**结构化报告（版本 / 阶段 / 分身 / 堆栈）与一键导出 | **F5** |
| D1 | 首次启动门槛高（xattr -cr / Gatekeeper） | **部分** | 本项目为双击安装，无 Gatekeeper 类比项；对应痛点是**杀软对注入 DLL 的拦截**与深度隔离首次降级 | README FAQ + 首启自检（并入 F3） |
| D2 | README 缺支持应用列表 | **缺失** | 现有 README 有功能 / 四层隔离表 / 构建 / 验证结果 / 已知边界，**无支持矩阵** | **F7** |
| D3 | README 缺工作原理图 | **部分** | `docs/sequence-diagram.mermaid`、`docs/class-diagram.mermaid` 已存在，但 README 只以内嵌 ASCII 示意图呈现主界面，未内嵌流程原理图 | **F7** |
| E1 | 建议增加 Dry Run | **缺失** | 目前仅有「是否确认」二选一弹窗 | **F3** |
| E2 | 建议增加回滚记录 | **部分** | 失败自动清理、`Snapshot` 快照 + `RegClone.ResetCloneRegistry`。**缺**操作留痕（谁在何时改了哪个键 / 哪个文件） | **F5** |
| E3 | 建议增加签名检测预判可克隆概率 | **不适用** | 本项目无 codesign / Team ID / Entitlements / Sandbox 概念；对应物是「兼容性预判」，已并入 **F1** | 折叠入 F1 |

---

## 2. 五项优先整改在本项目的重排

| 评审稿排序 | 本项目实际排序 | 理由 |
|---|---|---|
| ① Clone Registry | 并入 F2 | 注册表本体已存在，缺的只是「源应用版本快照」字段 |
| ② Clone Manager | **第 4 位（F4）** | 已有单应用维度的完整管理菜单，跨应用视图是体验增量而非痛点 |
| ③ Compatibility Database | **第 1 位（F1）** | 用户「试了才知道能不能分身」的痛点真实存在，且可自学习沉淀 |
| ④ Auto Rebuild | **第 2 位（F2）** | 源应用升级后分身行为静默失效，是隐性故障，属正确性问题 |
| ⑤ Icon Badge | 已有，降为 F6 | 序号 + 6 色角标 + 重命名已实现，仅任务栏层残留 |

---

## 3. V1.1 建议方案（可直接开工）

新增文件一律置于 `src/AppClone/Core/` 或 `src/AppClone/Forms/`，遵守既有硬约束：**零 PackageReference、不改 `Injector` / `RegClone` / `fenshen.c` 隔离核心、新增数据字段全部带默认值**。

### F1 兼容性数据库 + ✓/⚠/✗ 徽章（P0）

新增 `Core/CompatibilityDb.cs`，落盘 `data/compat.json`（`Paths` 增加访问器，`Paths.cs:22-29` 同区域追加）。

```csharp
public enum CompatLevel { Unknown, Supported, Partial, Unsupported }

public class CompatEntry
{
    public string Match { get; set; } = "";              // exe 文件名（小写）或 publisher 关键词
    public CompatLevel Level { get; set; } = CompatLevel.Unknown;
    public string Note { get; set; } = "";
    public string Source { get; set; } = "seed";         // seed | learned | user
    public int SuccessCount { get; set; }
    public int FailCount { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public static class CompatibilityDb
{
    public static CompatLevel Resolve(AppEntry app, out string note);
    public static void Report(string exePath, bool ok, string? reason = null);
}
```

- **内置种子**：复用 `AppClassifier.CategoryRules` 词典与 `LaunchRules` 命中结果 —— Chromium/Firefox 系（有官方多实例通道）→ `Supported`；UWP（`Supported=false`）→ `Unsupported`；分类为 `other` 且无规则命中 → `Unknown`。
- **自学习钩子**：`Launcher.Launch` 成功记录 `StartedAt`，新增 `Core/LaunchWatch.cs` 在启动后 8s 观察窗口内检测进程是否已退出；异常早退 → `Report(exe, false, "启动后立即退出")`；连续 3 次失败 → 自动降为 `Unsupported` 并在状态栏提示可反馈。
- **UI 展示**：`MainForm.RefreshAppChips`（`:363`）与 `MakeCloneCard`（`:943`）各加一枚 `PillChip`（`Ui.cs` 已有 `AccentLightOf / DangerOf / FillGrayOf` 三色可用）：
  - `✓ 已测可用` → Accent 浅底
  - `⚠ 部分可用` → 警示色
  - `✗ 暂不支持` → Danger 色
  未收录显示中性 `— 未测试`。悬停 ToolTip 显示 `Note`。
- **手动反馈**：克隆管理菜单增加「标记为可用 / 部分可用 / 不支持」，写 `Source="user"` 并优先于自学习结果。

### F2 源应用更新检测 + 一键重建（P0）

`CloneEntry` 增字段（全部带默认值，老数据不丢）：

```csharp
public string SourceVersionAtCreate { get; set; } = "";
public string SourceExeMtimeAtCreate { get; set; } = "";   // ISO 8601
public long   SourceExeSizeAtCreate  { get; set; }
```

新增 `Core/SourceWatch.cs`：

```csharp
public enum SourceChangeKind { None, VersionUpgraded, ExeReplaced, PathMissing, PathChanged }
public static SourceChangeKind Check(AppEntry app, out string currentVersion, out string reason);
```

- **检测时机**：`Program.Main` 启动后（后台 Task，不阻塞 UI）+ `MainForm.SelectApp` 切应用时。
- **版本来源优先级**：注册表 `DisplayVersion`（复用 `AppDiscovery` 的读取逻辑）→ `FileVersionInfo.GetVersionInfo(exe).FileVersion` → 文件 mtime + size 兜底（用于手动添加、无版本号的应用）。
- **UI**：右栏顶部新增可折叠 banner：「源应用已从 4.0.1 升级到 4.1.0，克隆记录仍是旧版本」+ 两个动作：
  - `仅更新记录` → 刷新三个快照字段，不清数据（适用于数据向下兼容的升级）
  - `重建分身数据` → 逐分身走既有 `ResetWithSnapshot`（`MainForm.cs:1491`，先存快照再清空），**保留 Id / Name / Color / Hotkey / 隔离开关**（即保留分身身份与设备指纹）
- **注意**：Windows 语义下「重建」不等于「重新复制应用」，只是让分身带着新的源版本基线 + 干净数据重新开始。

### F3 预检查 + Dry Run（P0）

新增 `Core/Preflight.cs`：

```csharp
public record PreflightItem(Severity Level, string Title, string Detail, bool Blocking);
public static class Preflight
{
    public static List<PreflightItem> Check(AppEntry app, CloneEntry? clone = null);
}
```

| 检查项 | 判定 | 阻断 |
|---|---|---|
| 数据目录所在卷剩余空间 | `< 2 GB` 告警（阈值写入 `Settings`） | 否 |
| `sandbox\inject64.dll`（或 `inject32.dll`）存在且位数匹配 | 缺失 → 将降级为标准隔离 | 否 |
| 目标 exe 存在 | 缺失 | **是** |
| 分身数据目录可写 | 试写临时文件 | **是** |
| 深度隔离开启但注册表快照未就绪 | 提示将降级 | 否 |
| 当前是否管理员 | 部分应用（服务型）需要 | 否 |

接入点：`NewCloneFlow`（`:628`）、`BatchNewClones`（`:1288`）、`LaunchClone`（`:1198`）、`Program.LaunchHeadless`（`:79`）。
`Blocking` 项不通过即中止并弹窗；非阻断项汇总为一张清单，用户确认后继续。

**Dry Run**：新增 `Forms/DryRunForm.cs`，在真正启动前展示将执行的操作快照（JSON 直读形式）：

```
环境变量：USERPROFILE → data\profiles\<appId>\<cloneId>\home
          APPDATA / LOCALAPPDATA / TEMP / TMP / HOME 同上重定向
注册表：  HKCU → 私有键 <RegClone.EnvSubKey>（快照 3 项 / 共 N 键）
注入：    sandbox\inject64.dll
设备分身：COMPUTERNAME → DESKTOP-XXXXXXX，MachineGuid / 卷序列号 重定向
附加参数：LaunchRules.Resolve 结果（如 --user-data-dir=...\udata）
```

由 `Settings` 新开关 `ShowDryRun`（默认关，首启向导中询问）控制。

### F4 克隆管理中心（P1）

新增 `Forms/CloneManagerForm.cs`，与既有 `SnapshotsForm` / `DiscoverForm` 同级同风格：

- 顶部筛选：应用下拉 + 状态（运行中 / 未运行）+ 兼容性三态 + 文本框搜索。
- 主体 `ListView`（Details，全中文表头）：应用 / 分身 / 隔离 / 设备分身 / 兼容性 / 创建于 / 启动次数 / 累计时长 / 最后启动 / 状态。
- 数据源：`Store.Model.Apps × ClonesOf` 联表 `CloneStats.Get` 与 `CompatibilityDb.Resolve`。
- 行内操作：启动 / 停止 / 打开数据目录 / 重建（F2）/ 删除。
- 入口：顶栏「⋯」下拉 + 托盘菜单。
- 附带「查看日志」→ 打开 `data\logs\` 目录并按当前分身过滤（依赖 F5）。

### F5 日志分级 + 结构化崩溃报告（P1）

新增 `Core/Log.cs`：

```csharp
public enum LogLevel { Debug, Info, Warn, Error }
public static class Log
{
    public static void Write(LogLevel level, string area, string message,
        string? appId = null, string? cloneId = null, string? step = null, Exception? ex = null);
    public static string LogsDir { get; }        // data\logs\
}
```

- 落盘 `data\logs\ops-YYYYMMDD.jsonl`（每行一个对象：`ts / level / area / appId / cloneId / step / message / error`），保留 7 天自动清理。
- **回滚记录**：`Store.RemoveClone` / `RegClone.DeleteCloneRoot` / `Snapshot.Restore` / `ResetWithSnapshot` 四处写入 `step="rollback"` 记录，形成可追溯链路。
- **结构调整**：`Program.WriteCrashLog`（`:68-77`）改为写 `data\logs\crash-<时间戳>.json`，字段含 应用版本 / 当前主界面选中应用 / 分身 / 最后一条操作日志 / 异常堆栈；设置面板增加「导出诊断包」（打包 logs + apps.json + settings.json，剔除任何密钥类内容）。
- 保留 `launch.log` / `crash.log` 一段时间做向后兼容，README 注明迁移关系。

### F6 角标图标落到快捷方式与任务栏（P2）

- 新增 `Core/BadgeIcon.cs`：用 `Graphics` 在应用图标右下角绘制序号圆点（复用 `CloneColors.Resolve(clone.Color)`），输出 `data\icons\badge-<cloneId>.ico`（含 16/32/48/256 多尺寸）。
- `MainForm.CreateShortcut`（`:1517`）的 `IconPath` 由 `app.ExePath` 改为该 ico → 桌面图标可区分。
- 启动时调用 `SetCurrentProcessExplicitAppUserModelID` 或为快捷方式设置 AppUserModelID，使多分身任务栏图标可分组区分（需实测各大版本 Windows 行为，标注为实验性开关，默认关）。

### F7 README / docs 增补（P2）

- 新增 **支持应用矩阵**（Edge / Chrome / 微信 / QQ / Telegram / Discord / Steam / Trae / VS Code = ✅；Arc 类混合沙盒 = ⚠；UWP / 硬编码绝对路径应用 = ✗），并注明「矩阵由兼容性数据库导出，可随实测更新」。
- 新增 **原理流程图**：把 `docs/sequence-diagram.mermaid` 渲染为 SVG 内嵌 README（对应评审稿 D3）。
- 新增章节：`FAQ`（为什么某些应用不能分身 / 为什么被杀软误报 / 为什么单实例限制没破 / 为什么第一次启动慢）、`Security Notes`（注入 DLL 的作用面与不做什么）、`Roadmap`（即本文第 3 节）、`Architecture`（Core 类职责表）。

---

## 4. 明确不照搬的条目

| 评审建议 | 不采纳原因 |
|---|---|
| `cp -R` → `ditto` / APFS `cp -c` 提速 | 本项目零复制，无此瓶颈 |
| BundleID 自动生成 / 冲突检查 | Windows 无 BundleID 概念；对应物是数据目录与注册表私有键，已由 `Paths` + `RegClone` 天然隔离 |
| `xattr -cr`、Gatekeeper 首次启动向导 | 平台特有机制；本项目对应痛点是杀软拦截，另见 D1 |
| `codesign` 重签名 / Team ID / Entitlements / Sandbox 分析 | Windows 无签名验证环节；折叠为兼容性预判（F1） |
| `protocol CloneExecutor` 的 shell 命令抽象 | 无 shell 依赖；若有 CLI 化需求，应抽象为 `IIsolationLayer`（环境变量层 / 注册表层 / DLL 注入层），而非 shell 命令层 |
| 「复制 → 改 ID → 去属性 → 重签名 → 校验」生命周期模型 | 与本项目「建目录 → 快照注册表 → 注入 → 启动」模型不同构，强行套用会扭曲设计 |
| 完成度打分（8/10、5/10 等） | 度量维度不同，本项目在隔离强度维度上超出评审对象 |

---

## 5. 实施约束（沿用仓库既有铁律）

1. **数据兼容**：`AppEntry` / `CloneEntry` 新增字段一律带默认值，老 `apps.json` 反序列化后新字段取默认，数据零丢失。
2. **零依赖**：不得引入 `PackageReference`。
3. **隔离核心冻结**：`Core/Injector.cs`、`Core/RegClone.cs`、`src/sandbox_dll/fenshen.c` 不做改动（F1 的自学习钩子只读启动结果，不侵入注入链路）。
4. **UI 写法**：`Ui.Label(...)` 是工厂方法而非构造函数，禁止 `Ui.Label(...) { ... }` 对象初始化器写法（历史曾因此产生 42 个级联编译错）。
5. **枚举限定名**：`Ui` 类内存在同名 `Theme` 属性，需引用枚举时写 `Core.Theme.X`。
6. **编辑纪律**：同文件多处修改必须串行或整文件 `Write`，改完逐项 grep 复核（本仓库多次出现「Edit 报成功但未落盘」）。
7. **构建前置**：构建前确认 `AppClone.exe` 未在运行，否则报 MSB3027。

---

## 6. 验收清单

- [ ] `dotnet build -c Release` → 0 错误（允许既有 2 个警告）
- [ ] 用 v1.2.0 的 `apps.json` 载入 → 应用与分身数量不变、新字段为默认值
- [ ] 应用卡与分身卡正确显示 ✓ / ⚠ / ✗ / — 四态徽章
- [ ] 人为修改源应用版本号 → 启动后出现升级提示；「仅更新记录」与「重建分身数据」两条路径均正确
- [ ] 磁盘空间不足 / DLL 缺失被预检查捕获并给出明确提示
- [ ] Dry Run 输出与 `Launcher.Launch` 实际行为一致（逐字段比对）
- [ ] 管理中心表格行数 = `Store.Model.Clones` 全量之和，行内操作全部可用
- [ ] `data\logs\` 生成当日 jsonl，含 rollback 记录；崩溃报告可导出
- [ ] `bash scripts/test.sh` 隔离能力 15 项断言仍全部通过（隔离核心未动，应无回归）
