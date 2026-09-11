# 应用分身 AppClone — 增量架构设计（自动发现已安装软件 + 自动化 + 性能优化）

| 项 | 内容 |
|---|---|
| 文档类型 | 增量架构设计（仅描述本次新增/变更部分，配合 PRD 落地） |
| Language | 简体中文 |
| 项目名 | `appclone`（仓库 AppClone / 应用分身） |
| 技术栈 | C# WinForms + .NET 10（`src\AppClone\`），C 沙盒 DLL（`src\sandbox_dll\fenshen.c`，本次**不改**） |
| 基线 | 四层隔离（环境变量 / 注册表 / 多开解锁 / 设备分身）+ 分身管理，`scripts/test.sh` 15/15 通过 |
| 版本 | 增量版本 **1.1.0**（现为 1.0.0） |
| 撰写人 | 高见远（架构师） |
| 日期 | 2026-09-11 |
| 关联文档 | `docs/prd-incremental-2026-09-11.md`（需求唯一来源） |

---

## 0. 架构约束（铁律，工程师必须死守）

1. **零第三方依赖**：不新增任何 NuGet `PackageReference`。只用 .NET 10 BCL（`Microsoft.Win32.Registry` / `System.Text.Json` / `System.Drawing` / `System.Diagnostics` / `System.Threading.Tasks`）+ 原生 P/Invoke（`advapi32`/`shell32`/`user32`/`kernel32`/`dwmapi`）+ 系统 CLI（PowerShell / winget，均可降级）。`AppClone.csproj` 除现有 `..\Shared\Shortcuts.cs` 链接外**不得**新增 `PackageReference`（已核对现有 csproj 确认零引用）。
2. **向后兼容**：`StoreModel`（`apps` 数组 + `clones` 字典）结构**不得变动**；`AppEntry`/`CloneEntry` 仅**追加**带默认值的字段；反序列化容错（`.bak` 备份 + 空仓启动）保留；老 `apps.json` 直接加载、数据不丢。
3. **不破坏隔离**：沙盒 DLL 四层机制与 `scripts/test.sh` 15/15 不得破。本次**不修改** `src\sandbox_dll\fenshen.c`、`Core\Injector.cs`、`Core\RegClone.cs` 的核心隔离逻辑（P5 仅做"后台化 + 就绪标记跳过"，不动复制算法）。
4. **禁用 `Win32_Product`**：任何代码路径不得出现 `Win32_Product` / `Get-CimInstance Win32_Product` / `SELECT * FROM Win32_Product`。在 `AppDiscovery.cs` 顶部以 `#pragma warning` 注释 + 静态分析 grep 双保险（架构评审 `grep -ri "win32_product"` 必须零命中）。
5. **UI 全中文**，风格沿用 `Ui.cs`（`AppleCard` / `AppleButton` / `PillChip` + Apple 系统色板，`Microsoft YaHei UI`）。
6. **所有扫描 IO 必须后台线程**，UI 零阻塞、可取消（`CancellationToken`）；批量回填走 `Control.BeginInvoke`，批大小 = 20 条 / 100ms。
7. **不写入系统级持久变更**（注册表/快捷方式）除非用户显式确认；P1 的 R7 强制规则须提供一键还原并在卸载时还原。
8. **不降低冷启动速度**：新增功能默认不增加冷启动耗时（走缓存 + 懒加载 + 不阻塞启动路径）。

---

## 1. 实现方案与框架选型

### 1.1 总体架构

沿用现有单体 WinForms 结构（无 MVVM/MVC 框架，避免引入依赖与复杂度），在 `Core` 新增**无状态静态服务类**，在 `Forms` 新增 `DiscoverForm`。核心分层：

- **扫描引擎层** `Core\AppDiscovery.cs`：职责单一的扫描器，按源（S1/S2/S3 + P1 游戏库/UWP）并行/串行采集原始候选，**每个源带超时与 CancellationToken 包裹**。
- **识别层** `Core\AppClassifier.cs`：路径规范化、去重合并、分类词典打分、推荐三档标签。**全静态、纯函数**，便于单测且零状态。
- **缓存层** `Core\DiscoveryCache.cs`：扫描结果落盘 + 注册表指纹增量判定 + 损坏自愈。**文件 IO 用 `lock` 保护**。
- **规则层** `Core\LaunchRules.cs`：以规则表替代 `Launcher.BuildAutoArgs` 硬编码（R9）。
- **UI 层** `Forms\DiscoverForm.cs`：复用 `Ui.cs` 全部组件；扫描进度用新增 `Ui.RoundProgressBar`（见 §3）。
- **接入层** `MainForm.cs`：把"＋ 添加应用"按钮改为下拉，空态加"立即扫描本机应用"，状态栏加批量进度；`Store` 增加"从 DiscoveredApp 入库"的批量方法。

### 1.2 线程模型（关键，约束 6/8）

```
主线程(UI, STA)
  │  打开发现页
  ▼
DiscoverForm.OpenDiscover()
  ├─ 同步：DiscoveryCacheIO.Load() 读缓存 → 立即(≤0.3s) 渲染列表（缓存命中）
  └─ 异步：_cts = new CancellationTokenSource()
            Task.Run(() => AppDiscovery.ScanAsync(_cts.Token, progress))
               ├─ S1 注册表 Uninstall：RegEnumKey 原生枚举（≤300ms，主力先返回）
               ├─ S2 App Paths / S3 开始菜单.lnk：与 S1 并发（Task.WhenAll）
               ├─ 每源 WithTimeout(ms)：Task.WhenAny(source, Task.Delay(timeout)) 
               │     超时 → 记 SourceState.Timeout，跳过本源，不影响其它源
               └─ 逐批产出：AppClassifier.Merge 后，按 20 条/100ms 经 IProgress 上报
  ▲ DiscoverForm 的 Progress 回调内：Control.BeginInvoke(() => 追加卡片)
     → UI 线程仅做"加控件 + 局部 Invalidate"，单次 <16ms，0 阻塞
  └─ 关闭对话框 / 重新扫描：_cts.Cancel() → 后台 Task 在 ≤500ms 内退出（轮询点检查 IsCancellationRequested）
```

