using System.Reflection;
using System.Text;

namespace MainGeom;

/// <summary>
/// 无 GUI 几何复现台：与真实 Show 生命周期同参走一遍 MainForm，倾倒控件树几何。
/// 生命周期：ctor（无句柄）→ CreateControl（建句柄 + 框架 AutoScale）→ OnLoad（DPI 守卫）。
/// 用法：MainGeom.exe [输出文件路径]；不传则写到 exe 同目录 main-geom-report.txt。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "main-geom-report.txt");

        var sb = new StringBuilder();
        try
        {
            // 与 AppClone.Program.Main 同参：PMv2 必须最先设置
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            MiniProbe(sb);
            FlowProbe(sb);

            // 数据目录重定向，避免碰真实数据
            string dataDir = Path.Combine(Path.GetTempPath(), "main-geom-data");
            Directory.CreateDirectory(dataDir);
            Environment.SetEnvironmentVariable("FENSHEN_DATA_DIR", dataDir);

            using var f = new AppClone.MainForm();
            sb.AppendLine($"DeviceDpi={f.DeviceDpi}");
            sb.AppendLine($"AutoScaleDims={f.CurrentAutoScaleDimensions}");
            sb.AppendLine($"AutoScaleMode={f.AutoScaleMode}");
            sb.AppendLine($"FormSize(pre)={f.Size}  HandleCreated={f.IsHandleCreated}");

            // —— 模拟真实 Show：CreateControl(true) 建句柄并触发框架 AutoScale ——
            var cc = typeof(Control).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "CreateControl" && m.GetParameters().Length == 1);
            bool ccInvoked = false;
            if (cc != null)
            {
                try { cc.Invoke(f, new object[] { true }); ccInvoked = true; }
                catch (Exception ex) { sb.AppendLine("CreateControl_FAIL: " + ex.InnerException?.Message); }
            }
            if (!ccInvoked)
            {
                _ = f.Handle;                    // 兜底：旧口径
                f.PerformAutoScale();
            }
            f.PerformLayout();

            sb.AppendLine($"==== POST-SHOW (CreateControl invoked={ccInvoked}) ====");
            sb.AppendLine($"FormSize={f.Size} HandleCreated={f.IsHandleCreated} DeviceDpi={f.DeviceDpi}");
            Dump(f, 0, sb);

            // —— OnLoad（DPI 守卫；框架缩放正确时应为幂等跳过）——
            var ol = f.GetType().GetMethod("OnLoad", BindingFlags.Instance | BindingFlags.NonPublic);
            ol?.Invoke(f, new object[] { EventArgs.Empty });
            f.PerformLayout();
            sb.AppendLine("==== POST-ONLOAD ====");
            sb.AppendLine($"FormSize={f.Size}");
            Dump(f, 0, sb);
        }
        catch (Exception ex)
        {
            sb.AppendLine("HARNESS_FAIL: " + ex);
        }
        File.WriteAllText(outPath, sb.ToString());
        Console.Out.Write("OK " + outPath);
        return 0;
    }

    /// <summary>逐语句复刻 MainForm ctor 的关键语句序列，定位「谁把窗体在构造期缩放了」。</summary>
    private static void MiniProbe(StringBuilder sb)
    {
        var f = new Form { Text = "mini" };
        sb.AppendLine($"[0] new Form                     Size={f.Size} Dpi={f.DeviceDpi}");
        f.Size = new Size(1120, 740);
        sb.AppendLine($"[1] Size=(1120,740)              Size={f.Size}");
        f.MinimumSize = new Size(960, 640);
        sb.AppendLine($"[2] MinimumSize=(960,640)        Size={f.Size}");
        f.AutoScaleMode = AutoScaleMode.Dpi;
        sb.AppendLine($"[3] AutoScaleMode=Dpi            Size={f.Size} CurDims={f.CurrentAutoScaleDimensions}");
        f.AutoScaleDimensions = new SizeF(96F, 96F);
        sb.AppendLine($"[4] AutoScaleDims=(96,96)        Size={f.Size} CurDims={f.CurrentAutoScaleDimensions}");
        f.Font = new Font("Microsoft YaHei UI", 9F);
        sb.AppendLine($"[5] Font=9pt                     Size={f.Size}");
        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 64 };
        f.Controls.Add(top);
        sb.AppendLine($"[6] Controls.Add(top 64)         Size={f.Size} top={top.Bounds}");
        top.BringToFront();
        sb.AppendLine($"[7] top.BringToFront()           Size={f.Size} top={top.Bounds}");
        var side = new Panel { Dock = DockStyle.Left, Width = 280 };
        f.Controls.Add(side);
        sb.AppendLine($"[8] Controls.Add(side 280)       Size={f.Size} top={top.Bounds} side={side.Bounds}");
        f.Dispose();
    }

    /// <summary>复刻 BuildRightPane 头部：TLP(AutoSize 列) + FlowLayoutPanel(None,AutoSize,GrowAndShrink)，
    /// 验证整树 Scale 后 actions 是否 0×0，并对比三种配方的存活情况。</summary>
    private static void FlowProbe(StringBuilder sb)
    {
        var f = new Form { Size = new Size(1000, 600) };
        f.AutoScaleMode = AutoScaleMode.Dpi;
        f.AutoScaleDimensions = new SizeF(96F, 96F);

        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 20, 8) };
        f.Controls.Add(right);

        TableLayoutPanel MakeHeader(FlowLayoutPanel actions)
        {
            var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = 68, ColumnCount = 3, Padding = new Padding(16, 0, 20, 0) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            header.Controls.Add(new PictureBox { Size = new Size(48, 48) }, 0, 0);
            header.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "info" }, 1, 0);
            header.Controls.Add(actions, 2, 0);
            return header;
        }

        FlowLayoutPanel MakeActions(bool autoSize)
        {
            var a = new FlowLayoutPanel
            {
                WrapContents = false, Anchor = AnchorStyles.None, Margin = new Padding(0),
                FlowDirection = FlowDirection.RightToLeft,
            };
            if (autoSize) { a.AutoSize = true; a.AutoSizeMode = AutoSizeMode.GrowAndShrink; }
            else a.Size = new Size(220, 42);
            a.Controls.Add(new Button { Size = new Size(112, 36), Text = "B1" });
            a.Controls.Add(new Button { Size = new Size(96, 36), Text = "B2" });
            return a;
        }

        var h1 = MakeHeader(MakeActions(true));   right.Controls.Add(h1);
        var h2 = MakeHeader(MakeActions(false));  right.Controls.Add(h2);
        var cloneFlow = new FlowLayoutPanel { Dock = DockStyle.Fill };
        right.Controls.Add(cloneFlow);

        sb.AppendLine($"[F0] h1.actions pre-layout={h1.Controls[2].Bounds}");
        f.PerformLayout();
        sb.AppendLine($"[F0.5] after layout: actions1={h1.Controls[2].Bounds} actions2={h2.Controls[2].Bounds}");

        foreach (Control c in f.Controls) c.Scale(new SizeF(1.5f, 1.5f));
        f.PerformLayout();
        sb.AppendLine($"[F1] after Scale+layout: h1={h1.Bounds} actions1={h1.Controls[2].Bounds} h2={h2.Bounds} actions2={h2.Controls[2].Bounds}");
        f.Dispose();
    }

    private static void Dump(Control c, int depth, StringBuilder sb)
    {
        string ind = new(' ', depth * 2);
        sb.AppendLine($"{ind}{c.GetType().Name} bounds={c.Bounds} dock={c.Dock} text='{Trim(c.Text)}'");
        if (depth >= 4) return;
        foreach (Control child in c.Controls) Dump(child, depth + 1, sb);
    }

    private static string Trim(string s) => s.Length <= 24 ? s : s[..24] + "…";
}
