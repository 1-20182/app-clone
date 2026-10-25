# v1.4.1 高 DPI 显示不全修复（2026-09-21）

## 现象（用户真机反馈）

125%/150% 缩放下大面积「显示不全」：安装向导提示文字被裁、主界面卡片文字被裁、
添加应用列表行显示不全——几乎所有手写像素布局的 UI 都中招。

## 根因

- 工程 `ApplicationHighDpiMode=PerMonitorV2`，字体按**点数**创建（`Ui.Font(pt)`）→
  高 DPI 下 GDI 文本渲染自动放大 1.25/1.5 倍；
- 而全部窗体**零处 `AutoScaleMode`**，手写像素布局原地不动 → 容器不变、文字变大 → 全被裁。

## 修复方案（三种场景三套口径）

1. **构造期 UI**（全部对话框窗体）：`AutoScaleMode = Dpi; AutoScaleDimensions = (96,96)`，
   加载时框架整树缩放。涉及 MainForm / DiscoverForm / CloneManagerForm / LogViewerForm /
   DryRunForm / SnapshotsForm / PromptText 动态对话框。
2. **运行期动态子树**（AutoScale 救不了）：创建完成后显式
   `card.Scale(new SizeF(Ui.DpiScaleOf(this), …))`，并用 `IsHandleCreated` 闸门防止
   「构造期创建的卡片」被 AutoScale 二次缩放。涉及 MakeAppCard / MakeCloneCard /
   DiscoverForm.MakeCard / SnapshotsForm.MakeCard / MainForm.RebuildLayout。
3. **有运行期重建路径的窗体**（SettingsForm / Setup WizardForm）：**不走 AutoScale**，
   全量字面量经 `S()` 手动换算物理像素——构造期与重建期同一条代码路径，避免三套口径打架。

## 关键细节（踩过的坑）

- **PillChip 尺寸免疫**：徽章构造时经 `CreateGraphics + MeasureText` 实测（已是物理像素），
  重写 `ScaleControl` 为空实现，否则 AutoScale 和 card.Scale 都会把它二次放大。
- **ListView 列宽不走控件缩放**（ColumnHeader 不是 Control），CloneManager / LogViewer
  的列宽逐个 `S()` 换算。
- **Resize 处理器里不要用 S() 对「已被缩放过的控件」重算位置**——用控件自身实际宽度：
  `btnOpen.Left = row.Width - btnOpen.Width`（真实高 DPI 与探针 150% 仿真下都自洽；
  150% 仿真实测抓出此 bug：`S(64)` 恒等于 64 而按钮实际已是 96 宽，右溢 32px）。
- **卡片宽度用物理 ClientSize 计算，必须放在 Scale 之后重设**，内部定宽文本列跟随重算。
- 头像自绘用 `new Rectangle(Point.Empty, avatar.Size)` 替代写死的 40×40。
- RoundProgressBar 状态文字区高度写死 14px → 改为 `Height - textTop` 下限保护。

## 探针防线（防回归）

`test/ui-audit` 新增 **150% DPI 仿真**：`GrowFonts(×1.5)` + `Form.Scale(×1.5)` 后，
以与 96 DPI 完全相同的三类判定（越界 / 重叠 / 硬裁）复审五个窗体 × 3 尺寸。
96 DPI 进程里 S() 恒等，必须显式仿真才能覆盖高 DPI。已并入 `probe.cmd` 第 4 步。

## 验证

- 主程序编译 0 错误（仅 2 个既有警告）；核心探针 86/86；安装器 UI 探针 77/77；
- 布局审计（96 DPI + 150% 仿真）0 FAIL；
- `dist\AppClone.exe`（49,480,581 B）+ `dist\AppClone-Setup.exe`（93,568,511 B，19:33），
  载荷哈希与 dist 逐字节一致（exe / inject64.dll 均 True）。

## 版本

1.4.0 → **1.4.1**（`AppClone.csproj` / SettingsForm 关于区 / Setup `Program.Version` / README）。
