# 应用分身 AppClone v1.2.0 增量架构设计 + 任务分解

> 架构师：高见远（software-architect-2）
> 硬约束：零第三方依赖（仅 .NET 10 / net10.0-windows 官方库）；复用 Ui.cs 基建；全中文文案；沙箱核心（Launcher.cs / Injector.cs / RegClone.cs / fenshen.c）不改动。
> 范围：用户拍板 = P0（设置面板 + 界面美化 + 深色模式）+ 全量 P1（主界面搜索/标签筛选、现有分身批量操作、托盘菜单+开机自启、启动热键+运行统计+配置导入导出）。

---

## 1. 实现方案 + 框架选型

**框架选型结论：保持纯 WinForms（net10.0-windows + UseWindowsForms），不引入任何第三方库。**

- **主题系统**：新增 `Core/Theme.cs`（`Theme` 枚举 + `ThemeRole` 颜色角色 + `ThemeManager` 静态管理器，持有 `Current` 与 `ThemeChanged` 事件）。`Ui.cs` 扩展 Dark 色板与字体层级常量，新增 `Ui.ThemedColor(ThemeRole)` 统一取色 API。既有 `AppleButton/AppleCard/PillChip/RoundProgressBar` 改为在 `OnPaint` 中读取 `Ui.ThemedColor(...)`，并在构造函数订阅 `ThemeManager.ThemeChanged` 触发 `Invalidate()`。主界面/设置面板在 `ThemeChanged` 中调用 `Ui.ApplyTheme(root)` 重设静态控件底色并重绘。
- **设置面板**：新建独立窗口 `Forms/SettingsForm.cs`，左侧分组导航（7 组），右侧按组切换子面板。所有设置行用内嵌静态工厂 `SettingRow.Make(...)` 复用 `AppleCard`+`AppleButton`+`PillChip` 拼装；改动即时写 `Settings.Current.*` 并 `Settings.Save()`，主题项额外调 `ThemeManager.Set(...)` 触发全局重绘。
- **托盘**：新建 `Core/TrayContext.cs` 封装 `NotifyIcon` + 菜单（打开/新建/最近启动/退出），支持 `TrayShowCount` 在悬浮文字显示运行数。
- **开机自启**：新建 `Core/AutoStart.cs`，写/读 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`。
- **全局热键**：新建 `Core/HotkeyManager.cs`，用 `RegisterHotKey/UnregisterHotKey` P/Invoke；`MainForm` 覆写 `WndProc` 捕获 `WM_HOTKEY(0x0312)` 转发。
- **运行统计**：新建 `Core/CloneStats.cs`，内存计数（启动次数/累计秒数/最后启动时间），落盘 `data/stats.json`。
- **配置导入/导出**：新建 `Core/ConfigIo.cs`，单分身导出 `{app, clone}` JSON；全量导出为 JSON 数组（不打包 zip）；导入重建 ID 并复制数据目录。

---

## 2. 文件列表（新增 vs 修改）

### 新增文件（7）
| 文件 | 作用 |
|---|---|
| `Core/Theme.cs` | `Theme` 枚举、`ThemeRole` 颜色角色枚举、`Palette`（Light/Dark）、`ThemeManager`（Current + ThemeChanged 事件 + Color(role)） |
| `Core/CloneStats.cs` | 运行统计内存计数 + 落盘 `data/stats.json`（`RecordLaunch`/`RecordStop`/`Get`/`Save`） |
| `Core/ConfigIo.cs` | 单分身/全量配置导出导入（`ExportClone`/`ExportAll`/`Import`） |
| `Core/AutoStart.cs` | 开机自启 `HKCU\Run` 读写（`SetEnabled`/`IsEnabled`） |
| `Core/HotkeyManager.cs` | 全局热键注册/转发（`Register`/`UnregisterAll`/`HandleWmHotkey` + `HotkeyFired` 事件） |
| `Core/TrayContext.cs` | `NotifyIcon` 托盘菜单封装（`BuildMenu`/`SetRunningCount` + `OpenRequested`/`NewRequested`/`ExitRequested`） |
| `Forms/SettingsForm.cs` | 设置面板窗口（左侧分组导航 + 7 组子面板 + 内嵌 `SettingRow` 静态工厂 + `public static void Open(Form)`） |

### 修改文件（8）
| 文件 | 改动点 |
|---|---|
| `Ui.cs` | 新增 Dark 色板、`FontLevel` 字体层级常量、`Ui.ThemedColor(ThemeRole)`、`Ui.ApplyTheme(Control)`；`AppleButton/AppleCard/PillChip/RoundProgressBar` 改读主题色并订阅 `ThemeChanged` 重绘 |
| `Core/Settings.cs` | 新增 14 字段 + 3 枚举（`IsolationMode`/`CloseBehavior`/`LaunchBehavior`）+ `DisableDiscoveryCache→EnableDiscoveryCache` 兼容迁移 |
| `Core/Store.cs` | `CloneEntry` 加 `Hotkey` 字段；`Store` 加批量 API（`BatchAddClones`/`EndAllRunning`） |
| `Core/Paths.cs` | 新增 `StatsFile => data/stats.json` |
| `MainForm.cs` | 顶栏设置齿轮、搜索/标签筛选栏、空状态插画化、状态栏密度、`ThemeManager.Init`+`Ui.ApplyTheme`、热键 `WndProc`、`CloneStats` 钩子、`TrayContext` 集成、开机自启钩子、关闭行为 |
| `Forms/DiscoverForm.cs` | 默认份数取自 `Settings.DefaultCloneCount`；扫描源取自 `Settings`；底色改用 `Ui.ThemedColor` |
| `Program.cs` | 可选：`LaunchHeadless` 启动时调 `CloneStats.RecordLaunch` |
| `AppClone.csproj` | `<Version>1.2.0</Version>` |

---

## 3. 数据结构与接口

### 3.1 主题
```csharp
public enum Theme { Light, Dark }
public enum ThemeRole { Bg, Card, Accent, AccentDark, AccentLight, ChipPurple, ChipPurpleText,
                        TextPrimary, TextSecondary, Border, Ok, Danger, FillGray, FillGrayHover }

