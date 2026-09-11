# 应用分身 AppClone — 增量 PRD（自动发现已安装软件 + 自动化 + 性能优化）

| 项 | 内容 |
|---|---|
| 文档类型 | 增量 PRD（仅描述**本次新增/变更**部分，不重写整个产品文档） |
| Language | 简体中文 |
| 项目名 | `appclone`（仓库 AppClone / 应用分身） |
| 技术栈 | C# WinForms + .NET 10（`src\AppClone\`），C 沙盒 DLL（`src\sandbox_dll\fenshen.c`） |
| 现有基线 | 四层隔离（环境变量 / 注册表 / 多开解锁 / 设备分身）+ 分身管理，`scripts/test.sh` 15/15 通过 |
| 版本 | 增量版本建议 **1.1.0**（现为 1.0.0） |
| 撰写人 | 许清楚（产品经理） |
| 日期 | 2026-09-11 |

## 0. 原始需求与问题定义

**用户原话（逐字）**

> "目前的软件做出来的初步的样子现在请你根据可添加的新功能以及自动化相关的方面优化这个软件的性能表现优化这个软件的功能添加这个软件的新功能之类的你可以先搜寻相关功能展示相关功能应该什么功能应该出现在我们这个软件里面实现它最好是能够自动检测当前电脑中安装的软件之类的一些功能自动化一定要高"

**当前最大痛点**：添加应用只能**手动点击「＋添加应用」→ 自己找到 .exe**。用户必须知道目标程序装在哪（`C:\Program Files\...\WeChat\WeChat.exe` 这一层路径对普通用户是黑盒），这是自动化程度低的最集中体现。

**本次增量的三条主线**

1. **自动检测本机已安装软件**（用户明确点名，P0 主线）：扫描 → 去重 → 排序推荐 → 勾选 → 一键批量添加。
2. **自动化一定要高**（用户强调）：发现之外的一切重复动作都自动做（图标、命名、分类、参数适配、批量建分身、增量刷新）。
3. **性能优化**：启动、UI 卡顿、图标 IO、注册表快照、进程轮询五项开销。

---

## 1. 调研结论：同类工具功能矩阵

### 1.1 调研对象

| 类别 | 代表工具 | 调研来源 |
|---|---|---|
| 通用沙盒/隔离 | Sandboxie-Plus / Sandboxie Classic | sandboxie-plus.com 官方功能页与 Brochure |
| 安卓应用分身 | 平行空间（LBE）、双开助手 | 各安卓分发站功能描述（多来源交叉一致） |
| Windows 多开方案 | 微信多开（批处理 / Mutex Hook / 沙箱 / 多安装目录） | 腾讯云开发者社区、CSDN 技术分析 |
| 包管理器清单 | winget list / winget export、Get-AppxPackage | Microsoft Learn、GeekChamp、TechBloat |
| 浏览器多实例 | Chromium `--user-data-dir`、Firefox `-profile -no-remote` | 已在 AppClone `Launcher.BuildAutoArgs` 落地 |
| 游戏平台库 | Steam `libraryfolders.vdf` + `appmanifest_*.acf` | GitHub steam_utils.py、@ciberus/find-steam-app、GeekChamp |

### 1.2 功能矩阵

> 结论分级：**必须有**（本次 P0/P1 做）、**锦上添花**（P2 或后续）、**不适合**（明确不做，写进"非目标"）。

