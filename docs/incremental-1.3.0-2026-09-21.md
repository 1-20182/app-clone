# 应用分身 AppClone v1.3.0 增量交付记录（F1 / F2 / F3）

- 日期：2026-09-21
- 来源：`docs/review-mapping-2026-09-21.md` 第 3 节的 P0 三项（F1 兼容性数据库 / F2 源应用更新检测 / F3 预检查 + Dry Run）
- 版本：`1.2.0` → `1.3.0`（`src/AppClone/AppClone.csproj:11`、设置面板「关于」）
- 交付口径：**Dry Run 默认开启**（用户指定）

---

## 0. 一句话总结

把评审稿里「用户撞墙才知道」的三件事，变成了「事前就知道」：**能不能分身**（F1）、**源应用变了没有**（F2）、
**这次启动到底会干什么**（F3）。隔离核心（`Injector` / `RegClone` / `fenshen.c`）零改动。

---

## 1. 文件清单

### 新增（5 个）

| 文件 | 职责 |
|---|---|
| `src/AppClone/Core/CompatibilityDb.cs` | 兼容性数据库：内置推断 + 自学习 + 用户标记，落盘 `data\compat.json` |
| `src/AppClone/Core/LaunchWatch.cs` | 启动观察窗：6 秒后发现异常早退即回写兼容性（F1 的执行者） |
| `src/AppClone/Core/SourceWatch.cs` | 源应用基线打标与变化检测（版本号 + 时间戳 + 大小） |
| `src/AppClone/Core/Preflight.cs` | 启动前预检查 + PE 位数识别（零依赖手工解析 PE 头） |
| `src/AppClone/Forms/DryRunForm.cs` | 启动预览窗口（只读文本框 + 自动滚动，零重叠布局） |

### 新增（验证设施）

| 文件 | 职责 |
|---|---|
| `test/probe/probe.csproj` + `test/probe/Program.cs` | 37 项断言的逻辑回归探针，引用编译产物，数据目录重定向到临时目录 |
| `scripts/probe.cmd` | 一键「编译主程序 + 跑探针」 |

### 修改（12 个）

| 文件 | 改动 |
|---|---|
| `Core/Paths.cs` | 新增 `CompatFile` |
| `Core/Theme.cs` | `ThemeRole` 末尾追加 `OkLight / Warn / WarnLight / DangerLight`（不改既有顺序）+ 两套色板 |
| `Ui.cs` | 新增 4 个主题颜色访问器 |
| `Core/Store.cs` | `CloneEntry` 增 3 个源基线字段（带默认值）；`AddClone` / `AddAppFromDiscovery` 打标 |
| `Core/Settings.cs` | 新增 `ShowDryRun`（默认 **true**）、`MinFreeDiskGB`（默认 2） |
| `Core/Launcher.cs` | 抽出 `BuildEnv` 供启动与预览共用；新增 `Describe` + `LaunchPlan` 记录 |
| `Core/Program.cs` | 无头启动路径加阻断级预检查（不弹预览，保持快捷方式一键直达语义） |
| `MainForm.cs` | 兼容性徽章（应用卡 + 分身卡）、升级提示条、启动链路接入预检查/预览/观察窗、7 个新方法 |
| `Forms/SettingsForm.cs` | 「启动前显示执行预览」开关 + 磁盘阈值数字框 |
| `AppClone.csproj` | 版本 1.3.0 |
| `README.md` | 新增 v1.3.0 功能、目录结构、构建/验证/已知边界条目 |
| `scripts/probe.cmd` | 新增 |

---

## 2. F1 兼容性数据库

### 2.1 三级信息来源（优先级由低到高）

| 级别 | 来源 | 是否落盘 | 说明 |
|---|---|---|---|
| 1 | 内置推断 `Infer` | 否 | UWP → `Unsupported`；`LaunchRules.BuiltIn` 命中的浏览器内核 → `Supported`；其余 → `Unknown` |
| 2 | 自学习 `learned` | 是 | 启动观察窗的结果；成功 → `Supported`，失败 → `Partial`，连续 3 次失败 → `Unsupported` |
| 3 | 用户标记 `user` | 是 | **自学习不再覆盖**（`Report` 首行即返回） |

内置推断刻意不落盘：`compat.json` 只保存「实测过 / 用户标记过」的结论，保持文件精简且可随时清空回归推断。

### 2.2 状态机

```
Unknown ──启动成功──> Supported
Unknown ──启动失败──> Partial ──连续失败≥3──> Unsupported
任意     ──用户标记──> 强制该档（Source=user，不可被自学习改写）
任意     ──Reset────> 回到内置推断
```

### 2.3 观察窗的误判防护（`LaunchWatch`）