internal static class Palette {
    private static readonly Dictionary<ThemeRole, Color> Light = new() { /* 现有 Ui 色值 */ };
    private static readonly Dictionary<ThemeRole, Color> Dark = new() { /* 深色调 */ };
    public static Color Get(Theme t, ThemeRole r) => (t == Theme.Dark ? Dark : Light)[r];
}

public static class ThemeManager {
    public static Theme Current { get; private set; } = Theme.Light;
    public static event Action? ThemeChanged;
    public static void Init(Theme t);
    public static void Set(Theme t);
    public static Color Color(ThemeRole role) => Palette.Get(Current, role);
}
```
Dark 色板：`Bg=#1C1C1E`、`Card=#2C2C2E`、`Accent=#0A84FF`、`AccentDark=#409CFF`、`AccentLight=#0A2A4D`、`ChipPurple=#2A2140`、`ChipPurpleText=#C9A8FF`、`TextPrimary=#F5F5F7`、`TextSecondary=#98989F`、`Border=#3A3A3C`、`Ok=#30D158`、`Danger=#FF453A`、`FillGray=#3A3A3C`、`FillGrayHover=#48484A`。

`Ui.cs` 新增：
```csharp
public const float FTitle=16F, FSub=13F, FSection=12F, FCardTitle=10.5F, FBody=9.5F, FCaption=8.5F, FChip=8F;
public static Color ThemedColor(ThemeRole role) => ThemeManager.Color(role);
public static void ApplyTheme(Control root);   // 设 root.BackColor=ThemedColor(Bg)，递归重设非自绘控件底色并 Invalidate
```

### 3.2 设置（Core/Settings.cs）
```csharp
public enum IsolationMode { Deep, Standard }
public enum CloseBehavior { Minimize, Exit }
public enum LaunchBehavior { Keep, Close }

public class Settings {
    public bool ScanSteam { get; set; } = true;
    public bool ScanEpic { get; set; }
    public bool ScanUwp { get; set; } = true;
    public bool ScanWinget { get; set; }
    public bool DisableDiscoveryCache { get; set; }   // 仅兼容旧文件读取

    public Theme Theme { get; set; } = Theme.Light;
    public IsolationMode DefaultIsolationMode { get; set; } = IsolationMode.Deep;
    public bool DefaultDeviceClone { get; set; } = true;
    public int DefaultCloneCount { get; set; } = 1;
    public bool LaunchOnStartup { get; set; }
    public CloseBehavior CloseBehavior { get; set; } = CloseBehavior.Minimize;
    public LaunchBehavior LaunchBehavior { get; set; } = LaunchBehavior.Keep;
    public bool MinimizeToTray { get; set; } = true;
    public bool TrayShowCount { get; set; } = true;
    public bool ConfirmDestructive { get; set; } = true;
    public bool RememberDontAsk { get; set; }
    public bool ShowDataUsage { get; set; } = true;
    public bool UpdateCheck { get; set; } = true;
    public bool EnableDiscoveryCache { get; set; } = true;

    public static Settings Current { get; }
    public static void Load();   // 含迁移：旧 DisableDiscoveryCache 存在且新键不存在 → EnableDiscoveryCache = !DisableDiscoveryCache
    public static void Save();
}
```