| # | 功能点 | 出现在 | 建议 | 理由 |
|---|---|---|---|---|
| F1 | **从本机已安装程序列表勾选添加**（而非手选 exe） | 平行空间「添加应用」页、Sandboxie「Add By Name/File」 | **必须有（P0）** | 与用户点名的诉求完全一致；安卓分身工具的标准入口，桌面缺失即体验断层 |
| F2 | 自动提取图标 / 应用名 / 版本 / 发布者 | Sandboxie、平行空间、winget list | **必须有（P0）** | 现状已能提取图标，但名称取自文件名（如 `WeChat` 尚可、`msedge` 差）；需改用 DisplayName |
| F3 | 一键批量添加 + 批量创建 N 个分身 | 平行空间「一键分身」 | **必须有（P0）** | "自动化一定要高"的直接落地，从 N 次点击降到 1 次 |
| F4 | 分类筛选 / 搜索 / 排序（按"值得分身"推荐） | Sandboxie「可搜索的沙箱与配置列表」 | **必须有（P0）** | Uninstall 键在一台典型机器上约 400+ 条（含大量运行时/补丁/驱动），不筛选等于不可用 |
| F5 | 已添加应用置灰 / 去重 | 平行空间（已添加不再列出） | **必须有（P0）** | 防止重复添加同一 exe |
| F6 | 强制规则：指定程序永远以分身方式启动 | Sandboxie「Force Programs / 强制运行规则」 | **必须有（P1）** | 自动化最高形态——"这个程序以后都走分身"，无需每次开主界面 |
| F7 | 桌面/开始菜单快捷方式自动生成（每个分身独立图标与名称） | 平行空间「创建快捷方式 + 自定义图标名称」、Sandboxie「Start Menu integration」 | **必须有（P1）** | 现有「创建桌面快捷方式」仅单条手动；升级为批量 + 自定义名称/角标 |
| F8 | 快照 / 一键恢复与重置 | Sandboxie「Snapshot Manager」 | **必须有（P1）** | 现有「重置分身数据」是一次性不可逆；快照 = 自动化回滚 |
| F9 | 导入 / 导出配置（含分身数据 7z 归档） | Sandboxie「Import and export of sandboxes using 7z archives」 | **锦上添花（P2）** | 换机迁移有价值，但需自带压缩能力（不能引 NuGet，只能用系统 tar.exe 或自研）→ 成本高 |
| F10 | 分身自定义名称 / 颜色 / 图标 | 平行空间「自定义分身图标与名称」、Sandboxie「custom sandbox colors, icons, fonts」 | **必须有（P1）** | 多分身场景下"分身 1/2/3"不可辨识，是真实可用性问题 |
| F11 | 隐私锁 / 隐藏分身 | 平行空间「空间锁、隐身安装、隐藏图标」 | **锦上添花（P2）** | 桌面场景弱于手机（手机是共用设备），优先级低 |
| F12 | 网络隔离 / 每分身代理 / 防火墙规则 | Sandboxie「per-sandbox firewall (WFP)、DNS control、SOCKS5 proxy」 | **不适合（本期）** | 需驱动或 WFP 深度集成，远超用户态注入范围，与"四层隔离"技术栈不匹配 |
| F13 | 虚拟定位 / GPS 模拟 | 平行空间 | **不适合** | 桌面端无对应 API 层，且触及合规红线 |
| F14 | 机型/设备指纹伪装 | 平行空间「机型模拟」 | **已有（不需新增）** | AppClone 第四层「设备分身」已覆盖 MachineGuid/ProductId/计算机名/BIOS 序列号 |
| F15 | 内存/进程数限制、全局热键终止 | Sandboxie「per-sandbox memory limits、global hotkeys」 | **锦上添花（P2）** | 小众需求；但"一键结束全部分身"值得做（P1） |
| F16 | 加密沙箱（AES 存储） | Sandboxie「Encrypted sandboxes」 | **不适合** | 与产品定位（多开分身）无关，是安全沙盒需求 |
| F17 | 便携模式（免安装） | Sandboxie「Portable mode」 | **已有（不需新增）** | `Paths.DataRoot` 已支持 exe 同目录 data\ + `FENSHEN_DATA_DIR` 覆盖 |
| F18 | 托盘常驻 + 快速启动 | Sandboxie「Tray Icon Menu」 | **锦上添花（P2）** | 有 F6 强制规则后价值下降 |
| F19 | 应用类型自适应启动参数 | Chromium/Firefox 官方多实例通道 | **已有（需扩展，P1）** | 现状仅按 exe 名白名单匹配 8 种 Chromium + Firefox；应扩展为规则表（Electron/CEF/微信/Telegram 等） |
| F20 | 生命周期触发器（分身启动/退出时执行脚本） | Sandboxie「Trigger system for sandbox lifecycle events」 | **锦上添花（P2）** | 极客向，收益/成本比低 |
| F21 | 扫描结果缓存 + 增量刷新 | 同类工具普遍缺失（Sandboxie、平行空间每次都重扫） | **必须有（P0）** | 我们的差异化优势：让"发现"从 8 秒降到 <1 秒，符合"自动化一定要高" |
| F22 | 自动识别"值得分身"的应用并置顶 | 无同类工具提供（行业空白） | **必须有（P0）** | Uninstall 列表噪音极大，靠用户自己从 400 条里挑是不现实的；这是把"扫描"变成"可用功能"的关键一跳 |
| F23 | 多开能力预检（应用是否真的能多开） | 无同类工具提供 | **锦上添花（P2）** | 可基于进程互斥体探测给出"预计可多开/可能受限"提示，价值中等、实现成本高 |

**小结**：本次纳入 **必须有 11 项（F1–F8、F10、F19、F21、F22）**；明确不做 **6 项（F12、F13、F16 及 F11/F15/F18/F20 中的重部分）**，理由均已记录，避免架构师重复评估。

---

## 2. 技术调研：Windows 枚举已安装程序的途径与坑

> 结论直接约束 P0 实现，请架构师按此选型。