判定成功有三种路径，只为「少报失败」：
1. 观察窗（6 s）结束时进程仍存活；
2. 进程已退出但**退出码为 0**；
3. 进程已退出但 `Launcher.IsRunning` 仍为真 —— 覆盖 Chromium 类「交出主进程、由子进程接管」的情况
   （Job Object 会跟踪整棵进程树）。

失败只在「非 0 退出 且 整棵树已消失」时记录。用户主动结束（`StopClone` → `MarkUserStopped`）
在观察窗中直接消费标记并跳过，**不参与判定**。

### 2.4 落盘格式

`data\compat.json` 是数组，枚举用 `JsonStringEnumConverter` 以可读字符串持久化以便人工编辑：

```json
[ { "Match": "wechat.exe", "Level": "Supported", "Note": "用户确认可用",
    "Source": "user", "SuccessCount": 1, "FailCount": 3, "ConsecutiveFails": 3,
    "UpdatedAt": "2026-09-21T12:56:04+08:00" } ]
```

损坏时另存 `.corrupt` 并以空库启动 —— 与主数据（`apps.json`）完全隔离，坏掉不影响分身。

---

## 3. F2 源应用更新检测

### 3.1 基线三元组

`CloneEntry` 新增三个字段（全部带默认值，老 `apps.json` 反序列化后为空）：

| 字段 | 内容 |
|---|---|
| `SourceVersionAtCreate` | `FileVersionInfo.FileVersion` → `ProductVersion` → 空串 |
| `SourceExeMtimeAtCreate` | 文件最后写入时间（UTC，ISO 8601 往返格式） |
| `SourceExeSizeAtCreate` | 文件字节数 |

打标时机：`Store.AddClone`、`Store.AddAppFromDiscovery`、`F2 重建`、`仅更新版本记录`。

### 3.2 检测结果

`SourceWatch.Check(app, clones, out baseline, out current, out reason)`：

| 返回 | 触发条件 | 处置 |
|---|---|---|
| `PathMissing` | 源 exe 已不存在（判在最前） | 提示条：克隆记录可能已失效 |
| `VersionUpgraded` | 版本号与基线不同 | 提示条：`旧 → 新`，建议重建 |
| `ExeReplaced` | 版本号相同但时间戳/大小变了 | 提示条：文件已被替换（静默更新场景） |
| `None` | 三项均一致 | 不显示提示条 |
| `Unknown` | **该应用没有任何分身带基线** | 不显示 —— 由 `BackfillSourceBaselines` 静默回填 |

`Unknown` 与 `None` 的区分是关键：老用户升级到 1.3.0 时全部数据都没基线，
若把「无基线」当作「已升级」会造成全量误报。因此启动时先静默回填基线，此后再检测才有效。

### 3.3 重建语义（本项目特有）

本项目**不复制应用本体**，所以「重建」不是「重新克隆」：

1. `StopClone`（若在运行）
2. `Snapshot.Create` 先存快照（快照失败仅告警，不阻断）
3. 删除 `data\profiles\<appId>\<cloneId>` 并重建空 home
4. `RegClone.ResetCloneRegistry`（仅深度隔离）
5. `SourceWatch.Stamp` 推进基线

保留：`Id` / `Name` / `Color` / `Hotkey` / `DeepIsolation` / `DeviceClone`。
→ 分身身份与假设备指纹不变，用户的账号不会「换了一台机器」。

---

## 4. F3 预检查 + Dry Run

### 4.1 预检查项（`Preflight.Check`）

| 级别 | 检查项 | 阻断 |
|---|---|---|
| Error | 目标程序不存在（含路径为空） | **是** |
| Error | 数据目录不可写（写临时文件后立即删除） | **是** |
| Warn | 磁盘剩余空间低于 `Settings.MinFreeDiskGB` | 否 |
| Warn | 注入组件缺失或位数不匹配（按目标 PE 位数精确判定） | 否 |
| Info | 未以管理员身份运行 | 否 |

接入点：`MainForm.NewCloneFlow` 之后的启动链路 `LaunchClone`、批量新建、以及 `Program.LaunchHeadless`。

### 4.2 Dry Run

- 默认开（`Settings.ShowDryRun = true`）。
- 展示了什么见 `DryRunForm.BuildText`：预检查结果 → 目标/分身/隔离模式 → 编号步骤 →
  环境变量逐项映射 → 注册表私有键 + 注入组件 + 附加参数，末尾明确声明「真实 HKCU / HKLM 不会被修改」。
- 「以后不再显示」勾选后写回 `Settings.ShowDryRun = false`（可逆）。
- 关闭 Dry Run 后，仍会把第一条 `Warn` 回显到状态栏，保证警告不静默丢失。
- 无头模式（桌面快捷方式）**不弹预览**，只做阻断级检查 —— 保持「一键直达」的语义。

### 4.3 防漂移设计（本次最关键的一处重构）

预览与实际启动如果各写一份环境变量逻辑，迟早会漂移。因此：