### 3.3 数据模型（Core/Store.cs）
```csharp
public class CloneEntry {
    public string Id { get; set; } = ...;
    public string Name { get; set; } = "";
    public bool DeepIsolation { get; set; } = true;
    public bool DeviceClone { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string Color { get; set; } = "";
    public string Hotkey { get; set; } = "";   // 新增：如 "Ctrl+Alt+1"，空=未绑定
}

public class Store {
    public List<CloneEntry> BatchAddClones(string appId, int count);
    public int EndAllRunning();
}
```

### 3.4 运行统计（Core/CloneStats.cs）
```csharp
public class StatsEntry { public int LaunchCount; public long CumulativeSeconds; public DateTime LastLaunchedAt; }
public static class CloneStats {
    public static void RecordLaunch(string appId, string cloneId);
    public static void RecordStop(string appId, string cloneId, TimeSpan dur);
    public static StatsEntry Get(string appId, string cloneId);
    public static void Save();
    public static void FlushIfNeeded();
}
```

### 3.5 配置导入导出（Core/ConfigIo.cs）
```csharp
public static class ConfigIo {
    public record CloneConfig(AppEntry App, List<CloneEntry> Clones);
    public static void ExportClone(string path, Store store, string appId, string cloneId);
    public static void ExportAll(string path, Store store);
    public static (AppEntry? App, List<CloneEntry> Clones) Import(string path, Store store);
}
```

### 3.6 开机自启（Core/AutoStart.cs）
```csharp
public static class AutoStart {
    public static void SetEnabled(bool enable);
    public static bool IsEnabled();
}
```

### 3.7 全局热键（Core/HotkeyManager.cs）
```csharp
public sealed class HotkeyManager : IDisposable {
    public const int WmHotkey = 0x0312;
    public event Action<string, string>? HotkeyFired;   // (appId, cloneId)
    public HotkeyManager(Form owner);
    public bool Register(string combo, string appId, string cloneId); // combo="Ctrl+Alt+1"
    public void UnregisterAll();
    public void HandleWmHotkey(int msg, IntPtr wParam);
    private static (uint mod, uint vk) Parse(string combo);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr, int, uint, uint);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr, int);
}
```

### 3.8 托盘（Core/TrayContext.cs）
```csharp
public sealed class TrayContext : IDisposable {
    public event Action? OpenRequested;
    public event Action? NewRequested;
    public event Action? ExitRequested;
    public TrayContext(Icon icon);
    public void BuildMenu(IEnumerable<(string appName, string cloneName, string appId, string cloneId)> recent, int recentN = 5);
    public void SetRunningCount(int running, int total, bool showCount);
    public void Dispose();
}
```

### 3.9 设置面板（Forms/SettingsForm.cs）
```csharp
public class SettingsForm : Form {
    public static void Open(Form owner);
    internal static AppleCard SettingRow(string title, string? desc, Control control, int width = 600);
    // 7 组子面板：BuildAppearance/BuildIsolation/BuildLaunch/BuildData/BuildScan/BuildBehavior/BuildAbout
}
```

---

## 4. 程序调用流程（时序图）

### ① 启动 → 读 Settings → 应用主题 → 建托盘 → 注册热键
```
Program.Main → new MainForm()
MainForm..ctor → Settings.Load()
             → ThemeManager.Init(Settings.Current.Theme)
             → BuildTopBar/Sidebar/RightPane/StatusBar
             → Ui.ApplyTheme(this)
             → new TrayContext(Icon); BuildMenu(recent)
             → new HotkeyManager(this); 循环 Register(clone.Hotkey,...)
             → AutoStart.SetEnabled(Settings.LaunchOnStartup)
             → Ui.RoundWindow + FadeIn
```