| 途径 | 覆盖 | 速度 | 坑 | 结论 |
|---|---|---|---|---|
| **注册表 Uninstall 键**<br>`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*`<br>`HKLM\SOFTWARE\WOW6432Node\...\Uninstall\*`<br>`HKCU\SOFTWARE\...\Uninstall\*` | 最广（控制面板"程序与功能"同源），典型机器 400+ 条 | 极快（PowerShell Get-ItemProperty 三键合计约 **2.1 秒**；原生 RegEnumKey 可做到 <300ms） | 噪音多：系统补丁、语言包、运行时（VC++ Redist）、驱动、Office 更新都会被列出；需用 `SystemComponent=1`、`ParentKeyName`（子补丁）、`ReleaseType=Security Update/Hotfix/Update`、无 `DisplayName`、`WindowsInstaller=1 且无 InstallLocation` 过滤 | **主力扫描源（S1）** |
| **WMI `Win32_Product`** | 仅 MSI 安装的软件（大量现代安装器不覆盖） | **极慢**：实测 `Get-CimInstance Win32_Product` 耗时 **68 秒** | **严重副作用**：查询会触发所有 MSI 产品的**一致性检查并静默自动修复**，会在应用事件日志刷大量条目，极端情况曾致蓝屏；Microsoft KB974524 明确不推荐；`Win32Reg_AddRemovePrograms` 仅在装了 SCCM 代理时才存在 | **❌ 明确禁用，写入代码注释与 PRD 约束** |
| **App Paths 键**<br>`HKLM/HKCU\...\CurrentVersion\App Paths\*` | 补充覆盖：不写 Uninstall 的绿色版/工具（如部分便携软件、SnippingTool、PowerShell） | 极快 | `(Default)` 值即 exe 全路径，但可能被劫持（安全软件取证常见指标）；需校验文件存在 | **补充源（S2）** |
| **开始菜单快捷方式**<br>`%ProgramData%\Microsoft\Windows\Start Menu\Programs`<br>`%APPDATA%\Microsoft\Windows\Start Menu\Programs` | 覆盖绿色版、Steam/Epic 生成的快捷方式 | 快（递归枚举 .lnk） | .lnk 需解析（项目已有 `Shared\Shortcuts.cs` 的 IShellLink 实现）；**卸载类快捷方式**（卸载 xxx / Uninstall / 帮助 / 官网）必须过滤；部分 .lnk 指向 UWP（含 AppUserModelID）需分流 | **补充源（S3）** |
| **UWP / AppX**<br>`Get-AppxPackage`（PowerShell） | 微软商店应用、系统内置应用 | **慢**：PowerShell 启动本身 ≥250ms，加 Appx 枚举约 1–3 秒 | UWP 装在受保护目录 `C:\Program Files\WindowsApps\`，**无法直接 CreateProcess 挂起注入 DLL**；启动必须走 `explorer.exe shell:appsFolder\<PackageFamilyName>!<AppId>`（中间经过 explorer，拿不到子进程句柄 → 四层隔离基本失效） | **P1：可"发现"但默认标记「不支持分身」，UI 灰显并给出原因**；不做 P0 |
| **游戏平台库**<br>Steam / Epic | 游戏多开是分身高频场景 | 快（纯文件解析） | Steam：从 `HKCU\Software\Valve\Steam` 的 `SteamExe` 拿到安装目录 → `steamapps\libraryfolders.vdf`（**存在 v1/v2 两种格式**，v1 值为字符串、v2 值为含 `path` 的子对象，且 v2 仅在 Steam 启动时同步、可能过期 → **强制按 v1 扫描 `appmanifest_*.acf`**）→ `AppState.name/installdir`；Epic：`%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item`（JSON） | **P1：作为独立扫描源，可开关** |
| **winget list** | 与 Uninstall 高度重叠，价值主要在"补充分类/发布者" | **慢**（数秒，且首次运行可能联网） | 会弹 **msstore 源协议确认提示**（"是否同意所有源协议条款? [Y/N]"）导致进程挂起 → 必须加 `--disable-interactivity` 与超时；无 winget 的机器需优雅降级 | **P2：默认关闭，设置里可开** |
| **全盘扫描 .exe** | 最全 | 极慢（分钟级） | 噪音灾难、IO 风暴 | **❌ 默认禁用**；仅允许 P2「手动指定目录扫描」 |

**去重与合并策略（P0）**

1. **主键**：exe 路径规范化（去首尾引号 → `Environment.ExpandEnvironmentVariables` → `Path.GetFullPath` → 小写）后完全一致 → 视为同一应用，合并字段。
2. **次键**：DisplayName 归一化（去版本号 `\d+(\.\d+)+`、去 `(x64)`/`64-bit`、去空格与大小写）→ 同名的多个路径为"候选"，按优先级保留一个。
3. **源优先级**：S1 Uninstall > S2 App Paths > S3 开始菜单 > S5 游戏库。
4. **字段补全**：高优先级源缺字段时，用低优先级源补（如 S1 有 Publisher 但无有效 exe 路径，用 S2 补）。
5. **已添加去重**：规范化路径与 `Store.Model.Apps[].ExePath` 规范化后比对，命中即置灰并标「已添加」。

**"值得分身"识别（P0，差异化能力）**

- **排除（必过滤）**：`SystemComponent=1`、有 `ParentKeyName`（子补丁）、`ReleaseType` 含 Update/Hotfix/Security Update、Publisher 属于微软运行时黑名单（Microsoft Visual C++ Redistributable / .NET Runtime / Windows SDK / 驱动与打印机）、名称含 `卸载/Uninstall/Help/文档/Readme/SDK/Runtime/Redistributable/Driver/Update`。
- **推荐分类词典（命中即加分并置顶）**：
  - 浏览器：`chrome/msedge/firefox/brave/opera/vivaldi/360se/ chromium/ QQBrowser/百分浏览器`
  - IM 社交：`WeChat/微信/QQ/Telegram/Discord/Slack/钉钉/飞书/企业微信/WhatsApp/Line/Signal`
  - 邮件办公：`Outlook/Thunderbird/Foxmail/Teams/Zoom/Notion/Obsidian`
  - 游戏客户端：`Steam/Epic/EpicGames/WeGame/原神/米哈游/Riot/LOL/Battle.net/Minecraft`
  - 云盘：`百度网盘/阿里云盘/OneDrive/Dropbox/Google Drive/坚果云`
  - 开发：`VS Code/Cursor/Trae/JetBrains/Postman/GitHub Desktop`
- **排序**：分类推荐度降序 → 是否已在库中（已添加沉底）→ 名称（`StringComparer.CurrentCulture`，中文友好）。
- **输出三档标签**：`推荐分身`（命中词典）/ `可尝试`（普通桌面应用）/ `不建议`（运行时、系统组件、卸载器、安全软件、驱动）。

**性能要求（P0，硬性）**

| 指标 | 目标 |
|---|---|
| 冷启动到首屏列表可见（无缓存） | **≤ 1.5 s**（先出 S1 结果，S2/S3 增量追加） |
| 全量扫描完成（S1+S2+S3） | **≤ 3 s**（典型 400–800 条） |
| UI 线程阻塞 | **全程 0 ms**（所有 IO 在后台线程，UI 只接收批量回调） |
| 二次打开（有缓存） | **≤ 0.3 s** 出列表，后台静默增量刷新 |
| 单源超时 | winget 8 s / PowerShell AppX 6 s / 其余 3 s，超时即跳过并记状态 |
| 内存增量 | ≤ 60 MB（含图标缓存） |

---

## 3. 产品目标（本次增量）

| # | 目标 | 衡量指标（可验证） |
|---|---|---|
| G1 | **消灭"手找 exe"**：用户从本机已安装程序中勾选即可添加 | 添加 1 个新应用的**平均点击数 ≤ 4**（打开发现 → 勾选 → 添加），当前 ≥ 6 且必须知道 exe 路径；新用户无需任何路径知识 |
| G2 | **批量化**：一次操作完成"添加 + 建分身 + 建快捷方式" | 批量添加 N 个应用并各建 2 个分身，**总耗时 ≤ 10 s 且仅需 2 次点击** |
| G3 | **自动化分级**：把"值得分身"的应用自动推到用户面前 | 发现页首屏 20 条中，**人工认定的"值得分身"应用 ≥ 80%**（在 3 台不同配置的测试机上人工核对） |
| G4 | **性能可感知**：主界面全交互无卡顿 | 冷启动 ≤ 1.5 s；列表滚动/切换应用无掉帧（无 >100 ms 的 UI 线程阻塞）；空闲（无运行分身）时 CPU 占用 **< 0.5%** |
| G5 | **零回归**：不破坏既有能力与数据 | `scripts/test.sh` **15/15 仍全绿**；老 `apps.json`（无新增字段）可直接加载且数据完整 |

---

## 4. 用户故事

| # | 用户故事 | 覆盖能力 |
|---|---|---|
| US1 | 作为**刚装好软件的新用户**，我希望打开软件就能看到本机已安装的、适合分身的程序列表，这样我不必知道任何 exe 路径也能立刻开始 | F1 自动发现 |
| US2 | 作为**有多账号需求的用户**，我希望在发现页一次勾选微信、QQ、Edge 三个应用并批量添加，这样我不用来回点三次「添加应用」 | F3 批量添加 |
| US3 | 作为**效率优先的用户**，我希望批量添加时能直接指定"每个应用创建 2 个分身并生成桌面快捷方式"，这样添加完就可以直接双击使用 | F3 + F7 |
| US4 | 作为**面对几百条列表的用户**，我希望列表默认按"值得分身"排序，并用分类标签筛选（浏览器/社交/游戏/开发），同时有搜索框，这样我 3 秒内能找到目标 | F4 + F22 |
| US5 | 作为**老用户**，我希望已经添加过的应用在发现页置灰显示「已添加」，这样我不会重复添加同一个程序 | F5 |
| US6 | 作为**长期使用者**，我希望第二次打开发现页时立刻显示上次结果，并在后台自动补充新安装的软件，这样我不用每次等扫描 | F21 缓存 + 增量刷新 |
| US7 | 作为**重度多开用户**，我希望把微信设为"始终以分身方式启动"，这样我双击桌面原图标也是走分身环境 | F6 强制规则 |
| US8 | 作为**开了 5 个分身的用户**，我希望给每个分身改名为"工作号/小号/测试号"并选不同颜色，这样我不会点错 | F10 自定义 |
| US9 | 作为**担心数据丢失的用户**，我希望在重置分身前能自动存一个快照，这样误操作可一键回滚 | F8 快照 |
| US10 | 作为**性能敏感用户**，我希望软件在没有任何分身运行时几乎不占资源，且图标和列表加载不卡界面 | G4 性能优化 |

---

## 5. 需求池

> 每条含：**描述 / 验收标准（可验证）/ 自动化点**。

### P0 — 必须有（本次交付主线：自动检测已安装软件）

#### R1 已安装软件扫描引擎

**描述**：新增 `Core\AppDiscovery.cs`，后台线程扫描本机已安装程序，产出统一的 `DiscoveredApp` 列表（名称、exe 路径、发布者、版本、安装位置、图标源、来源标记、分类、推荐度）。

**扫描源与顺序**（见第 2 节结论）：
- S1 注册表 Uninstall（HKLM x64 / HKLM WOW6432Node / HKCU）— **主力，同步优先返回**
- S2 App Paths（HKLM / HKCU）— 补充
- S3 开始菜单快捷方式（ProgramData + 当前用户）— 补充
- **禁用**：`Win32_Product`、全盘扫描

**验收标准**
1. 在 3 台测试机（Win10 22H2 / Win11 23H2 / Win11 24H2）上，扫描结果**必须包含**：系统已装的浏览器、微信/QQ（若安装）、Office/VS Code 等，且**不出现** VC++ Redistributable、.NET Runtime、KB 补丁、驱动包。
2. 扫描产出的每一条 exe 路径，**经 `File.Exists` 校验通过率 100%**（不存在的文件不进列表）。
3. 冷启动首屏 ≤1.5 s，全量 ≤3 s（见第 2 节性能表）。
4. 扫描全程 UI 线程无阻塞：拖动窗口、切换 Tab 不掉帧（可用 Stopwatch 在 UI 线程打点验证，单次消息处理 <16 ms）。
5. 单源超时后自动跳过，不抛异常、不影响其它源，状态栏显示"XX 源超时已跳过"。
6. 代码内**不得出现** `Win32_Product` / `SELECT * FROM Win32_Product` 字样（架构评审 grep 校验）。

**自动化点**：全自动扫描（无需用户指定路径）；自动过滤噪音；自动补全缺失字段；自动超时兜底。

#### R2 去重、排序与"值得分身"推荐

**描述**：按第 2 节的去重策略与分类词典实现 `AppClassifier` / `AppMerger`，输出三档推荐标签。

**验收标准**
1. 同一 exe 在结果中**只出现一次**；同名不同路径的候选取优先级最高者。
2. 结果按 推荐度 → 已添加沉底 → 名称 排序；分类筛选（全部/浏览器/社交/游戏/开发/云盘/其它）切换后列表 ≤200 ms 内重绘完成。
3. 人工核对：发现页首屏前 20 条中"值得分身"应用占比 ≥80%。
4. 「不建议」档（运行时/系统组件/驱动/卸载器/安全软件）条目数占全量比 ≥30%（说明过滤生效）且不含任何已知的分身高价值应用。

**自动化点**：自动归类、自动打分排序、自动排除。

#### R3 「发现应用」对话框（UI，见第 7 节线框）

**描述**：新增 `DiscoverForm`，替代"只能手选 exe"的单一入口；顶栏「＋添加应用」改为下拉，含「从本机已装应用添加（推荐）/ 手动选择 exe」。

**验收标准**
1. 对话框风格与 `Ui.cs` 一致：`Ui.Bg` 背景、白卡片 `AppleCard`（Radius 14、DrawBorder、Shadow）、按钮 `AppleButton`（Filled/Secondary/Ghost）、标签 `PillChip`（分类与推荐度标签沿用 `ChipPurple` 系配色）。
2. 含：搜索框（名称/发布者模糊匹配，输入后 ≤150 ms 出结果）、分类 PillChip 筛选栏、扫描进度条与文案、已添加置灰、全选/全不选、底部「添加所选（N）」。
3. 支持复选框多选 + `Ctrl/Shift` 连选；列表虚拟化不要求，但 ≥500 条时滚动无明显卡顿。
4. 空态/失败态有明确文案与「重试」按钮。

**自动化点**：默认勾选推荐项（首屏"推荐分身"条目默认选中）；搜索/筛选即时生效。

#### R4 一键批量添加 + 批量创建分身

**描述**：勾选后点击「添加所选」，批量写入 `Store`；可选"同时为每个应用创建 N 个分身"（N=1/2/3 下拉），并可勾"同时生成桌面快捷方式"。

**验收标准**
1. 一次添加 10 个应用（各建 2 个分身）**总耗时 ≤10 s**（后台并行提取图标，主线程不卡）。
2. 添加后 `apps.json` 中 10 条 `AppEntry` + 20 条 `CloneEntry` 结构正确，主界面左侧列表即时刷新且数量正确。
3. 应用名取自扫描结果 `DisplayName`（如 "微信"、"Microsoft Edge"），**不再**是文件名（如 `WeChat` / `msedge`）。
4. 图标自动提取成功率：有 `DisplayIcon`/exe 的条目 ≥95%；失败时回落到默认图标且不崩溃。
5. 「同时生成桌面快捷方式」勾选后，桌面出现对应 .lnk，双击能启动对应分身。

**自动化点**：批量入库、自动命名、批量提图标（并发 + 失败重试 1 次）、批量建分身与快捷方式。

#### R5 扫描结果缓存与增量刷新

**描述**：扫描结果落盘到 `data\cache\discovered.json`（含 `ScannedAt`、`SourceVersion`、条目列表）；启动时先渲染缓存再后台增量刷新。

**验收标准**
1. 二次打开发现页 **≤0.3 s** 出现列表（读缓存），后台静默刷新完成后无闪烁替换。
2. 增量判定：读取 Uninstall 三键的子键数量与 `RegQueryInfoKey` 的最后写入时间做廉价指纹；指纹未变则**跳过全量重扫**；变化则仅重扫变化键。
3. 提供「重新扫描」按钮，触发全量并覆盖缓存。
4. 缓存文件损坏时自动删除重建，不崩溃（与 `Store.Load` 相同的容错风格）。

**自动化点**：自动缓存、自动增量判定、自动修复损坏缓存。

#### R6 性能：扫描不卡 UI

**描述**：扫描、图标提取、快捷方式解析全部后台线程；结果按批（每 20 条或每 100 ms）`BeginInvoke` 回填 UI；提供 `CancellationToken` 支持取消（关闭对话框/重新扫描时取消旧任务）。

**验收标准**
1. 扫描期间主窗口可正常拖动、缩放、切换应用，无"无响应"。
2. 关闭对话框时扫描任务在 **≤500 ms** 内响应取消并退出。
3. 窗口关闭/程序退出时无遗留后台线程（进程可正常退出，无残留 `AppClone.exe`）。

**自动化点**：自动并发调度、自动取消、自动清理。

---

### P1 — 应该有

#### R7 强制规则（Always Run as Clone）
指定 exe 后，双击其任意入口（原快捷方式/开始菜单）也走分身环境。实现路径：接管该 exe 的 `App Paths\xxx.exe` 的 `(Default)` 值指向 AppClone 启动器（自带原路径参数），或生成替换型桌面快捷方式。
**验收**：设置后双击原图标进入分身环境（`FENSHEN_CLONE` 环境变量可见）；关闭规则后完整还原注册表/快捷方式；卸载 AppClone 时自动全部还原。
**自动化点**：自动接管与自动还原，无需用户手工改注册表。

#### R8 批量快捷方式 + 自定义分身名称/颜色/图标
**验收**：右键分身可改名（"工作号"）并选择 6 色角标；批量生成快捷方式时名称格式为 `应用名 - 分身名`。
**自动化点**：默认名按 `应用名 - 分身N` 自动生成，用户可覆盖。

#### R9 应用类型自适应参数规则表扩展
把 `Launcher.BuildAutoArgs` 的硬编码 if 改为规则表（可存 `data\launch_rules.json`，内置默认 + 用户可加）：
Chromium 系（`--user-data-dir`）、Firefox 系（`-profile -no-remote`）、Electron/CEF（探测目录下 `.asar`/`resources` 后附加 `--user-data-dir`）、微信/QQ/Telegram 等（仅深度隔离，不加参）。
**验收**：新增一条规则无需改代码；现有 8 种 Chromium + Firefox 行为**完全不变**（回归验证）。

#### R10 分身快照与一键回滚
**验收**：重置/删除分身前可勾选"先存快照"；快照列表可一键恢复；快照存于 `data\snapshots\`，占用超过 2 GB 时提示清理。
**自动化点**：危险操作前自动建议快照；自动按时间命名。

#### R11 一键结束全部运行分身
**验收**：主界面新增「结束全部」按钮，一次结束所有运行中分身并刷新状态；有确认弹窗。

#### R12 UWP 应用的"发现但标记不支持"
**验收**：UWP 条目出现在发现页但灰显并标注「UWP 应用暂不支持分身」；不提供添加按钮。避免用户困惑。

#### R13 游戏平台库扫描（Steam / Epic，可开关）
**验收**：Steam 路径从 `HKCU\Software\Valve\Steam` 的 `SteamExe` 读取，`libraryfolders.vdf` 按 **v1 格式**（扫 `appmanifest_*.acf`）解析，兼容 v2；Epic 读 `%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item`；设置里可关闭。

---

### P2 — 锦上添花（本次不承诺，仅供架构预留）

| # | 需求 | 说明 |
|---|---|---|
| R14 | winget list 作为补充源 | 默认关闭；必须 `--disable-interactivity` + 8 s 超时；无 winget 优雅降级 |
| R15 | 手动指定目录扫描 | 用户选文件夹，递归找 .exe（限深 3 层、限 5000 文件） |
| R16 | 配置导入/导出（JSON，不含分身数据；含数据的 7z 归档视可行性再定） | 需系统 `tar.exe` 或自研，成本较高 |
| R17 | 托盘常驻 + 快速启动 | |
| R18 | 多开能力预检提示 | 探测命名互斥体，给出"预计可多开/可能受限" |
| R19 | 隐私锁/隐藏分身 | 桌面场景优先级低 |

---

## 6. 性能优化需求（单列）

> 独立的验收口径，架构师需给出每项的前后对比数据。

| # | 项目 | 现状/问题 | 优化要求 | 验收标准 |
|---|---|---|---|---|
| P-1 | **冷启动耗时** | `MainForm` 构造同步 `Store.Load()` + `RefreshAppList()` + 逐个 `AppIcons.Load()`（每次 `Image.FromStream` 一次磁盘 IO） | ① 图标加内存缓存 `Dictionary<string, Image>`（LRU 上限 200）；② 列表图标懒加载，仅渲染可见项；③ `Store.Load` 保持在启动路径但确保 <50 ms | 20 个应用的库，冷启动（进程起到首帧绘制）**≤1.5 s**，较当前提升 ≥30% |
| P-2 | **UI 重建风暴** | `RefreshAppList()` 每次 `Controls.Clear()` 全量重建；`SelectApp()` 与 `RefreshRightPane()` 常成对调用导致**重建两次** | 合并刷新入口；仅在数据真正变更时重建；卡片 hover 动画定时器在不可见时停止 | 切换选中应用时列表重建次数由 2 降为 1；10 个应用下单次切换 **≤50 ms** |
| P-3 | **运行状态轮询开销** | `_timer` 每 **1500 ms** 无条件 `UpdateRunningStates()`；其中 `ScanCommandLineMarker` 会 `Process.GetProcessesByName` + `OpenProcess` + `ReadProcessMemory`，对浏览器类分身是持续开销；`_pulse` 定时器 60 ms 常驻触发重绘 | ① **无运行分身时暂停 `_timer`**（或降频到 5000 ms），有运行分身时回到 1500 ms；② `ScanCommandLineMarker` 结果缓存由 5 s 提至 10 s 且仅在 Job/主进程判定失败时才走；③ `_pulse` 仅在存在运行中分身时启动 | 空闲（0 个运行分身）时进程 CPU 占用 **<0.5%**，定时器回调次数降为 0；有 3 个运行分身时 CPU <2% |
| P-4 | **图标 IO** | 每次加载都 `new FileStream` + `Image.FromStream`；缺失图标时 `Task.Run(Extract)` 后 `RefreshAppList()` 全量重建 | ① `AppIcons` 增加内存缓存与 `LoadAsync`；② 补提取完成后只更新**对应那一张卡片**，不整表重建；③ 扫描页图标统一 32px（比 48px 少 ~55% 体积） | 20 个应用滚动列表时磁盘读次数 = 0（全缓存命中）；补图标完成后无可见闪烁 |
| P-5 | **注册表快照耗时** | `RegClone.EnsureCloneRegistry` 在**启动分身的同步路径**上执行，首次逐键复制 HKCU 可能数百毫秒，直接卡住"启动"按钮 | ① 首次快照移到后台线程并在 UI 显示"准备隔离环境…"；② 已就绪的分身用标记文件跳过重复快照 | 首次启动深度隔离分身，UI 无 >100 ms 阻塞；第二次启动同分身快照耗时 ≈0 |
| P-6 | **扫描性能** | 新增功能 | 见 R1/R6 | 见第 2 节性能表 |
| P-7 | **内存** | 图标与扫描结果常驻 | 扫描结果在对话框关闭后释放；图标缓存 LRU 上限 200 张 | 打开/关闭发现页 5 次后，内存增量 ≤20 MB |

---

## 7. UI 设计稿

### 7.1 顶栏入口变更（MainForm）

```
┌──────────────────────────────────────────────────────────────────────┐
│ [ICO] 应用分身                              [数据目录] [＋ 添加应用 ▾] │
│       数据隔离 · 沙盒多开                                  │           │
└───────────────────────────────────────────────────────────┼───────────┘
   点击 ▾ 弹出（AppleButton Ghost 风格菜单）：                 │
   ┌────────────────────────────────┐  ◄─ 圆角 12，Ui.Card 白底
   │ 🗂  从本机已安装应用添加  推荐   │     （首项，蓝字加粗）
   │ 📄  手动选择 exe 文件…          │
   └────────────────────────────────┘