- **超时包装器**（通用工具，放 `AppDiscovery` 内 `private static Task<T> WithTimeout<T>(Task<T>, int ms, CancellationToken)`）：`Task.WhenAny(task, Task.Delay(ms, ct))`；若 delay 先完成则返回空结果并标记超时；同时向 task 发起 `ct.Cancel()` 尝试中止。
- **批量回填**：`AppDiscovery.ScanAsync` 内部维护累积缓冲，每满 20 条或每 100ms 调一次 `progress.Report(batch)`，避免一次性 BeginInvoke 几百个控件导致卡顿。
- **图标提取**：发现页统一 32px（`AppIcons.Extract(id, exe, 32)`），比 48px 少约 55% 内存；提取在 `Task.Run` 内并发，完成后 `BeginInvoke` **只更新对应那张卡片**（`PictureBox.Image` 替换），不整表重建。
- **内存释放**：发现页关闭即丢弃 `DiscoveryCache` 运行副本与未命中图标；`AppIcons` 内存缓存 LRU 上限 200 张（约束 P-7）。

### 1.3 缓存策略（R5）

- 落盘文件：`Paths.CacheDir + "discovered.json"`（新增 `CacheDir = Path.Combine(DataRoot, "cache")`）。
- Schema：`{ SchemaVersion:1, ScannedAt, MachineId, Stamps:{源→{KeyCount,LastWrite}}, Apps:[...] }`。
- **增量判定**：对 S1 三个 Uninstall 键，读取 `RegQueryInfoKey` 的 `LastWriteTime`（FILETIME）+ 子键数量，作为廉价指纹存 `Stamps`；二次打开时若指纹未变 → 跳过全量重扫、直接渲染缓存；变化则仅重扫变化键。
- **损坏自愈**：`JsonSerializer.Deserialize` 抛异常或 `SchemaVersion` 不符 → `File.Delete` 后全量重扫，不崩溃（与 `Store.Load` 同风格）。
- **关闭缓存开关**：设置项 `DisableDiscoveryCache`（默认 false=开启）；开启关闭后每次全量扫描，且不写 `discovered.json`。

### 1.4 复用现有资产（避免重复造轮子）

| 现有资产 | 本次复用点 |
|---|---|
| `Ui.cs`：`AppleCard`/`AppleButton`/`PillChip`/`Ui.*` 色板/`FadeAnimator`/`RoundWindow` | 发现页全部控件、进度条、圆角窗体、淡入 |
| `Core\AppIcons.cs` | 图标提取/加载（扩展缓存 + 32px） |
| `Shared\Shortcuts.cs`：`NativeLink` | 批量桌面快捷方式（R7/R8），复用 `Save()` 写 `--launch` 参数 |
| `Core\Launcher.cs` | `Launch()` 不变；仅 `BuildAutoArgs` 改为调 `LaunchRules.Resolve` |
| `Core\Store.cs` | 追加字段 + 新增批量入库方法 |
| `Core\RegClone.cs` | P5 仅"后台化 + 就绪标记跳过"，不改复制算法 |

---

## 2. 文件列表及相对路径