### ② 打开设置面板 → 改主题 → 即时重绘
```
用户点外观→选"深色" → SettingsForm: Current.Theme=Dark; Save(); ThemeManager.Set(Dark)
ThemeManager.ThemeChanged → MainForm: Ui.ApplyTheme(this) + RefreshAppList/RefreshRightPane
                       → SettingsForm: 重设本窗静态控件 + Invalidate
```

### ③ 新建分身 → 写统计 / 热键
```
用户 ＋新建分身 → Store.AddClone / BatchAddClones
              → 按 Settings.DefaultIsolationMode/DeviceClone/CloneCount 设默认
              → CloneStats.RecordLaunch
              → 若 clone.Hotkey 已设 → HotkeyManager.Register
              → RefreshRightPane
```

### ④ 关闭主窗口 → 最小化到托盘
```
点 X → OnFormClosing
  if CloseBehavior==Minimize && MinimizeToTray → e.Cancel=true; Hide()
  else → HotkeyManager.UnregisterAll(); CloneStats.Save(); TrayContext.Dispose(); _reallyClosing=true; Close()
托盘"退出" → ExitRequested → 同上真退出
```

---

## 5. 任务列表（T01–T17，有序）

| ID | 任务 | 文件 | 依赖 | 验收点 |
|---|---|---|---|---|
| T01 | 主题枚举与调色板管理器 | `Core/Theme.cs`(新) | — | Color(Dark,Bg) 返回深色；Set 触发 ThemeChanged |
| T02 | Ui 扩展：Dark 色板+字体层级+组件主题感知 | `Ui.cs`(改) | T01 | 组件改读 ThemedColor 且 ThemeChanged 重绘；ApplyTheme 可用 |
| T03 | Settings 新字段 + 兼容迁移 | `Core/Settings.cs`(改) | — | 旧 DisableDiscoveryCache→EnableDiscoveryCache 取反；新字段缺省不丢 |
| T04 | CloneEntry.Hotkey + Store 批量 API | `Core/Store.cs`(改) | — | BatchAddClones 返回 N；EndAllRunning 结束全部 |
| T05 | 运行统计落盘 | `Core/CloneStats.cs`(新) `Core/Paths.cs`(改) | — | RecordLaunch/RecordStop→Save 生成 stats.json 可回读 |
| T06 | 配置导入/导出 | `Core/ConfigIo.cs`(新) | T04 | ExportAll 写数组；Import 重建 ID+复制目录 |
| T07 | 开机自启 HKCU Run | `Core/AutoStart.cs`(新) | T03 | SetEnabled(true) 写 Run；IsEnabled 一致 |
| T08 | 全局热键管理器 | `Core/HotkeyManager.cs`(新) | — | Register 成功；Parse 正确；UnregisterAll 释放 |
| T09 | 托盘菜单封装 | `Core/TrayContext.cs`(新) | T03,T04 | BuildMenu 含四项；SetRunningCount 更新悬浮文字 |
| T10 | 设置面板骨架（导航+7组+SettingRow） | `Forms/SettingsForm.cs`(新) | T01,T02,T03 | Open(owner) 可开；7 组可切换；SettingRow 复用 AppleCard |
| T11 | 设置面板各分组控件绑定+即时保存+主题切换 | `Forms/SettingsForm.cs`(改) | T10 | 19 项全部可改并 Save；改主题即时重绘 |
| T12 | 主界面：设置入口+搜索/标签筛选+空状态插画+状态栏密度+主题应用 | `MainForm.cs`(改) | T02,T03 | 齿轮开设置；搜索+分类筛选；空状态插画；状态栏密度 |
| T13 | 主界面：批量操作+托盘常驻关闭行为+开机自启钩子 | `MainForm.cs`(改) | T04,T06,T07,T09 | 批量新建/结束/导出；关闭按 CloseBehavior 最小化；启动应用 LaunchOnStartup |
| T14 | 主界面：热键注册+统计刷新钩子+托盘集成 | `MainForm.cs`(改) | T05,T08,T09 | WndProc 转发 WM_HOTKEY 启动；统计随运行态刷新；托盘事件接通 |
| T15 | 发现页：默认份数/扫描源来自 Settings + 主题 | `Forms/DiscoverForm.cs`(改) | T03 | 默认份数=DefaultCloneCount；扫描源读 Settings；深色可读 |
| T16 | 美化收口（间距 8/12/16、圆角 14、字体层级统一） | `Ui.cs`/`MainForm.cs`/`DiscoverForm.cs`(改) | T02,T12,T13,T14 | 全窗间距/圆角/字号符合层级；AppleCard 默认 Radius=14 |
| T17 | 全量语法自查 | 全部（查） | T01–T16 | 无 CS0191/CS1002/CS1513；ComboBoxStyle 拼写；热键 Handle 有效 |