```

### 7.2 「发现应用」对话框（DiscoverForm，新增）

尺寸 900×640，可缩放，最小 760×520；`Ui.Bg` 背景，标题栏沿用顶栏的渐变发丝线；`FadeAnimator.FadeIn` 淡入。

```
┌──────────────────────────────────────────────────────────────────────────┐
│  发现本机已安装的应用                                        [_][□][×]    │
│  自动扫描已完成 · 共 412 项 · 推荐分身 18 项        [⟳ 重新扫描]          │
├──────────────────────────────────────────────────────────────────────────┤
│  🔍 [ 搜索应用名称或发布者…                              ]                │
│                                                                          │
│  (全部●) (浏览器) (社交聊天) (游戏) (开发工具) (云盘) (其它)              │  ◄─ PillChip
│                                                                          │
│  ┌────────────────────────────────────────────────────────────────────┐  │
│  │ ☑ [ICON] 微信                        社交聊天   3.9.12  腾讯        │  │
│  │         C:\Program Files\Tencent\WeChat\WeChat.exe    [推荐分身]    │  │  ◄─ AppleCard
│  ├────────────────────────────────────────────────────────────────────┤  │    Radius 14
│  │ ☑ [ICON] Microsoft Edge              浏览器     120.0.1  Microsoft  │  │    白底+描边
│  │         C:\Program Files (x86)\Microsoft\Edge\...\msedge.exe [推荐] │  │
│  ├────────────────────────────────────────────────────────────────────┤  │
│  │ ☑ [ICON] 腾讯QQ                      社交聊天   9.9.16  腾讯        │  │
│  │         C:\Program Files\Tencent\QQ\Bin\QQ.exe          [推荐分身]  │  │
│  ├────────────────────────────────────────────────────────────────────┤  │
│  │ ☐ [ICON] Visual Studio Code          开发工具   1.89.0  Microsoft   │  │
│  │         C:\Users\xu\AppData\Local\Programs\...\Code.exe  [推荐分身] │  │
│  ├────────────────────────────────────────────────────────────────────┤  │
│  │ ▨ [ICON] 微信（已添加）              社交聊天   —       腾讯        │  │  ◄─ 已添加：
│  │         C:\...\WeChat.exe                              [已添加]     │  │    背景 FillGray
│  ├────────────────────────────────────────────────────────────────────┤  │    复选框禁用
│  │ ▨ [ICON] Netflix                     UWP        —       Netflix    │  │  ◄─ UWP：灰显
│  │         shell:appsFolder\4DF9E0F8...!Netflix.App  [暂不支持分身]   │  │
│  └────────────────────────────────────────────────────────────────────┘  │
│                                                                          │
│  ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓░░░░░░░░  正在扫描：开始菜单快捷方式 (1/3)      │  ◄─ 扫描中显示
│                                                                          │    进度条(Accent)
├──────────────────────────────────────────────────────────────────────────┤
│  ☐ 仅显示「推荐分身」        ☑ 全选     已选 3 项                        │
│                                                                          │
│  为每个应用创建分身： [ 2 ▾] 个    ☑ 同时生成桌面快捷方式                │
│                                                                          │
│                              [ 取消 ]  [ 添加所选（3） ]                 │  ◄─ Secondary / Filled
└──────────────────────────────────────────────────────────────────────────┘
```

**交互与视觉说明（严格对齐 `Ui.cs`）**

| 元素 | 实现 |
|---|---|
| 窗口背景 / 卡片 | `Ui.Bg`(#F5F5F7) / `AppleCard`（`Radius=14`, `DrawBorder=true`, `Shadow=true`, `BackColor=Ui.Card`) |
| 主按钮「添加所选（N）」 | `AppleButton(Style.Filled)`，尺寸 140×34；N=0 时 `Enabled=false`（禁用态沿用 `CurrentText()` 的灰色） |
| 次按钮「取消」「重新扫描」 | `AppleButton(Style.Secondary)` |
| 分类筛选 | `PillChip`，选中态 `Ui.AccentLight` 底 + `Ui.AccentDark` 字；未选 `Ui.FillGray` 底 + `Ui.TextSecondary` 字 |
| 「推荐分身」标签 | `PillChip(text, Ui.ChipPurple, Ui.ChipPurpleText)`；「已添加」用 `Ui.FillGray` + `Ui.TextSecondary`；「暂不支持」用 `Ui.Danger` 描边（`outline:true`） |
| 图标 | 32×32 `PictureBox`（`SizeMode=Zoom`），异步加载 + 内存缓存；默认占位用系统图标 |
| 文字 | 名称 `Ui.Font(10F, bold)` + `Ui.TextPrimary`；路径 `Ui.Font(8F)` + `Ui.TextSecondary`（过长省略号截断，Tooltip 显示全路径） |
| 进度条 | 自绘圆角条：底色 `Ui.FillGray`，进度 `Ui.Accent`，高度 4，圆角 2；文案 `Ui.Font(8.5F)` + `Ui.TextSecondary` |
| 窗口圆角 | `Ui.RoundWindow(this)`；淡入 `FadeAnimator.FadeIn(this)` |
| 空/失败态 | 空：`Ui.TextSecondary` 文案 + 「重新扫描」按钮；失败：`Ui.Danger` 文案 + 「重试」 |

### 7.3 主界面微调（增量）

- 侧栏空态文案改为：`还没有应用\n\n点击右上角「＋添加应用」\n选择「从本机已安装应用添加」\n30 秒即可开始多开` + 一个 Secondary 按钮「立即扫描本机应用」。
- 状态栏（`_statusText`）增加扫描/批量操作进度文案：`正在添加 3 个应用…（2/3）`。
- 右侧分身卡片新增「改名 / 设颜色」入口（P1，右键菜单增加项）。

---

## 8. 数据模型变更与向后兼容

**铁律**：老 `apps.json` 必须能直接加载、数据不丢；所有新增字段**带默认值**且不改字段名/类型。

```csharp
// Store.cs —— 仅新增字段，不删改既有字段
public class AppEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string Args { get; set; } = "";
    public DateTime AddedAt { get; set; } = DateTime.Now;

    // ↓ 新增（全部有默认值，老文件反序列化后取默认值）
    public string Source { get; set; } = "manual";   // manual | uninstall | apppaths | startmenu | steam | epic | winget
    public string Publisher { get; set; } = "";      // 发布者，用于筛选与展示
    public string Version { get; set; } = "";        // DisplayVersion
    public string Category { get; set; } = "";       // browser | social | game | dev | cloud | other
    public bool Recommended { get; set; }            // 是否在发现页被判为「推荐分身」
}