> 仓库根 `D:\fenshen`，下列路径相对 `src\AppClone\`（除跨项目项注明）。标注 **[new]** 新建 / **[modify]** 修改。

### 2.1 新建文件

| 路径 | 职责 | 阶段 |
|---|---|---|
| `Core\AppDiscovery.cs` **[new]** | 扫描引擎：S1/S2/S3 +（P1 Steam/Epic/UWP）。后台 Task、超时包裹、CancellationToken、批量上报、产出 `DiscoveryResult`。**严禁 Win32_Product** | M1 |
| `Core\AppClassifier.cs` **[new]** | 路径规范化 `NormalizeExePath`/`NormalizeName`、去重合并 `Merge`、分类词典 `Classify`、推荐三档标签。全静态纯函数 | M1 |
| `Core\DiscoveryCache.cs` **[new]** | `DiscoveredApp`/`DiscoveryCache`/`SourceStamp` 数据类 + `DiscoveryCacheIO`（Load/Save/NeedsRescan/损坏自愈）。`lock` 保护文件 IO | M1 |
| `Forms\DiscoverForm.cs` **[new]** | "发现本机已安装应用"对话框：搜索框、分类 PillChip 筛选、进度条、复选、批量添加入口、空/失败态。复用 `Ui.cs` | M1 |
| `Core\LaunchRules.cs` **[new]** | 启动参数规则表 `LaunchRule` + `LaunchRules`（内置默认 + `data\launch_rules.json` 用户扩展）+ `Resolve(exeName)`。替代 `Launcher.BuildAutoArgs` | M3 |
| `Core\GameLibrary.cs` **[new]** | Steam（`libraryfolders.vdf` v1 + `appmanifest_*.acf`）/ Epic（`Manifests\*.item`）扫描，作为 `AppDiscovery` 的可选源 | M3 |
| `Core\UwpScanner.cs` **[new]** | UWP 枚举（`Get-AppxPackage`，6s 超时可降级），产出 `Supported=false` 灰显条目 | M3 |
| `Core\AutoLaunchRule.cs` **[new]** | R7 强制规则：默认"替换桌面快捷方式"温和方案；可选"App Paths 接管"高级模式；`RestoreAll()` 一键/卸载还原 | M3 |
| `Core\Snapshot.cs` **[new]** | R10 分身快照：纯目录复制 `CloneDir` → `SnapshotsDir\<app>\<clone>\<timestamp>`，标记文件、>2GB 提示、回滚/删除 | M3 |

### 2.2 修改文件

| 路径 | 改什么 | 阶段 |
|---|---|---|
| `Core\Store.cs` **[modify]** | `AppEntry` 追加 `Source/Publisher/Version/Category/Recommended`（全默认值）；`CloneEntry` 追加 `Color`（默认 `""`）；`Store` 新增 `AddAppFromDiscovery(DiscoveredApp, int cloneCount, bool makeShortcut)` 批量入库（写 AppEntry 用 `DisplayName`、自动建 N 个 CloneEntry、并发提图标、可选建快捷方式）。**不改动** `StoreModel` 结构 | M1 |
| `Core\Paths.cs` **[modify]** | 新增 `CacheDir => Path.Combine(DataRoot,"cache")`；`EnsureAll()` 内 `Directory.CreateDirectory(CacheDir)`；新增 `SnapshotsDir`、`LaunchRulesFile`、`CategoriesFile`、`DiscoveryCacheFile` 访问器。**不改变** `DataRoot/StoreFile/IconsDir/ProfilesDir/SandboxDir` 既有行为 | M1 |
| `Core\AppIcons.cs` **[modify]** | 增加内存缓存 `ConcurrentDictionary<string,Image>` + LRU 200（`GetCached(id)`/`PutCache(id,img)`/`LoadAsync(id, Action<Image>)`）；`Extract(id, exe, size)` 支持指定尺寸（32/48）；`RefreshOne(id)` 仅更新单卡 | M1/M2 |
| `Ui.cs` **[modify]** | 新增 `RoundProgressBar : Control`（圆角进度条：底色 `FillGray`、进度 `Accent`、高 4 圆角 2），用于发现页；新增 `CloneColors` 静态类（6 色枚举 + `Resolve(string)`）。其余组件不变 | M1/M3 |
| `Core\Launcher.cs` **[modify]** | `BuildAutoArgs` 改为调 `LaunchRules.Resolve(app.ExePath, clone)`；行为与原 8 种 Chromium+Firefox **完全一致**（回归验证）；`ScanCommandLineMarker` 缓存由 5s 提至 10s 且仅 Job/主进程判定失败时走（P-3） | M2/M3 |
| `MainForm.cs` **[modify]** | "＋ 添加应用"改下拉（发现页/手动 exe）；空态加"立即扫描本机应用"按钮；状态栏加批量进度文案；接入 `DiscoverForm`；`RefreshAppList` 合并刷新 + 仅可见项懒加载（P-2）；`_timer`/`_pulse` 空闲暂停（P-3）；R8 右键改名/设色；R11 "结束全部"按钮；R10 重置前"先存快照"勾选 | M1/M2/M3 |
| `Program.cs` **[modify]** | 新增 `--restore-all-rules` 参数（R7 卸载/一键还原入口）；其余不变 | M3 |
| `AppClone.csproj` **[modify]** | 仅 `Version` 1.0.0 → 1.1.0；**不新增** `PackageReference`（约束 1） | M1 |

---

## 3. 数据结构与接口

> 完整 Mermaid 类图见 `docs/class-diagram.mermaid`。下列为契约要点（工程师实现依据）。

### 3.1 持久化数据模型（追加字段，全默认值）

```csharp
// —— Store.cs 内 AppEntry 追加（其余 5 字段不变）——
public string Source     { get; set; } = "manual";  // manual|uninstall|apppaths|startmenu|steam|epic|uwp
public string Publisher  { get; set; } = "";
public string Version    { get; set; } = "";
public string Category   { get; set; } = "";        // browser|social|game|dev|cloud|office|other|uwp
public bool   Recommended{ get; set; }

// —— Store.cs 内 CloneEntry 追加（其余 4 字段不变）——
public string Color      { get; set; } = "";        // ""=默认蓝；否则 CloneColors 中的十六进制
```

### 3.2 扫描缓存数据类（独立文件 `Core\DiscoveryCache.cs`，**不混进 apps.json**）

```csharp
public class DiscoveredApp
{
    public string Name           { get; set; } = "";  // 展示名（DisplayName，回退文件名）
    public string ExePath        { get; set; } = "";  // 规范化后的规范路径（主键）
    public string DisplayName    { get; set; } = "";  // 原始 DisplayName
    public string Publisher      { get; set; } = "";
    public string Version        { get; set; } = "";
    public string InstallLocation{ get; set; } = "";
    public string IconPath       { get; set; } = "";  // DisplayIcon 原路径（供 AppIcons 提取）
    public string Source         { get; set; } = "";  // uninstall|apppaths|startmenu|steam|epic|uwp
    public string Category       { get; set; } = "";  // 分类词典命中结果
    public bool   Recommended    { get; set; }        // 是否判为「推荐分身」
    public bool   Supported      { get; set; } = true;// UWP=false（灰显不支持）
    public DateTime FoundAt      { get; set; }
}

public class SourceStamp               // 增量指纹
{
    public int  KeyCount  { get; set; }
    public long LastWrite { get; set; } // RegQueryInfoKey 返回的 FILETIME（100ns）
}

public class DiscoveryCache
{
    public const int SchemaVersion = 1;
    public int  SchemaVersion_     { get; set; } = SchemaVersion;
    public DateTime ScannedAt      { get; set; }
    public string MachineId        { get; set; } = "";
    public Dictionary<string, SourceStamp> Stamps { get; set; } = new();
    public List<DiscoveredApp> Apps { get; set; } = new();
}
```

### 3.3 扫描引擎接口契约

```csharp
// Core\AppDiscovery.cs
public sealed record ScanProgress(int SourceIndex, int SourceTotal, string SourceName, int Scanned, int Total);
public enum SourceState { Ok, Timeout, Skipped, Error }

