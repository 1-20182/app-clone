# 应用分身 AppClone v1.4.0 增量交付记录（F4 / F5 / F6 / F7）

- 日期：2026-09-21
- 来源：`docs/review-mapping-2026-09-21.md` 第 3 节的 P1 / P2 四项
  （F4 克隆管理中心 / F5 日志分级 + 结构化崩溃报告 / F6 角标图标 / F7 README 增补）
- 版本：`1.3.0` → `1.4.0`（`src/AppClone/AppClone.csproj:11`、设置面板「关于」）
- 交付口径：**成品交付** —— 便携版 `dist\AppClone.exe` + 安装程序 `dist\AppClone-Setup.exe` 均由本轮脚本产出

---

## 0. 一句话总结

P0 解决的是「事前就知道」（能不能分身 / 源应用变了没 / 这次启动会干什么）；
P1/P2 解决的是「出事后查得清、管得动、看得见」：**一张表管住所有分身**（F4）、
**一条可检索可送修的流水线**（F5）、**桌面图标就能认出这是哪个分身**（F6）。
隔离核心（`Injector` / `RegClone` / `fenshen.c`）依旧**零改动**。

---

## 1. 文件清单

### 新增（7 个）

| 文件 | 职责 |
|---|---|
| `src/AppClone/Core/CloneOverview.cs` | 管理中心的数据装配层：应用 × 分身 × 统计 × 兼容性 × 运行态 联表 + 筛选 + 列文案统一出口 |
| `src/AppClone/Core/Log.cs` | 分级日志（Debug/Info/Warn/Error）+ 7 天轮转 + 结构化崩溃报告 + 诊断包导出 |
| `src/AppClone/Core/BadgeIcon.cs` | 手写多尺寸 ICO（16/32/48 BMP-DIB + 256 PNG），把分身序号画在应用图标右下角 |
| `src/AppClone/Forms/CloneManagerForm.cs` | 跨应用总览窗体：筛选栏 + 10 列 ListView + 行内操作栏 + 2 秒轮询运行态 |
| `src/AppClone/Forms/LogViewerForm.cs` | 操作日志查看器：解析当日 `ops-*.jsonl`，「只看这个分身 / 应用」过滤 |
| `docs/incremental-1.4.0-2026-09-21.md` | 本文档 |
| `scripts/qa.cmd` | 一键全量 QA：编译 → 探针 → 便携版 → 安装程序 |

### 修改（12 个）

| 文件 | 改动 |
|---|---|
| `Core/Paths.cs` | 新增 `LogsDir` / `LegacyLaunchLog` / `LegacyCrashLog`；`EnsureAll()` 补建日志目录 |
| `Core/Settings.cs` | 新增 `WriteOpsLog=true` / `MinLogLevel=Info` / `BadgeShortcutIcon=true` / `TaskbarGrouping=false` |
| `Core/Store.cs` | `RemoveApp` / `RemoveClone` 写 `Rollback` 留痕；`AddClone` 写 `Info` |
| `Core/Snapshot.cs` | 快照创建 / 恢复 / 删除三处留痕（恢复与删除记为 `rollback`） |
| `Core/AppIcons.cs` | 新增 `RenderIcon(exePath, size)` 供角标图标取底座 |
| `Core/TrayContext.cs` | 新增 `ManagerRequested` / `LogsRequested` 事件 + 两个菜单项 |
| `Program.cs` | 全局异常改走 `Log.WriteCrashReport`；启动/退出/无头启动均入流水 |
| `MainForm.cs` | 顶栏「⋯ 更多 ▾」下拉、托盘接线、启动/停止/删除/重置/重建全链路留痕、快捷方式改挂角标图标、5 个新方法 |
| `Forms/SettingsForm.cs` | 新增「日志与诊断」分区（写日志开关 / 最低级别 / 打开日志目录 / 导出诊断包）；版本号 → 1.4.0 |
| `src/Shared/Shortcuts.cs` | 新增 `AppUserModelId` 属性 + `TrySetAppUserModelId`（实验性，失败静默） |
| `AppClone.csproj` | 版本 1.4.0 |
| `test/probe/Program.cs` | 断言 37 → **86**，覆盖 F1–F6 全部可判定逻辑 |

---

## 2. F4 克隆管理中心