```csharp
// Launcher.cs
private static Dictionary<string, string?> BuildEnv(AppEntry app, CloneEntry clone)  // 唯一实现
public static LaunchOutcome Launch(...)   // 调用 BuildEnv
public static LaunchPlan Describe(...)    // 调用同一个 BuildEnv
```

`Describe` 是只读的（不创建进程、不改目标程序），唯一副作用是 `RegClone.EnsureDeviceFake`
—— 它是幂等的（`DeviceName` 与 `Mirrored` 标记持久化），且**必须**调用才能让预览显示真实的假设备名。

---

## 5. 主题与 UI

`ThemeRole` 末尾追加 `OkLight / Warn / WarnLight / DangerLight`（浅色 `#E7F8EC / #FF9500 / #FFF4E6 / #FFECEB`，
深色 `#14301B / #FF9F0A / #3A2A12 / #3A1E1D`），并补 4 个访问器。
**追加在枚举末尾**：既有成员顺序不变，避免任何按序号取值的老逻辑受影响。

徽章四态配色：`✓ 可用` → OkLight/Ok，`⚠ 部分` → WarnLight/Warn，`✗ 不支持` → DangerLight/Danger，
`— 未测试` → FillGray/TextSecondary。

### 停靠顺序的坑（务必记住）

`BuildRightPane` 中 `_upgradeBar` 必须写在 `_cloneFlow` 之后、`header` 之前：

```csharp
right.Controls.Add(_cloneFlow);    // index 0 → Dock=Fill，最后填充
right.Controls.Add(_upgradeBar);   // index 1 → Dock=Top，第二个占顶边
right.Controls.Add(header);        // index 2 → Dock=Top，最先占顶边
```

WinForms 从 `Controls` 集合**末尾向前**依次停靠。因此：

- 想让 header 在最顶部，它就必须是最后加入的那个；
- **`_upgradeBar` 绝不可调用 `BringToFront()`** —— 那会把它挪到 index 0 变成「最后停靠」，
  而最后一名的 `_cloneFlow` 是 `Dock=Fill`，会先吃掉整个客户区，布局当场崩掉。

---

## 6. 数据兼容

- `CloneEntry` 三个新字段全部带默认值，老 `apps.json` 载入后为空、由启动回填补齐，**不产生误报**。
- `clone.Color` 是十六进制串（值域不变）—— 与 `CloneColors` 的 6 色板无冲突，本轮未改动。
- `Settings` 新增两字段均为追加，缺字段取默认值。
- 实测：探针内置一条 v1.2.0 格式的无字段 `apps.json`，载入后应用/分身不丢、新字段为默认、
  隔离开关保持默认 true。

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
结果：**PASS=37  FAIL=0  SKIP=0**（退出码 0）。覆盖范围见 README「逻辑回归探针」小节。

> 探针把 `FENSHEN_DATA_DIR` 重定向到 `test\probe\bin\Release\net10.0-windows\tmp\`，
> 全程不读写用户真实数据目录，也不启动 GUI。
>
> 复现说明：首轮第 25 项 `FAIL 无基线的老数据 → Unknown` 属于**用例顺序错误**（断言跑在删除源文件之后，
> 于是 `Check` 正确地先命中了 `PathMissing`），调整顺序后通过 —— 代码无缺陷，但这也侧面验证了
> `Check` 的判定优先级是「路径存在性 → 版本号 → 文件指纹」。

### 7.3 尚未覆盖、需人工确认

隔离能力测试 `bash scripts/test.sh` 需在完整 Git Bash 中运行（本环境 bash 指向损坏的 WSL）；
隔离核心本轮零改动，预期无回归，但**未在本轮实跑**。

以下项需要在本机 GUI 中目测（本环境不启动 GUI）：

- [ ] 应用卡与分身卡上的四态徽章正确显示、窄卡片下与运行计数同行不重叠
- [ ] 源应用真升级后，右栏顶部提示条出现且两条按钮可用
- [ ] Dry Run 窗口在窄窗/长路径下的滚动与布局
- [ ] 深度色主题下徽章、提示条配色正确
- [ ] 关闭 Dry Run 后启动不再弹窗（设置面板可重新开启）

---

## 8. 后续（本次未做）

| 项 | 内容 | 优先级 |
|---|---|---|
| F4 | 克隆管理中心（跨应用总览表格 + 行内操作 + 日志查看） | P1 |
| F5 | 日志分级（INFO/WARN/ERROR/DEBUG）+ 结构化崩溃报告 + 回滚记录 | P1 |
| F6 | 角标图标落到桌面快捷方式与任务栏分组（AppUserModelID） | P2 |
| F7 | README 支持应用矩阵 / FAQ / 原理流程图 / Roadmap | P2 |

F4 可直接复用本轮成果：`CloneStats.Get`（运行统计）+ `CompatibilityDb.Resolve`（兼容性）
+ `SourceWatch.Check`（源变化）三者联表即可。