public sealed class DiscoveryResult
{
    public List<DiscoveredApp> Apps = new();
    public Dictionary<string, SourceState> Status = new();  // "uninstall"|"apppaths"|"startmenu"|...
}

public static class AppDiscovery
{
    // 主入口：后台线程执行；progress 每批(20条/100ms)上报；ct 可取消
    public static Task<DiscoveryResult> ScanAsync(
        CancellationToken ct,
        IProgress<ScanProgress>? progress = null,
        DiscoveryOptions? opt = null);   // opt 控制开关(Steam/Epic/Uwp/winget)，默认 Steam 开 Epic 关 Uwp 开 winget 关

    // 单源（内部）：均带 WithTimeout + ct
    // ScanUninstall / ScanAppPaths / ScanStartMenu / ScanSteam / ScanEpic / ScanUwp
}
```

### 3.4 识别层接口契约

```csharp
// Core\AppClassifier.cs
public static class AppClassifier
{
    // 单一规范化入口（共享知识：路径规范化唯一实现）
    public static string NormalizeExePath(string p);
    public static string NormalizeName(string n);

    // 去重合并 + 分类打分：raw 为各源原始候选；addedSet 为已添加应用的规范化路径集合
    // 返回已排序(推荐度→已添加沉底→名称)的 DiscoveredApp 列表
    public static List<DiscoveredApp> Merge(
        IEnumerable<DiscoveredApp> raw, HashSet<string> addedSet);

    // 单条分类：返回 (Category, Recommended, Tier)
    // Tier: "推荐分身" | "可尝试" | "不建议"
    public static (string Category, bool Recommended, string Tier) Classify(DiscoveredApp d);
}
```

### 3.5 启动参数规则表 Schema（R9，替代 `BuildAutoArgs` 硬编码）

```csharp
// Core\LaunchRules.cs
public class LaunchRule
{
    public string Id            { get; set; } = "";
    public string Family        { get; set; } = "None"; // Chromium|Firefox|Electron|None
    public List<string> Match   { get; set; } = new();  // 精确 exe 名(小写)，如 "msedge.exe"
    public List<string> MatchContains { get; set; } = new(); // 名称包含(小写)
    public string Args          { get; set; } = "";      // 模板，占位 {udata}/{ffprofile}
}

public static class LaunchRules
{
    public static readonly List<LaunchRule> BuiltIn = new()   // 内置默认（=原 8 种行为）
    {
        new() { Id="chromium", Family="Chromium",
                Match=["chrome.exe","msedge.exe","brave.exe","opera.exe","vivaldi.exe",
                       "chromium.exe","dragon.exe","360se.exe"],
                Args="--user-data-dir=\"{udata}\" --no-first-run" },
        new() { Id="firefox", Family="Firefox", Match=["firefox.exe"],
                Args="-profile \"{ffprofile}\" -no-remote" },
        // 用户扩展从 data\launch_rules.json 合并（P1 可编辑）
    };
    public static string Resolve(string exePath, string udata, string ffprofile);
    // 解析占位：{udata}=Paths.CloneUData(app,clone)，{ffprofile}=Paths.CloneFFProfile(app,clone)
    // Electron/CEF：命中 MatchContains 且目录下存在 resources/asar → 追加 --user-data-dir
}
```

### 3.6 `DiscoverForm` ↔ `AppDiscovery` 交互契约

| 动作 | 调用 | 说明 |
|---|---|---|
| 打开 | `new DiscoverForm(store).ShowDialog()` | 构造内同步读缓存并立即渲染；随后 `ScanAsync` |
| 重新扫描 | `form.Rescan()` | `_cts.Cancel()` → 新 `_cts` → 重新 `ScanAsync`（覆盖缓存） |
| 批量添加 | `form.AddSelected(List<DiscoveredApp> picked, int cloneCount, bool makeShortcut)` | 调 `Store.AddAppFromDiscovery(...)`，返回新增数 |
| 关闭 | `form.Close()` | `Dispose()` 时 `_cts.Cancel()`，丢弃运行副本 |

### 3.7 分身颜色枚举（共享 `Ui.CloneColors`）

```csharp
public static class CloneColors
{
    public const string Default = "#0A84FF"; // 蓝（Accent）
    public const string Green   = "#34C759";
    public const string Orange  = "#FF9500";
    public const string Pink    = "#FF2D55";
    public const string Purple  = "#AF52DE";
    public const string Gray    = "#8E8E93";
    public static Color Resolve(string hex);  // "" 或未知 → Default
}
```

---

## 4. 程序调用流程（时序图）

> 完整 Mermaid 见 `docs/sequence-diagram.mermaid`。关键三场景摘要如下。

### 4.1 ① 打开发现页 → 读缓存 → 后台扫描 → 分批回填 → 勾选 → 批量添加 → 批量建分身/快捷方式

```
User → MainForm: 点「＋ 添加应用 ▾ → 从本机已装应用添加」
MainForm → DiscoverForm: ShowDialog(store)
DiscoverForm → DiscoveryCacheIO: Load()
  alt 缓存命中且指纹未变
    DiscoveryCacheIO → DiscoverForm: DiscoveryCache  (≤0.3s)
    DiscoverForm → AppClassifier: Merge(cache.Apps, 已添加集合)
    DiscoverForm → User: 立即显示列表(缓存)
  else 无/过期
    DiscoverForm → User: 空态 + 进度条