### 2.1 为什么把「数据装配」从窗体里剥出来

窗体的正确性原本只能靠人眼在 GUI 里数行数。因此本轮的关键设计是：
**「哪些行该出现、每列显示什么」这类可判定逻辑全部放进 `CloneOverview`，窗体只负责把它画到 `ListView` 上。**

```csharp
public static List<OverviewRow> Build(Store store);                       // 全量联表
public static List<OverviewRow> Filter(IEnumerable<OverviewRow> rows,     // 四维筛选
    string? appName, RunStateFilter state, CompatLevel? compat, string? query);
public static string IsolationText(CloneEntry); DeviceText; RunningText;  // 列文案统一出口
public static string FormatDuration(long seconds); LastLaunchText(DateTime);
```

这样探针就能直接断言「3 行 / 筛选后 1 行 / 列文案 == 深度隔离」，而不需要启动任何窗口。

### 2.2 表格列（10 列）

应用 / 分身 / 序号 / 隔离 / 设备分身 / 兼容性 / 状态 / 启动次数 / 累计时长 / 最后启动。

- 运行状态在 `Build` 时刻采样一次；窗体用 **2 秒定时器轮询**刷新「状态」列，避免长期持锁。
- 累计时长为 0 时显示 `—`（`FormatDuration`），避免表格里出现一片「0 秒」的视觉噪音。
- 序号 `Index` 与卡片头像上的角标数字同源，保证「表里的 3 号」就是「图标上的 3 号」。

### 2.3 筛选维度

| 控件 | 取值 | 语义 |
|---|---|---|
| 应用下拉 | 「全部应用」+ 各应用名 | 精确匹配 `App.Name` |
| 状态下拉 | 全部 / 运行中 / 未运行 | `RunStateFilter` |
| 兼容性下拉 | 全部 / ✓ / ⚠ / ✗ / — | `CompatLevel?` |
| 搜索框 | 自由文本 | 应用名 / 分身名 / exe 文件名 / 发行商 四选一命中（忽略大小写） |

空条件一律视为「不限制」，调用方不必到处写 `null` 判断。

### 2.4 行内操作

启动 / 停止 / 打开数据目录 / 重建（复用 F2 的重建语义）/ 删除 / 查看日志 / 导出诊断包。

窗体的所有动作**都不自己实现**，而是通过 `Func` / `Action` 属性回调给 `MainForm` ——
保证「管理中心的删除」与「主界面的删除」走的是**同一段代码**，不会出现两套行为漂移。
回调属性：`LaunchRequested` / `StopRequested` / `OpenFolderRequested` / `RebuildRequested` /
`DeleteRequested` / `ViewLogRequested` / `ExportDiagnosticsRequested`。

### 2.5 入口

顶栏「⋯ 更多 ▾」下拉（管理中心 / 操作日志 / 导出诊断包）+ 托盘菜单「分身管理中心 / 操作日志」。

---

## 3. F5 日志分级 + 结构化崩溃报告

### 3.1 落盘布局

```
data\logs\
├── ops-YYYYMMDD.jsonl     每行一个 JSON 对象：ts / level / area / step / appId / cloneId / message / error
└── crash-<时间戳>.json     结构化崩溃报告（见 3.3）
```

`ops-*.jsonl` 用 **JSON Lines** 而非一个大 JSON 数组：追加日志只需 `FileMode.Append` 写一行，
不必读入整个文件再序列化回去；文件损坏时也只损失一行，不影响其余。

### 3.2 分级与开关

| 级别 | 数值 | 典型用途 |
|---|---|---|
| `Debug` | 0 | 高频内部步骤（默认不落盘，`MinLogLevel=Info`） |
| `Info` | 1 | 正常操作（启动 / 新建 / 快照 / 快捷方式） |
| `Warn` | 2 | 可继续的异常（角标图标生成失败已回退、预检查告警） |
| `Error` | 3 | 失败（启动失败、删除失败、崩溃） |

`Settings.WriteOpsLog`（默认 **true**）是总开关；`Settings.MinLogLevel`（默认 **Info**）过滤下限。
**低于下限的日志仍会更新内存中的「最后操作」**（`Log.LastOp`）—— 因为崩溃报告需要上下文，
不能因为级别过滤而让崩溃报告里出现一句空白的「最后干了什么」。

