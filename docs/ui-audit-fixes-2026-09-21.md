# 增量交付：全 UI 面遮挡/裁切排查与修复（2026-09-21）

> 范围：安装向导之外的全部 UI（主窗体 + 5 个子窗体）。主程序版本仍为 1.4.0，
> 隔离核心（Injector / RegClone / fenshen.c）零改动。

## 0. 方法：不再肉眼猜布局，造了一台"布局 CT 机"

上一轮安装器修复证明"读代码脑内模拟 WinForms 布局"会猜错（那个浏览按钮被输入框
糊住就没人看出来）。本轮新建 **`test/ui-audit`**——无 GUI 布局审计探针：

- 复用 AppClone UI 源码编译（不引用 44MB 产物），数据目录重定向到临时目录；
- 对可无副作用构造的窗体（Settings / CloneManager / LogViewer / DryRun / Snapshots）
  在 320→1400 五种宽度下递归 `PerformLayout()`，遍历控件树检测：
  **子控件越界父容器、兄弟控件重叠、AutoEllipsis 缺失导致的文本溢出**；
- 关键陷阱：不显示窗口时 `Control.Visible` 沿父链恒为 false，
  必须用反射读内部状态位 `GetState(States.Visible)` 判定，否则审计的是空集（首轮就踩了）。

## 1. 探针首战即抓到一个崩溃级 bug

**CloneManagerForm 打开即闪退（栈溢出）**：`_appFilter.SelectedIndexChanged`
订阅的是匿名 lambda，而 `RefreshList()` 里却 `-= OnAppFilterChanged` 退订一个
从未订阅过的方法——退订无效 → 下拉重建时事件再入 `RefreshList` → 无限递归。
修复：改用具名方法订阅；`RefreshList` 加 `_refreshing` 重入闸门兜底。

## 2. 修复清单（探针 43 项 FAIL → 0）

| 窗体 | 问题 | 修法 |
|---|---|---|
| Snapshots | 「立即创建快照」用窗体总高（含标题栏）当锚定基准，底部越界 21px；Dock=Fill 列表盖住一切；空状态文本溢出 | 整段重排为标准 Dock 结构：Top 头 / Bottom 按钮面板 / Fill 列表 + Fill 居中空态，二者互斥显示 |
| DryRun | 标题（AutoSize 高度滞后量出）压住副标题 5px；「确认启动」被默认 3px 外边距挤出停靠高度 | 标题/副标题定高定宽 + AutoEllipsis + head.Resize 跟随；按钮 Margin=Empty |
| LogViewer | 提示行（含完整日志路径）右溢 63px | 定宽 + AutoEllipsis + head.Resize 跟随宽度 |
| Settings | 卡片宽用 `ClientSize-4` 没扣 40px Padding，默认尺寸即右溢 36px 且不随窗口伸缩；`RowWithLabel` 读 AutoSize 标签的未量完宽度（拿到默认值 100）导致控件压住真实文本 16px；行宽缩放时右锚定差值按旧宽捕获、把控件拖进标签（最多 72px）；「版本 1.4.0」被 FSub 字号的「应用分身」压住 4px；超长勾选框在最小宽度溢出 97px | 卡片宽度统一 `InnerCardWidth`（扣 Padding）+ `_content.Resize` 联动；标签宽度用 `TextRenderer.MeasureText` 实测；行内控件一律左锚定、行宽构建期给对（`ClientSize-76`）实现"构建期零缩放"；数据目录行用显式 Resize 重算；关于区用 `name.Bottom+4` 推导；缩短 AppUserModelID 文案 |
| MainForm | ① `_noMatch`（没有匹配的应用）从不 BringToFront，被永不隐藏的 `_appFlow`（Dock=Fill）盖住，**该提示从未显示过**；② `_cloneEmpty`（该应用还没有分身）压在不透明的 `_cloneFlow` 底下，**同样从未显示过**；③ 右栏两张浮动卡片从未居中，一直缩在左上角；④ `PromptText` 对话框取消按钮右缘超客户区 8px 被裁；⑤ 空态卡引导文本设了 Size 但 AutoSize 不换行，横向撑出卡片 | ① 可见时 BringToFront；② 空态时 `_cloneFlow.Visible=false`；③ `CenterRightOverlays()` 统一居中（构造/Resize/切换时调用）；④ `ClientSize` 定尺寸 + 按客户区推算按钮坐标 + FixedDialog；⑤ `AutoSize=false` 固定多行区域 |

## 3. 验证

- UI 布局审计：43 FAIL → **0 FAIL**（320/480/560/720/900/1100 多宽度全覆盖）
- 核心逻辑探针 86/86 + 安装器 UI 探针（见 qa.cmd 输出）
- 隔离能力 test.sh 15/15（UI 改动不触及隔离核心，回归保险）
- `scripts/qa.cmd` 全量 QA 退出码 0；dist 两份成品重建，载荷哈希与 dist 一致

## 4. 经验沉淀（已并入 MEMORY.md / skill）

1. **同文件多处编辑必须串行**——本轮并行编辑连续吞掉 4 处改动（usages 落盘、
   declarations 丢失，CS0103 才暴露），老铁律再次应验，修完必须 grep 复核。
2. **无窗口环境下 `Control.Visible` 不可信**（父链不可见导致恒 false）。
3. **AutoSize 标签的 Width/Height 在量完之前读不得**；锚定差值在"构建后容器再缩放"
   的序列下会按旧宽捕获——要么构建期给对尺寸（零缩放），要么显式 Resize 重算。