DiscoverForm → AppDiscovery: ScanAsync(ct, progress)  [Task.Run 后台]
  par S1 注册表 Uninstall（主力,优先返回）
    AppDiscovery → AppDiscovery: RegEnumKey 三键 ≤300ms
  and S2 App Paths + S3 开始菜单.lnk（并发）
    AppDiscovery → AppDiscovery: 解析 .lnk(复用 Shortcuts 思路)
  AppDiscovery → AppClassifier: 逐批(20条/100ms) Classify+Merge+去重
  AppClassifier → DiscoverForm: 规范化+分类+推荐标签
  DiscoverForm → User: BeginInvoke 分批回填卡片（UI 0 阻塞）
AppDiscovery → DiscoverForm: DiscoveryResult(含超源状态)
DiscoverForm → DiscoveryCacheIO: Save(结果+指纹)
User → DiscoverForm: 勾选（默认勾「推荐分身」）+ 设分身数 N + 是否建快捷方式
User → DiscoverForm: 点「添加所选(N)」
DiscoverForm → Store: AddAppFromDiscovery(d, N, makeShortcut) ×N
  loop 每个选中应用
    Store → Store: 写 AppEntry(Name=DisplayName) + N×CloneEntry
    Store → AppIcons: Extract(id, exe, 32) [Task.Run 并发]
    opt 建快捷方式
      Store → MainForm: CreateShortcut(app, clone) [复用 NativeLink]
MainForm → Store: Save()
MainForm → MainForm: RefreshAppList() 即时刷新左侧
DiscoverForm → User: 关闭，状态栏「已添加 N 个应用」
```

### 4.2 ② 主界面冷启动性能改造（P-1/P-2/P-7）

```
Application.Run → MainForm(构造)
MainForm → Store: Load()  [同步 <50ms]
MainForm → MainForm: RefreshAppList() 仅渲染可见项
  loop 每个可见 AppCard
    MainForm → AppIcons: GetCached(id)  [内存命中 0 IO]
    alt 未命中
      AppIcons → AppIcons: LoadAsync → 后台 Extract 仅更新该卡(不整表重建)
MainForm → MainForm: 启动 _timer(1500ms)；_pulse 仅当有运行分身才起
MainForm → User: 首帧 ≤1.5s
Note: 空闲(0 运行分身)时 _timer 暂停/降频 5000ms；发现页扫描不进启动路径
```

### 4.3 ③ 运行状态轮询改造（P-3 空闲暂停）

```
_timer(1500ms) → MainForm: UpdateRunningStates()
MainForm → Launcher: IsRunning(app,clone) ×N
  Launcher: Job 活跃数 > 0 ? 主进程存活 ? 命令行标记(缓存10s,仅失败走)
alt 有运行分身
  Launcher → MainForm: 部分 true
  MainForm → MainForm: 更新 dot/state；启动 _pulse 呼吸动画
else 0 个运行分身
  Launcher → MainForm: 全 false
  MainForm → _timer: 暂停 或 降频至 5000ms
  MainForm → _pulse: 停止