### 3.3 结构化崩溃报告

```jsonc
{ "ts": "...", "kind": "...", "appVersion": "...", "osVersion": "...",
  "runtime": "...", "is64BitProcess": true, "dataRoot": "...",
  "appId": "...", "cloneId": "...", "detail": "...",
  "lastOp": "{...最后一条流水...}", "message": "...", "type": "...",
  "stack": "...", "inner": "..." }
```

接入点：`Program` 的 `Application.ThreadException`、`AppDomain.UnhandledException`、
`TaskScheduler.UnobservedTaskException` 三处全局兜底。旧 `crash.log` 仍追加一行摘要做兼容。

### 3.4 并发与轮转

- **并发**：GUI 进程与 `--launch` 无头进程可能同时写同一份流水，因此用
  `FileShare.ReadWrite` 打开 + 失败重试 3 次（每次等 25 ms），避免独占打开导致一边静默丢日志。
- **轮转**：保留 `KeepDays = 7` 天；清理每天最多执行一次（`_cleanupDay` 记忆），
  不因每次写日志都去扫目录。
- **异常安全**：`Write` 的每一层都吞异常 —— 日志写不进去绝不能阻断启动分身。

### 3.5 回滚留痕（E2 的落点）

凡是「删掉用户数据」或「把数据回退到快照」的地方，统一写 `step="rollback"`：

| 位置 | 记录 |
|---|---|
| `Store.RemoveApp` | 移除应用 + N 个分身数据 |
| `Store.RemoveClone` | 删除分身 |
| `Snapshot.Restore` | 恢复快照（当前数据被覆盖） |
| `Snapshot.Delete` | 删除快照 |
| `MainForm.DeleteClone` | 用户确认删除分身 |
| `MainForm` 重置 / 重建 | 用户确认重置 / 重建 N 个分身数据 |

事后能回答「谁在什么时候动了什么」——这正是评审稿 E2 想要的能力。

### 3.6 诊断包导出（隐私边界）

`Log.ExportDiagnostics(zipPath)`：