public class CloneEntry
{
    // ...既有 4 个字段不变...
    public string Color { get; set; } = "";          // P1：分身角标颜色（空=默认蓝）
    // Name 继续沿用，P1 允许改名（默认值仍为 "分身 N"）
}

// 新增：扫描缓存（独立文件，不混进 apps.json）
// 路径：Paths.CacheDir + "discovered.json"
public class DiscoveredApp { /* Name, ExePath, Publisher, Version, InstallLocation,
                               Source, Category, Recommended, IconPath, FoundAt */ }
public class DiscoveryCache { public DateTime ScannedAt; public string MachineId;
                              public List<DiscoveredApp> Apps = new(); }
```

**兼容要求**
1. `StoreModel` 结构（`apps` 数组 + `clones` 字典）**不得变动**。
2. 反序列化失败时的现有容错（备份 `.bak` + 空仓库启动）**必须保留**。
3. `Paths` 新增 `CacheDir => Path.Combine(DataRoot, "cache")` 并在 `EnsureAll()` 中创建；**不改变** `DataRoot / StoreFile / IconsDir / ProfilesDir / SandboxDir` 的既有行为（`FENSHEN_DATA_DIR` 测试覆盖仍生效）。
4. 卸载/删除应用时的清理逻辑（`icons\<id>.png`、`profiles\<id>`）需同步清理该应用的缓存条目。

---

## 9. 约束（硬性，必须遵守）

1. **零第三方依赖**：不得引入任何 NuGet 包。只允许 .NET 10 BCL（`Microsoft.Win32.Registry` / `System.Text.Json` / `System.Drawing` / `System.Diagnostics`）+ 原生 Win32 P/Invoke（`advapi32`/`shell32`/`kernel32`）+ 系统自带 CLI（PowerShell / winget，均须可优雅降级）。`AppClone.csproj` 的 `ItemGroup` 除现有 `..\Shared\Shortcuts.cs` 外不得新增 `PackageReference`。
2. **向后兼容**：第 8 节全部要求；老用户数据零丢失。
3. **不破坏隔离能力**：`src\sandbox_dll\fenshen.c` 的四层机制、环境变量与注入流程**不建议改动**；如确需改动，必须 `bash scripts/test.sh` **15/15 全绿**。
4. **禁用 `Win32_Product`**：任何代码路径不得查询该 WMI 类（会触发 MSI 一致性检查与静默修复）。
5. **UI 全中文**，风格延续 `Ui.cs`（`AppleCard` / `AppleButton` / `PillChip`，Apple 系统色板），字体 `Microsoft YaHei UI`。
6. **所有扫描 IO 在后台线程**，UI 线程零阻塞，且支持取消。
7. **不写入任何系统级持久变更**（注册表/快捷方式）除非用户显式确认（P1 的 R7 强制规则例外，但必须提供一键还原并在卸载时还原）。
8. **不得降低启动速度**：新增功能默认不增加冷启动耗时（走缓存 + 懒加载）。

## 10. 非目标（本次明确不做）

- 虚拟定位 / GPS 模拟（F13）
- 网络隔离、每分身防火墙、SOCKS5 代理、DNS 控制（F12，需 WFP/驱动）
- 加密沙箱（F16）
- 修改目标应用二进制文件（补丁式多开，如 RevokeMsgPatcher 路线）——仅做运行时隔离
- 全盘 exe 扫描（默认）
- 云端同步 / 账号体系

## 11. 待确认问题

| # | 问题 | 我方默认建议 | 需谁拍板 |
|---|---|---|---|
| Q1 | **扫描结果是否落盘缓存？**（涉及隐私：会记录本机软件清单到 `data\cache\discovered.json`） | 落盘，但仅存名称/路径/发布者/版本，**不上传**；设置里提供「关闭扫描缓存」开关 | 用户 |
| Q2 | **默认扫描范围是否包含 UWP/微软商店应用？** | 扫描并列出，但**默认灰显「暂不支持分身」**（UWP 无法注入 DLL，四层隔离失效） | 用户 + 架构师 |
| Q3 | **是否默认启用游戏平台库（Steam/Epic）扫描？** | 默认**开启 Steam**（游戏多开是高频场景），Epic 默认关闭（manifest 格式稳定性较差） | 用户 |
| Q4 | **winget 是否纳入？** | 默认**关闭**（慢、可能弹源协议、可能联网），设置里可开 | 架构师 |
| Q5 | **绿色版/便携软件（无卸载项）是否收录？** | 收录（通过开始菜单快捷方式 + App Paths 源），但标注来源为「快捷方式」，提示"可能不完整" | 产品（我判定即可） |
| Q6 | **「推荐分身」词典是否需要用户可编辑？** | P0 内置只读；P1 提供 `data\categories.json` 允许用户自定义关键词 | 用户 |
| Q7 | **批量添加时默认创建几个分身？** | 默认 **1 个**（保守，避免误创建大量目录占空间），下拉可选 1/2/3 | 用户 |
| Q8 | **强制规则（R7）改 `App Paths` 注册表是否可接受？**（会影响全系统对该 exe 的启动） | 若担心风险，可用"替换桌面快捷方式"的温和方案替代；需用户选择 | 用户 + 架构师 |
| Q9 | **分身快照（R10）是否需要压缩？**（不引 NuGet 则只能用系统 tar.exe 或纯复制） | P1 先做**纯目录复制**的快照（简单可靠），压缩留待后续 | 架构师 |
| Q10 | **是否需要英文界面？** | 本次全中文，不做多语言 | 用户 |

## 12. 建议的交付切分

| 阶段 | 内容 | 验收 |
|---|---|---|
| M1（P0 核心） | R1 扫描引擎 + R2 去重推荐 + R3 发现对话框 + R4 批量添加 + R5 缓存 + R6 性能 | 第 3 节 G1–G5 全达标；15/15 测试仍绿 |
| M2（性能） | P-1 ~ P-7 全部性能项 | 每项前后对比数据入档 |
| M3（P1） | R7 强制规则 / R8 自定义 / R9 规则表 / R10 快照 / R11 结束全部 / R12 UWP 标注 / R13 游戏库 | 逐条验收标准 |