---

## 6. 依赖包列表
无第三方依赖。仅 .NET 10 框架自带：`System.Windows.Forms`、`System.Drawing`/`System.Drawing.Drawing2D`、`System.Text.Json`、`Microsoft.Win32`、`System.Threading.Tasks`；P/Invoke：`user32.dll`、`dwmapi.dll`。

---

## 7. 共享知识（跨文件约定，重点易错）

1. **主题取色统一入口**：所有颜色一律走 `Ui.ThemedColor(ThemeRole.X)`。禁止新增/修改代码里再硬编码 `Ui.Bg`/`Ui.Accent` 等浅色常量（浅色回退用 `Parent?.BackColor ?? Ui.ThemedColor(Bg)`）。
2. **组件主题重绘**：所有自绘控件构造函数必须 `ThemeManager.ThemeChanged += _ => Invalidate();`；OnPaint 全部用 `Ui.ThemedColor`。主界面/设置面板在 ThemeChanged 中调 `Ui.ApplyTheme(root)` 重设非自绘静态控件底色，并对动态列表做 RefreshAppList/RefreshRightPane 重建。
3. **设置改动通知**：`Settings.Current.X = ...; Settings.Save();`；仅"主题"项额外 `ThemeManager.Set(...)`。禁止设置面板直接持有主界面引用——用 ThemeChanged 事件解耦。
4. **统计落盘线程安全**：`CloneStats` 内部 `lock`；内存计数 + 退出时 Save + 累计若干次 FlushIfNeeded。统计与 apps.json 分离（data/stats.json）。
5. **全局热键句柄**：RegisterHotKey 必须在 MainForm.Handle 有效后（构造末尾或 OnHandleCreated）注册；WndProc 捕获 `m.Msg == 0x0312` 转发；组合串 `"Ctrl+Alt+1"`，Parse 映射 MOD+vk；冲突注册失败静默标红+记录日志（不弹窗打断）。
6. **数据兼容铁律**：Settings 新增字段全带默认值；DisableDiscoveryCache 旧 key 兼容（Load 中 HasKey(DisableDiscoveryCache) && !HasKey(EnableDiscoveryCache) 时取反），Save 写新 key。
7. **沙箱核心不可动**：Launcher/Injector/RegClone/fenshen.c 逻辑保持原样；新逻辑只改调用层。
8. **⚠️ 本沙箱无法编译，已知易炸点（工程师必读）**：
   - `readonly` 字段只能在构造函数或字段初始化器赋值，普通方法里赋值 CS0191（历史炸过 12 次）。HotkeyManager._owner、TrayContext._icon 等 readonly 必须在构造里赋值。
   - 对象初始化器 `new X { A=1 }` 合法；但方法返回值不能接 `{ }`，如 `Ui.Label(...) { Location=... }` 非法（CS1002/CS1513，历史 42 次）。必须 `var x = Ui.Label(...); x.Location = ...;`。
   - 控件类型名是 `ComboBoxStyle`（不是 ComboStyle）、TextBox/Label/CheckBox/Button/Panel 别拼错。
   - 新增 SetStyle(UserPaint...) 自绘控件注意 DPI/双缓冲（参考 AppleButton）。
   - 中文文案；NotifyIcon 的 Icon 不能为 null（传 MainForm.TryAppIcon()）。

---

## 8. 待明确事项（已按 PRD 建议取值，风险记录）
1. 深色模式仅手动切换（Theme），跟随系统留 Theme.System 占位、本期不实现。
2. 热键冲突：占用失败静默标红 + ToolTip（不弹窗）。
3. 统计落盘 data/stats.json；"最近启动"菜单上限 5 条。
4. 配置导入：一律新建 ID + 复制数据目录、不覆盖同名；批量导出仅配置 JSON 不含数据目录（UI 明确告知）。
5. 更新检查 UpdateCheck 仅存开关 + 展示版本号 1.2.0，不联网。
6. 数据目录迁移列为高级，本期不做（仅展示路径 + 打开）。
7. CloseBehavior==Exit 时关闭即真退出；CloseBehavior==Minimize 时按 MinimizeToTray 决定。
8. 全局热键在 UAC/全屏独占时可能收不到，属平台限制。
9. 批量导出不含快照数据。