- **收**：`data\logs\` 全部文件、`apps.json`、`settings.json`、`compat.json`、`stats.json`、
  `launch_rules.json`（若存在），外加一份 `summary.txt`（版本 / 环境 / 数据规模 / 兼容性统计 / 包含与不包含声明）。
- **不收**：`profiles\` 与 `snapshots\` —— 那里是用户的真实账号数据，体积巨大且属于隐私，**绝不进诊断包**。

探针对此有专门断言：`诊断包不含 profiles / snapshots（用户真实数据）`。

---

## 4. F6 角标图标落到快捷方式

### 4.1 为什么手写 ICO

本项目坚持零 `PackageReference`。ICO 容器格式本身极简单（6 字节文件头 + N × 16 字节目录项 + 各图像数据），
自己写反而比引依赖更可控。**最硬的验收标准是：系统 `System.Drawing.Icon` 解析器能读出这些帧** ——
读不出，桌面快捷方式上就是一个白块。

### 4.2 尺寸与编码策略

| 尺寸 | 编码 | 理由 |
|---|---|---|
| 16 | BMP(DIB) | 任务栏小图标。最保守格式，任何 Windows 版本、任何旧提取路径都认 |
| 32 | BMP(DIB) | 任务栏 / 中图标 |
| 48 | BMP(DIB) | 资源管理器中等图标 |
| 256 | PNG | 大图标。256 的 BMP 单张就要 256 KB，PNG 压到几 KB，Vista 后一律支持 |

DIB 细节：`BITMAPINFOHEADER` 的 `biHeight` 写**两倍**（图像高 + AND 掩码高），像素**自底向上 BGRA**，
掩码区全 0（32 位图靠 alpha 通道，不需要额外抠洞）——这是最容易写错、也最容易导致「白块」的地方。

### 4.3 小尺寸的取舍

`d = 56% × size`，16 px 下圆点直径约 9 px，**小于 `MinDiameterForText=12` 时不画数字，只画纯色圆点**：
宁可给「这是另一个分身」的视觉线索，也不要画一团看不清的糊字。
序号 `1–9` 直接用数字，**10 以上收敛为「9+」**（小圆点里两位数是灾难）。

### 4.4 缓存与回退

`Ensure(cloneId, index, colorHex, exePath, force)`：

- 若已有 ico 且其写入时间 **晚于源 exe**，直接复用 —— 避免每次创建快捷方式都重绘一遍。
- 底座取不到或写盘失败 → 返回 `null`，`MainForm.CreateShortcut` **回退到原 exe 图标**，
  并写一条 `Warn`（不阻断用户操作）。
- 输出路径：`data\icons\badge-<cloneId>.ico`。
- 单个文件上限保护 `MaxFileBytes = 4 MB`，防异常放大写出巨大文件。

### 4.5 实验性任务栏分组（默认关）

`Shared/Shortcuts.cs` 新增 `AppUserModelId` 属性 + `TrySetAppUserModelId`（IPropertyStore 写
`PKEY_AppUserModel_ID`），`MainForm` 在 `Settings.TaskbarGrouping` 开启时设置
`AppClone.{appId}.{cloneId}`。**任何一步失败都静默**，不弹错、不阻断 —— 各大 Windows 版本行为不一，
故标注为实验性、**默认关**。

---

## 5. F7 README / docs 增补

README 新增章节：**支持应用矩阵**、**原理流程图（Mermaid）**、**Architecture（Core 类职责表）**、
**FAQ**、**Security Notes**、**Roadmap**。详见 `README.md`。

---

## 6. 数据兼容

- `Settings` 四个新字段全部带默认值（`WriteOpsLog=true` / `MinLogLevel=Info` /
  `BadgeShortcutIcon=true` / `TaskbarGrouping=false`），缺字段取默认。
- `CloneOverview` / `Log` / `BadgeIcon` 全是**新增类**，不触碰 `AppEntry` / `CloneEntry` 结构。
- `Shortcuts.AppUserModelId` 是可选属性，默认空串 → 不写属性，行为与旧版一致。
- 实测：探针内置 v1.2.0 格式的无字段 `apps.json`，载入后应用 / 分身不丢、新字段为默认（沿用 1.3.0 断言）。

---

## 7. 验证

### 7.1 编译

```cmd
dotnet build src\AppClone\AppClone.csproj -c Release
```

结果：**0 错误**，2 个既有警告（`Ui.cs:534` CS8604、`RegClone.cs:136` CS8600，与本次改动无关）。

### 7.2 逻辑回归探针

```cmd
scripts\probe.cmd
```

结果：**PASS=86  FAIL=0  SKIP=0**（退出码 0）。新增覆盖：

- **F4**：`Build` 3 行、四维筛选（应用 / 状态 / 兼容性 / 搜索）、列文案与时长格式化
- **F5**：低于最低级别的日志不落盘、达到级别的日志落盘、总开关关闭后完全不写流水但仍更新内存最后操作、
  崩溃报告落盘含版本/最后操作/堆栈、旧 `crash.log` 仍追加、诊断包含 `summary.txt` 与当日流水且
  **不含 profiles / snapshots**
- **F6**：`Build` 生成 4 帧 ICO、文件头 `reserved=0 / type=1`、目录项尺寸/偏移/长度自洽、
  256 帧为 PNG 内嵌、16 帧为 BMP-DIB（`biSize=40` / `biHeight` 双倍）、
  **系统 `Icon` 解析器可读并取到 16×16 / 32×32**、序号 >9 走收敛分支、`Ensure` 命中缓存不重复绘制、
  图标落在 `data\icons`、源程序不存在时返回 `null`

> 探针把 `FENSHEN_DATA_DIR` 重定向到 `test\probe\bin\Release\net10.0-windows\tmp\`，
> 全程不读写用户真实数据目录，也不启动 GUI。

### 7.3 成品构建

```cmd
scripts\build.cmd             :: → dist\AppClone.exe + dist\sandbox\{inject64,inject32,selftest,devprobe}
scripts\build_installer.cmd   :: → dist\AppClone-Setup.exe
```

### 7.4 尚未覆盖、需人工确认

- 隔离能力测试 `bash scripts/test.sh`（15 项断言）需在**完整 Git Bash** 中运行（本环境 bash 指向损坏的
  PortableGit）。隔离核心本轮**零改动**，预期无回归，但**未在本轮实跑**。
- 以下项需在本机 GUI 目测（本环境不启动 GUI）：
  - [ ] 「⋯ 更多 ▾」下拉三个入口可点，管理中心 / 日志查看器 / 导出诊断包均能打开
  - [ ] 管理中心 10 列表头不重叠、筛选四维联动正确、行内操作全部生效
  - [ ] 日志查看器能读当日流水，「只看这个分身」过滤正确
  - [ ] 桌面快捷方式图标显示分身角标（数字 / 颜色 / 16 px 下的圆点）
  - [ ] 设置「日志与诊断」分区在深浅两主题下布局正确
  - [ ] 深度色主题下管理中心与日志窗体的表格 / 徽章配色

---

## 7.5 本轮全量 QA 发现并修复的缺陷（重要）

跑隔离能力测试时挖出两个问题，性质完全不同，都已修复：

### 缺陷 1（产品缺陷，严重）：无头启动被 GUI 单实例互斥体拦下

**现象**：测试 3「互斥体改名实现多开」实验组结果 `FIRST=1 SECOND=0`（期望 `2 / 0`），
且测试**卡死 16 分钟不返回**。抓进程现场：

```
PID=5088  CMD=D:\fenshen\dist\AppClone.exe --launch app_mutex clone_m2
PID=5088  Title='应用分身'  Responding=True
```

**根因**：`Program.Main` 在**分流命令行参数之前**就抢了单实例互斥体
`Local\AppClone.SingleInstance`，且注释明确写着「无头模式与 GUI 模式统一走此保护」。
两个 `--launch` 并发时，后一个拿到 `createdNew=false` → `MessageBox.Show("应用分身已在运行")`
→ 在无人点击的环境里**永久挂起**（`Responding=True` 的模态框）。

**影响面（这是它真正的严重性，不只是测试失败）**：

1. **主程序开着时，双击任何分身快捷方式都不会启动分身**，只弹一个框 ——
   而快捷方式的卖点恰恰是「免开主程序直达」；
2. 连续双击两个分身的快捷方式（正常操作），后一个必然被拦下，多开直接失效；
3. 在**无人值守 / 脚本 / 计划任务**场景下是永久挂起，而不是报错退出。

**修复**：把 `--launch` 分流**提到互斥体之前**，无头模式不再参与 GUI 单实例保护
（`Program.cs`，并补注释说明为何必须如此）。GUI 模式的单实例保护原样保留。

> 该缺陷自 v1.1.0（commit `c93ac2e`）就存在，属**历史遗留**，本轮 QA 才暴露。

### 缺陷 2（测试脚本缺陷）：原生 exe 被传入 POSIX 路径

**现象**：测试 3 对照组 `FIRST= SECOND=`（两个都是空）。

**根因**：对照组是**从 bash 直连**调用 `dist\sandbox\selftest.exe ctlA "$OUT/ctl.txt"`，
而 `$OUT` 是 POSIX 形式 `/d/fenshen/test/out`。原生程序不认识这个路径，
`selftest.c` 的 `fopen_s(&g_out, argv[2], "a")` 打不开文件 → **静默回退成「只打 stdout」**
（见 `selftest.c:7, 45-46`）→ `ctl.txt` 从未落盘 → `grep` 取不到计数。

对照组之外的用例之所以正常，是因为它们经 `AppClone --launch` 启动目标程序，
路径来自 `apps.json` 里的**反斜杠 Windows 路径**，天然没有这个问题。

**修复**：`test.sh` 新增 `OUTW='D:\fenshen\test\out'`，凡**直传原生 exe 参数**一律用 `$OUTW`；
给 MSYS 工具（grep / cat / rm / sed）的仍用 POSIX 的 `$OUT`。并加注释说明这条区分。

---

## 8. 后续（本次未做）

| 项 | 内容 | 优先级 |
|---|---|---|
| — | MainForm 拆分（`CloneMenuController` / `RightPaneBuilder`），降低单文件 1600+ 行的改动风险 | 低 |
| — | 兼容性数据库「云同步 / 社区共享」（当前纯本地） | 低 |
| — | 设备分身扩展：MAC 地址（`GetAdaptersAddresses`）与 SMBIOS UUID（`GetSystemFirmwareTable`） | 中 |

至此评审映射表（`docs/review-mapping-2026-09-21.md`）中的 **F1–F7 全部落地**。