```

---

## 5. 任务列表（有序、含依赖、按实现阶段）

> 三阶段：**M1(P0 核心)** / **M2(性能)** / **M3(P1)**。每条：编号 / 文件 / 做什么 / 依赖 / 验收。
> 验收口径对应 PRD 第 3 节 G1–G5、第 5 节 R1–R13、第 6 节 P-1~P-7。

### M1 — P0 核心（自动发现 + 批量添加 + 缓存）

**T01｜数据模型与目录基座** — `Store.cs`[modify]、`Paths.cs`[modify]、`AppClone.csproj`[modify]
- 做什么：`AppEntry`/`CloneEntry` 追加字段（§3.1，全默认值）；`Paths` 追加 `CacheDir`/`SnapshotsDir`/`LaunchRulesFile`/`CategoriesFile`/`DiscoveryCacheFile` 并在 `EnsureAll()` 建 `cache`；csproj 版本 1.1.0，**不新增 PackageReference**。
- 依赖：无。
- 验收：老 `apps.json`（无新字段）`Store.Load()` 直接加载、数据不丢；`grep Win32_Product` 零命中；`scripts/test.sh` 15/15 仍绿；`Paths.CacheDir` 目录被创建。

**T02｜图标内存缓存 + LRU + 32px + 异步加载** — `AppIcons.cs`[modify]
- 做什么：加 `ConcurrentDictionary<string,Image>` 缓存 + LRU 200（`GetCached`/`PutCache`/`LoadAsync(id, cb)`/`RefreshOne(id)`）；`Extract(id, exe, size)` 支持 32/48；未命中后台提取后**只更新单卡**。
- 依赖：无（T09 接入）。
- 验收：20 应用列表滚动磁盘读次数=0（全缓存命中）；补图标后无整表重建闪烁；LRU 上限 200。

**T03｜扫描引擎 AppDiscovery** — `Core\AppDiscovery.cs`[new]
- 做什么：S1 注册表 Uninstall（HKLM x64 / WOW6432Node / HKCU，原生 `RegEnumKey`）→ S2 App Paths（HKLM/HKCU）→ S3 开始菜单 .lnk（ProgramData + 用户，复用 `Shared\Shortcuts.cs` 解析思路）；`Task.Run` + `WithTimeout` + `CancellationToken`；逐批 20/100ms 经 `IProgress` 上报；产出 `DiscoveryResult`。**禁用 Win32_Product / 全盘扫描**。
- 依赖：T01（路径/目录）。
- 验收：3 台测试机含浏览器/微信/QQ/Office/VSCode 且不出现 VC++/Runtime/KB/驱动；每 exe `File.Exists` 100%；冷启动首屏 ≤1.5s、全量 ≤3s；单源超时跳过不抛异常；UI 拖动不掉帧；`grep -ri win32_product` 零命中。

**T04｜去重/分类/推荐 AppClassifier** — `Core\AppClassifier.cs`[new]
- 做什么：`NormalizeExePath`/`NormalizeName`（§7 规范）；`Merge` 主键 exe 规范化去重、次键 DisplayName 归一化、源优先级 S1>S2>S3、字段补全、已添加置灰；`Classify` 分类词典 + 排除规则 → 三档标签；按 推荐度→已添加沉底→名称 排序。
- 依赖：T01、T03（产出 `DiscoveredApp`）。
- 验收：同 exe 只出现一次；分类切换 ≤200ms 重绘；首屏前 20 条"值得分身"≥80%；「不建议」占全量 ≥30% 且不含高价值应用。

**T05｜扫描缓存 DiscoveryCache** — `Core\DiscoveryCache.cs`[new]
- 做什么：`DiscoveredApp`/`DiscoveryCache`/`SourceStamp` 类 + `DiscoveryCacheIO`（Load/Save/NeedsRescan/损坏自愈/关闭开关）；注册表指纹增量判定。
- 依赖：T01、T03、T04。
- 验收：二次打开 ≤0.3s 出列表；指纹未变跳过全量重扫；「重新扫描」覆盖缓存；损坏文件自动删重建不崩溃。

**T06｜发现对话框 DiscoverForm + 进度条** — `Forms\DiscoverForm.cs`[new]、`Ui.cs`[modify]
- 做什么：`DiscoverForm`（900×640，可缩放）复用 `Ui.cs`：`Ui.Bg`/`AppleCard`(R14)/`AppleButton`(Filled/Secondary)/`PillChip`(`ChipPurple` 系)；搜索框(≤150ms)、分类 PillChip 筛选、新增 `Ui.RoundProgressBar`、已添加置灰、全选/全不选、默认勾选推荐、空/失败态 + 重试；`FadeAnimator.FadeIn` + `Ui.RoundWindow`。
- 依赖：T02、T03、T04、T05。
- 验收：风格与 `Ui.cs` 一致；搜索/筛选即时；复选 + Ctrl/Shift 连选；≥500 条滚动无明显卡顿；关闭对话框扫描 ≤500ms 取消退出。

**T07｜批量添加 + 批量建分身/快捷方式接入** — `Store.cs`[modify]、`MainForm.cs`[modify]
- 做什么：`Store.AddAppFromDiscovery(DiscoveredApp, int cloneCount, bool makeShortcut)`：写 AppEntry（Name=DisplayName、带 Source/Publisher/Version/Category/Recommended）、自动建 N 个 CloneEntry、并发提图标（失败重试 1 次）、可选 `NativeLink` 建桌面快捷方式；主界面左侧即时刷新。
- 依赖：T01、T06。
- 验收：一次加 10 应用各 2 分身 ≤10s；`apps.json` 结构正确；名称取自 DisplayName；图标提取成功率≥95%；勾选后桌面 .lnk 双击可启动分身。

**T08｜主界面入口改造** — `MainForm.cs`[modify]
- 做什么："＋ 添加应用"改下拉（发现页/手动 exe，`ContextMenuStrip` 复用 Apple 色）；空态加"立即扫描本机应用"按钮；状态栏加批量进度文案（"正在添加 3 个应用…（2/3）"）。
- 依赖：T06、T07。
- 验收：入口可达；空态按钮打开发现页；批量操作状态栏文案正确。

### M2 — 性能（P-1~P-7）

**T09｜冷启动/列表懒加载/图标缓存接入** — `MainForm.cs`[modify]、`AppIcons.cs`[modify]
- 做什么：`RefreshAppList` 合并刷新入口、仅可见项懒加载、hover 动画不可见时停；图标走 `AppIcons.GetCached`/`LoadAsync`。
- 依赖：T02。
- 验收：20 应用冷启动 ≤1.5s（较原提升≥30%）；切换选中应用重建次数 2→1、≤50ms；滚动 0 磁盘读。

**T10｜运行状态轮询空闲暂停** — `MainForm.cs`[modify]、`Launcher.cs`[modify]
- 做什么：无运行分身时暂停 `_timer` 或降频 5000ms；`_pulse` 仅在有运行分身时启动；`ScanCommandLineMarker` 缓存 5s→10s 且仅 Job/主进程失败才走。
- 依赖：无（独立）。
- 验收：空闲 CPU <0.5%、定时器回调次数 0；3 运行分身 CPU<2%；无残留后台线程。

**T11｜注册表快照后台化 + 就绪跳过** — `RegClone.cs`[modify 最小]、`MainForm.cs`/`Launcher.cs`[modify 最小]
- 做什么：保持 `RegClone` 复制算法不变；`EnsureCloneRegistry` 增加"已就绪标记文件"跳过（幂等）；首次快照在 `NewCloneFlow` 已有 `await Task.Run` 基础上加"准备隔离环境…"状态文案；已就绪分身启动 ≈0 阻塞。
- 依赖：无（M2，不破坏隔离）。
- 验收：`scripts/test.sh` 15/15 仍绿；首次深度隔离启动 UI 无 >100ms 阻塞；二次启动同分身快照 ≈0。

### M3 — P1（自动化增强）

**T12｜启动参数规则表 LaunchRules** — `Core\LaunchRules.cs`[new]、`Launcher.cs`[modify]
- 做什么：内置默认规则（=原 8 种 Chromium+Firefox 行为）；`Launcher.BuildAutoArgs` 改调 `LaunchRules.Resolve`；`data\launch_rules.json` 用户扩展合并；Electron/CEF 探测 `resources/*.asar` 附加 `--user-data-dir`。
- 依赖：T01。
- 验收：新增规则无需改代码；现有 8 种行为**完全不变**（回归）；占位 `{udata}`/`{ffprofile}` 正确展开。

**T13｜UWP 灰显 + 游戏库扫描** — `Core\UwpScanner.cs`[new]、`Core\GameLibrary.cs`[new]、`AppDiscovery.cs`[modify]
- 做什么：UWP 经 `Get-AppxPackage`（6s 超时可降级）灰显 `Supported=false` + 原因文案；Steam（v1 `appmanifest_*.acf`，兼容 v2）+ Epic（`Manifests\*.item`）扫描；默认 Steam 开 / Epic 关 / UWP 开 / winget 关（设置可开）。
- 依赖：T03。
- 验收：UWP 灰显「暂不支持分身」无添加按钮；Steam 游戏入列、Epic 关时不出；winget 默认不跑。

**T14｜强制规则 R7 AutoLaunchRule** — `Core\AutoLaunchRule.cs`[new]、`Program.cs`[modify]、`MainForm.cs`[modify]
- 做什么：默认"替换桌面快捷方式"温和方案（不改 App Paths 注册表）；可选"App Paths 接管"高级模式（二次确认）；`RestoreAll()` 一键/卸载（`--restore-all-rules`）还原。
- 依赖：T01。
- 验收：设置后双击原图标进分身环境（`FENSHEN_CLONE` 可见）；关闭/卸载完整还原；无全系统启动副作用（温和方案默认）。

**T15｜分身快照与回滚 R10** — `Core\Snapshot.cs`[new]、`MainForm.cs`[modify]
- 做什么：`SnapshotsDir\<app>\<clone>\<timestamp>` 纯目录复制 + 标记文件；>2GB 提示；重置/删除前勾选"先存快照"；快照列表一键恢复。
- 依赖：T01（`SnapshotsDir`）。
- 验收：快照可恢复；占用 >2GB 提示清理；危险操作前建议快照。

**T16｜分身改名/颜色 R8 + 结束全部 R11** — `MainForm.cs`[modify]、`Ui.cs`[modify]
- 做什么：右键分身改名（默认 `应用名 - 分身N`）+ 6 色角标（`CloneColors`）；主界面加"结束全部"按钮（确认弹窗，一次结束全运行分身）。
- 依赖：T01（`CloneEntry.Color`）。
- 验收：改名/设色即时生效并持久化；"结束全部"一次结束所有运行分身并刷新状态。

**T17｜设置面板（缓存/扫描源/规则入口）** — `MainForm.cs`[modify]（或 `Forms\SettingsForm.cs`[new] 视空间）
- 做什么：开关"扫描缓存"、Steam/Epic/UWP/winget 扫描源、打开 `launch_rules.json`/`categories.json` 编辑入口。
- 依赖：T05、T12、T13、T14。
- 验收：各开关即时生效并持久化（落 `apps.json` 或独立 `settings.json`，不混进缓存）；关闭扫描缓存后每次全量且不写 `discovered.json`。

---

## 6. 依赖包列表

**无新增依赖。** 本次增量**零 NuGet `PackageReference`**。可用依赖仅：
- .NET 10 BCL：`Microsoft.Win32.Registry`、`System.Text.Json`、`System.Drawing.Common`、`System.Diagnostics`、`System.Threading.Tasks`、`System.Threading.CancellationTokenSource`。
- 原生 P/Invoke：`advapi32`（RegEnumKey/RegQueryInfoKey）、`shell32`（SHDefExtractIcon，已有）、`user32`/`kernel32`（已有）、`dwmapi`（已有）。
- 系统 CLI：PowerShell（`Get-AppxPackage`，UWP，6s 超时可降级）、winget（P2，默认关，8s 超时）。均优雅降级，缺失即跳过对应源。

> 已核对 `src/AppClone/AppClone.csproj`：现有 `ItemGroup` 仅链接 `..\Shared\Shortcuts.cs`，无任何 `PackageReference`。

---

## 7. 共享知识（跨文件约定，工程师必读）

1. **路径规范化唯一实现**：`AppClassifier.NormalizeExePath(string)` 为全局唯一入口（去引号 → `Environment.ExpandEnvironmentVariables` → `Path.GetFullPath` → `ToLowerInvariant()`）。发现去重、已添加比对（`Store.Model.Apps[].ExePath` 规范化）、强制规则比对**全部复用此函数**，避免多份实现漂移。
2. **名称归一化**：`AppClassifier.NormalizeName`：去版本号 `\d+(\.\d+)+`、去 `(x64)`/`64-bit`/`(x86)`/`32-bit`、去首尾空格、转小写、去空白字符；用于次键候选合并。
3. **分类常量**：`browser|social|game|dev|cloud|office|other|uwp`（字符串常量集中放 `AppClassifier.CategoryConstants`）。分类词典（浏览器/IM/办公/游戏/云盘/开发）与排除规则（SystemComponent/ParentKeyName/ReleaseType/微软运行时黑名单/名称含卸载等）写在 `AppClassifier` 顶部静态只读集合，便于 P1 `categories.json` 合并。
4. **颜色枚举**：`Ui.CloneColors` 6 色（蓝/绿/橙/粉/紫/灰），`CloneEntry.Color` 存十六进制串，`""`=默认蓝；UI 角标/头像用 `CloneColors.Resolve`。
5. **缓存 Schema 版本号**：`DiscoveryCache.SchemaVersion = 1`；`SchemaVersion_` 反序列化不符即视为损坏 → 删除重扫。
6. **线程安全设计**：`AppDiscovery`/`AppClassifier`/`LaunchRules`/`DiscoveryCacheIO` 均为**无状态静态类**（或静态方法），无实例字段，天然线程安全；`AppIcons` 内存缓存用 `ConcurrentDictionary` + `lock` 做 LRU 淘汰；`DiscoveryCacheIO` 文件读写用 `private static readonly object _ioLock`。
7. **批量回填批大小**：`Control.BeginInvoke` 每 **20 条 / 100ms** 一批（常量 `BatchSize=20`、`BatchIntervalMs=100`，放 `AppDiscovery`）。`IProgress<ScanProgress>.Report` 在后台线程调用，UI 侧回调内必须 `BeginInvoke`。
8. **取消契约**：每个 `DiscoverForm` 持有一个 `CancellationTokenSource _cts`；`FormClosing`/`Dispose` 调 `_cts.Cancel()`；重新扫描先 Cancel 旧任务再建新；后台源在每个 `await`/批量点检查 `ct.IsCancellationRequested`。
9. **超时常量**：winget 8000ms / AppX(UWP) 6000ms / 其余（注册表/App Paths/开始菜单/游戏库）3000ms。放 `AppDiscovery` 静态只读。
10. **源优先级**：`uninstall > apppaths > startmenu > steam/epic > uwp`（常量数组，用于 `Merge` 字段补全与候选保留）。
11. **图标尺寸**：发现页统一 32px；主界面卡片 40px 显示但提取仍用 48px（主库）；发现页提取 32px 以省内存。
12. **UI 语言**：全部硬编码中文；无多语言框架（Q10 默认全中文）。
13. **回归红线**：任何改动后必须 `bash scripts/test.sh` 15/15 通过；且老 `apps.json` 直接加载数据不丢。

---

## 8. 待明确事项（Q1–Q10 架构视角拍板默认值）

> PRD 第 11 节待确认问题，由架构师从技术风险/可行性角度给出**明确默认**，工程师按此落地；如需产品改默认值，后续可经设置项调整。

| # | 问题 | 架构视角默认决策 | 理由 |
|---|---|---|---|
| **Q1** | 扫描结果是否落盘缓存？ | **落盘** `data\cache/discovered.json`，仅存名称/路径/发布者/版本/分类（**不含**任何账号凭证）；**不上传**（无网络代码）；设置项 `DisableDiscoveryCache` 默认 **false（开启）**，可一键关闭 | 性能硬指标要求二次打开 ≤0.3s，必落盘；纯本地文件、内容无敏感凭证，风险可控 |
| **Q2** | 默认扫描含 UWP？ | **扫描并列出，但默认灰显「暂不支持分身」**（`Supported=false`），固定原因文案"UWP 应用无法注入 DLL，四层隔离失效"；无添加按钮 | UWP 装在受保护目录、须走 explorer 启动，注入 DLL 拿不到子进程句柄 → 隔离失效，按 PRD 结论灰显 |
| **Q3** | 默认启用游戏库？ | **Steam 默认开启**，**Epic 默认关闭**（manifest 格式稳定性差）；均在设置可开 | 游戏多开是高频场景，Steam v1 `appmanifest` 解析稳定；Epic `.item` JSON 偶有字段缺失，默认关更稳 |
| **Q4** | winget 是否纳入？ | **默认关闭**；架构预留 `AppDiscovery` 的 Winget 源（P2），本期不接；若开须 `--disable-interactivity` + 8s 超时 + 无 winget 优雅降级 | 慢、可能弹源协议确认导致挂起、可能联网，性价比低，P2 再做 |
| **Q5** | 绿色版/便携软件收录？ | **收录**（经 S2 App Paths + S3 开始菜单），`Source` 标 `apppaths`/`startmenu`；UI 用次级色 PillChip 提示"可能不完整" | 用户诉求是"消灭手找 exe"，绿色版也应可见；提示完整性存疑即可 |
| **Q6** | 分类词典可编辑？ | **P0 内置只读**；**P1 提供 `data/categories.json`** 用户自定义关键词，`AppClassifier` 启动时合并（内置为底，用户覆盖/追加） | 先保证默认体验正确，P1 再开放自定义，避免 P0 复杂度 |
| **Q7** | 批量默认创建几个分身？ | **默认 1 个**，下拉可选 1/2/3 | 保守，避免误建大量目录占空间；用户可显式加 |
| **Q8** | 强制规则 R7 改 App Paths 注册表？ | **默认"替换桌面快捷方式"温和方案**（不改 `App Paths` 注册表，避免影响全系统对该 exe 的启动且易还原）；提供可选"App Paths 接管"高级模式（需二次确认 + 卸载还原） | 改 App Paths 注册表会影响全系统启动该 exe、回滚成本高、有系统级副作用风险；温和方案零系统副作用、用户双击原图标即进分身，满足"自动化最高"诉求且安全 |
| **Q9** | 分身快照是否压缩？ | **P1 先做纯目录复制**（`DirectoryCopy` + 标记文件），不压缩；占用 >2GB 提示清理；压缩（系统 `tar.exe`/7z）留 P2 | 纯复制简单可靠、零依赖、可断点；压缩需外部工具或自研，成本高，先交付可用能力 |
| **Q10** | 是否英文界面？ | **本次全中文，不做多语言** | 用户群体为中文桌面用户，多语言框架增加依赖与复杂度，本期不做 |

---

## 9. 风险与回归要点

- **回归红线**：每次改动后 `bash scripts/test.sh` 必须 15/15；`AppClone.csproj` 不得出现 `PackageReference`；代码中不得出现 `Win32_Product`。
- **向后兼容**：`StoreModel` 结构不变；新字段全默认值；老 `apps.json` 直接加载。
- **隔离不被破坏**：不修改 `fenshen.c`/`Injector.cs`/`RegClone.cs` 核心；T11 仅做后台化与就绪跳过（不改复制算法）。
- **性能不退化**：新增扫描不进冷启动路径（首次打开发现页才跑）；图标内存缓存 LRU 200；轮询空闲暂停。
- **线程安全**：UI 控件只能经 `BeginInvoke` 访问；静态类无状态；缓存/文件 IO 加锁。

---

*设计结束。工程师按 M1→M2→M3 顺序、T01…T17 依赖关系落地；任何与本文档冲突处，以本文档为准并向架构师回执。*
