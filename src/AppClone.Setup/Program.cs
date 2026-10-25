using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Win32;

namespace AppCloneSetup;

internal static class Program
{
    public const string AppName = "应用分身";
    public const string AppExe = "AppClone.exe";
    public const string Version = "1.4.1";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{FENSHEN-APPCLONE}";
    /// <summary>安装器自己的设置（记住上次安装位置，省得每次重新找路径）。</summary>
    public const string SetupKey = @"Software\FenShen\AppCloneSetup";

    public static string DefaultDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", AppName);

    /// <summary>上次安装到的位置；不可用则回退默认位置。</summary>
    public static string InitialDir()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SetupKey);
            if (key?.GetValue("LastInstallDir") is string last && !string.IsNullOrWhiteSpace(last))
            {
                last = last.Trim();
                string root = Path.GetPathRoot(last) ?? "";
                if (Path.IsPathRooted(last) && root.Length > 0 && Directory.Exists(root))
                    return last;
            }
        }
        catch { }
        return DefaultDir;
    }

    public static void SaveLastDir(string dir)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SetupKey);
            key?.SetValue("LastInstallDir", dir);
        }
        catch { }
    }

    public static string SelfExe => Application.ExecutablePath;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "/uninstall", StringComparison.OrdinalIgnoreCase)))
            return RunUninstall();

        if (args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
            return RunSelfTest();

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new WizardForm());
        return 0;
    }

    // ------------------------------------------------------------------ 无头自检

    /// <summary>
    /// --selftest：切到真实 DPI 上下文后构造全部窗体，不显示窗口、立即退出。
    ///
    /// <para>为什么需要它：布局探针进程没有 DPI 感知，<c>DeviceDpi</c> 恒 96，
    /// 「96 DPI 看不见、真机 125%/150% 启动即崩」这类陷阱（如 v1.4.1 的 PathSelector
    /// 构造期 NRE）在探针里是隐身的。本入口以 PerMonitorV2 进程身份走一遍构造期，
    /// 让高缩放机器（含 CI 之外的真机排查）能无 GUI 复现。</para>
    /// </summary>
    private static int RunSelfTest()
    {
        TrySetProcessDpiAwareness();   // 必须在 WinForms 类型初始化之前生效
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using (var sel = new PathSelector())
            {
                if (sel.BoundsSnapshot().Box.Width <= 0) return 3;
            }
            using (var f = new WizardForm())
            {
                if (f.ClientSize.Width <= 0) return 4;
            }
            return 0;
        }
        catch (Exception ex)
        {
            // WinExe 看不见控制台，落一个临时日志供排查
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "appclone-setup-selftest.log"),
                    DateTime.Now + " SELFTEST_FAIL " + ex + Environment.NewLine);
            }
            catch { }
            return 2;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    private static void TrySetProcessDpiAwareness()
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }   // -4 = PerMonitorV2
    }

    // ------------------------------------------------------------------ 卸载

    private static int RunUninstall()
    {
        var r = MessageBox.Show($"确定卸载「{AppName}」吗？\n\n所有分身的数据与登录状态将被永久删除。",
            $"卸载 {AppName}", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) return 0;

        // 结束正在运行的管理器（不碰用户自己的浏览器）
        try
        {
            foreach (var p in Process.GetProcessesByName("AppClone"))
            {
                try { p.Kill(true); } catch { }
                p.Dispose();
            }
        }
        catch { }
        Thread.Sleep(600);

        string dir = DefaultDir;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            if (key?.GetValue("InstallLocation") is string loc && Directory.Exists(loc))
                dir = loc;

            string menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs", $"{AppName}.lnk");
            if (File.Exists(menu)) File.Delete(menu);

            // 安装时按用户勾选创建了桌面快捷方式，卸载时必须一并清掉，否则会留下指向已删目录的坏图标
            if (File.Exists(DesktopLnk)) File.Delete(DesktopLnk);

            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        }
        catch { }

        // 自删除：延时由外部 cmd 完成目录清理
        // 先 cd 到 %TEMP%：cmd 删不掉自己的当前目录，否则会留下空文件夹；
        // rd 偶发被 Defender 扫描占用导致失败，重试一次。
        try
        {
            var psi = new ProcessStartInfo("cmd.exe",
                $"/c cd /d \"%TEMP%\" & ping -n 4 127.0.0.1 > nul & rd /s /q \"{dir}\"" +
                $" & if exist \"{dir}\" (ping -n 3 127.0.0.1 > nul & rd /s /q \"{dir}\")")
            { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() };
            Process.Start(psi);
        }
        catch { }

        MessageBox.Show($"{AppName} 已卸载。", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    // ------------------------------------------------------------------ 安装动作

    private static Stream OpenPayload()
    {
        return Assembly.GetExecutingAssembly().GetManifestResourceStream("payload")
            ?? throw new InvalidOperationException("内置安装包缺失");
    }

    public static void Install(string dir, Action<string, int, int> onEntry, Action<string> onStep)
    {
        Directory.CreateDirectory(dir);

        onStep("正在释放程序文件…");
        using (var s = OpenPayload())
        using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
        {
            var entries = zip.Entries.Where(e => e.FullName.Length > 0 && !e.FullName.EndsWith("/")).ToList();
            int i = 0;
            foreach (var entry in entries)
            {
                string dest = Path.Combine(dir, entry.FullName.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                try { entry.ExtractToFile(dest, true); }
                catch (IOException)
                {
                    throw new IOException($"文件被占用：{Path.GetFileName(dest)}\n请先关闭正在运行的 {AppName} 再重试。");
                }
                i++;
                onEntry(entry.FullName, i, entries.Count);
            }
        }

        onStep("正在登记卸载信息…");
        string uninstallExe = Path.Combine(dir, $"Uninstall {AppName}.exe");
        try { File.Copy(SelfExe, uninstallExe, true); } catch { }

        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", Version);
            key.SetValue("Publisher", "FenShen");
            key.SetValue("DisplayIcon", Path.Combine(dir, AppExe));
            key.SetValue("InstallLocation", dir);
            key.SetValue("UninstallString", $"\"{uninstallExe}\" /uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            long kb = 0;
            try { kb = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024; } catch { }
            key.SetValue("EstimatedSize", kb, RegistryValueKind.DWord);
        }
    }

    public static string StartMenuLnk =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", $"{AppName}.lnk");

    public static string DesktopLnk =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{AppName}.lnk");

    public static void LaunchApp(string dir)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(dir, AppExe))
            { WorkingDirectory = dir, UseShellExecute = true });
        }
        catch { }
    }
}

/// <summary>安装向导主窗体：欢迎 → 安装 → 完成。</summary>
public class WizardForm : Form
{
    private string _dir = Program.InitialDir();
    private bool _desktopShortcut = true;
    private bool _started;

    private Panel _pages = null!;
    private Panel _pageWelcome = null!;
    private Panel _pageInstalling = null!;
    private Panel _pageDone = null!;
    private RoundedProgressBar _progress = null!;
    private Label _progressText = null!;
    private Label _stepText = null!;
    private Label _note = null!;
    private Label _doneDir = null!;
    private CheckBox _chkDesktop = null!;
    private CheckBox _chkLaunch = null!;
    private PathSelector _pathSelector = null!;
    private AppleButton _btnInstall = null!;

    public WizardForm()
    {
        Text = $"{Program.AppName} 安装向导";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        // 本窗体不走 AutoScale：页面布局在 Resize 里反复重算，逻辑值/物理值混用会打架；
        // 统一为「所有字面量经 S() 换算物理像素」，构造期与运行期同一条代码路径。
        Size = new Size(S(640), S(512));
        BackColor = Ui.Bg;
        Font = Ui.Font(10F);
        Icon = TryOwnIcon();

        Ui.RoundWindow(this);
        BuildPages();
    }

    /// <summary>96 DPI 逻辑像素 → 物理像素（安装向导全窗体统一走它；96 DPI 下恒等，探针断言不受影响）。</summary>
    private int S(int v) => Ui.S(this, v);

    private static Icon? TryOwnIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { return null; }
    }

    private void BuildPages()
    {
        _pages = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg };

        _pageWelcome = BuildWelcomePage();
        _pageInstalling = BuildInstallingPage();
        _pageDone = BuildDonePage();

        _pages.Controls.Add(_pageWelcome);
        _pages.Controls.Add(_pageInstalling);
        _pages.Controls.Add(_pageDone);
        _pageInstalling.Visible = false;
        _pageDone.Visible = false;

        Controls.Add(_pages);
    }

    private Panel BuildWelcomePage()
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg };

        var iconBox = new PictureBox
        {
            Size = new Size(S(88), S(88)),
            SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point(S(60), S(42)),
            Image = TryOwnIcon()?.ToBitmap(),
        };

        var title = new Label
        {
            Text = $"欢迎使用「{Program.AppName}」安装向导",
            Font = Ui.Font(15F, true),
            ForeColor = Ui.TextPrimary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(S(168), S(56)),
        };
        var sub = new Label
        {
            Text = $"版本 {Program.Version} · 数据隔离 · 沙盒多开 · 设备分身",
            Font = Ui.Font(9.5F),
            ForeColor = Ui.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(S(170), S(92)),
        };

        var body = new Label
        {
            Text = "选择安装位置：",
            Font = Ui.Font(9.5F),
            ForeColor = Ui.TextPrimary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(S(60), S(172)),
        };

        // 路径选择行：自带「浏览…」（原生资源管理器选择器）、「快速定位 ▾」、拖放与实时校验。
        _pathSelector = new PathSelector
        {
            AppName = Program.AppName,
            ExeName = Program.AppExe,
            DefaultPath = Program.DefaultDir,
            LastPath = Program.InitialDir(),
            SizeHint = "约 100 MB",
        };
        _pathSelector.SelectedPath = _dir;
        _pathSelector.PathChanged += (_, _) => _dir = _pathSelector.SelectedPath;
        _pathSelector.PathSubmitted += (_, _) => StartInstall();

        _chkDesktop = new CheckBox
        {
            Text = "创建桌面快捷方式",
            Checked = true,
            Font = Ui.Font(9.5F),
            ForeColor = Ui.TextPrimary,
            BackColor = Color.Transparent,
            AutoSize = true,
        };

        _note = new Label
        {
            Text = "安装即表示同意：本工具仅在本机隔离应用数据，请遵守目标应用的服务条款。",
            Font = Ui.Font(8.5F),
            ForeColor = Ui.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
        };

        _btnInstall = new AppleButton("开始安装") { Size = new Size(S(132), S(40)) };
        _btnInstall.Click += (_, _) => StartInstall();

        page.Controls.Add(iconBox);
        page.Controls.Add(title);
        page.Controls.Add(sub);
        page.Controls.Add(body);
        page.Controls.Add(_pathSelector);
        page.Controls.Add(_chkDesktop);
        page.Controls.Add(_note);
        page.Controls.Add(_btnInstall);

        // 同样手算：宿主在构造期只有 200×100，用 Anchor 会把列宽算飞。
        // 宽度基準 page.ClientSize 已是物理像素，偏移量与行高经 S() 换算。
        void Layout()
        {
            int w = page.ClientSize.Width;
            int h = page.ClientSize.Height;
            _pathSelector.SetBounds(S(60), S(198), Math.Max(_pathSelector.MinUsableWidthScaled, w - S(120)), _pathSelector.PreferredHeightScaled);
            _chkDesktop.Location = new Point(S(60), _pathSelector.Bottom + S(14));
            _note.Location = new Point(S(60), _chkDesktop.Bottom + S(20));
            _btnInstall.Location = new Point(w - _btnInstall.Width - S(40), h - _btnInstall.Height - S(26));
        }
        page.Resize += (_, _) => Layout();
        Layout();

        return page;
    }

    /// <summary>校验通过后进入安装页 —— 按钮点击与路径框回车共用，靠 _started 防重复触发。</summary>
    private void StartInstall()
    {
        if (_started) return;

        _desktopShortcut = _chkDesktop.Checked;
        if (string.IsNullOrWhiteSpace(_dir))
        {
            MessageBox.Show(this, "请先选择安装位置。", Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try { Directory.CreateDirectory(_dir); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法创建目录：{ex.Message}", "安装位置无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _started = true;
        Program.SaveLastDir(_dir);
        SlideAnimator.SlideTo(_pages, _pageInstalling);
        Task.Run(() => DoInstall(_dir));
    }

    private Panel BuildInstallingPage()
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg };

        var title = new Label
        {
            Text = "正在安装…",
            Font = Ui.Font(15F, true),
            ForeColor = Ui.TextPrimary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(S(60), S(70)),
        };
        _stepText = new Label
        {
            Text = "准备中…",
            Font = Ui.Font(10F),
            ForeColor = Ui.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(S(62), S(118)),
        };
        _progress = new RoundedProgressBar
        {
            Location = new Point(S(60), S(170)),
            Size = new Size(S(500), S(10)),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        _progressText = new Label
        {
            Text = "",
            Font = Ui.Font(8.5F),
            ForeColor = Ui.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(S(62), S(192)),
        };

        page.Controls.Add(title);
        page.Controls.Add(_stepText);
        page.Controls.Add(_progress);
        page.Controls.Add(_progressText);
        return page;
    }

    private Panel BuildDonePage()
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg };

        var check = new Label
        {
            Text = "✓",
            Font = Ui.Font(30F, true),
            ForeColor = Ui.Ok,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(S(60), S(52)),
        };
        var title = new Label
        {
            Text = $"「{Program.AppName}」已安装完成",
            Font = Ui.Font(15F, true),
            ForeColor = Ui.TextPrimary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(S(118), S(66)),
        };
        var body = new Label
        {
            Text = "1. 添加应用 → 2. 新建分身 → 3. 一键启动，每个分身都是独立的账号与设备。",
            Font = Ui.Font(9.5F),
            ForeColor = Ui.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(S(62), S(118)),
        };

        _doneDir = new Label
        {
            Text = "",
            Font = Ui.Font(8.5F),
            ForeColor = Ui.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(S(62), S(146)),
        };

        _chkLaunch = new CheckBox
        {
            Text = "立即运行应用分身",
            Checked = true,
            Font = Ui.Font(9.5F),
            ForeColor = Ui.TextPrimary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(S(60), S(186)),
        };

        var btnFinish = new AppleButton("完成") { Size = new Size(S(120), S(40)) };
        btnFinish.Click += (_, _) =>
        {
            if (_chkLaunch.Checked) Program.LaunchApp(_dir);
            Close();
        };

        page.Controls.Add(check);
        page.Controls.Add(title);
        page.Controls.Add(body);
        page.Controls.Add(_doneDir);
        page.Controls.Add(_chkLaunch);
        page.Controls.Add(btnFinish);
        page.Resize += (_, _) => btnFinish.Location =
            new Point(page.ClientSize.Width - btnFinish.Width - S(40), page.ClientSize.Height - btnFinish.Height - S(26));

        return page;
    }

    private void DoInstall(string dir)
    {
        try
        {
            void OnEntry(string name, int i, int total)
            {
                BeginInvoke(() =>
                {
                    _progress.Value = Math.Min(100, i * 100 / Math.Max(1, total));
                    _progressText.Text = $"{Path.GetFileName(name)}  ({i}/{total})";
                });
            }
            void OnStep(string s) => BeginInvoke(() => _stepText.Text = s);

            Program.Install(dir, OnEntry, OnStep);

            // 快捷方式（COM IShellLink 需在已初始化 COM 的 STA 线程创建 → 派发回 UI 线程）
            BeginInvoke(() =>
            {
                try
                {
                    _stepText.Text = "正在创建快捷方式…";
                    string appExe = Path.Combine(dir, Program.AppExe);
                    new AppClone.Shared.NativeLink
                    {
                        Target = appExe,
                        WorkingDirectory = dir,
                        IconPath = appExe,
                        Description = Program.AppName,
                    }.Save(Program.StartMenuLnk);

                    if (_desktopShortcut)
                    {
                        new AppClone.Shared.NativeLink
                        {
                            Target = appExe,
                            WorkingDirectory = dir,
                            IconPath = appExe,
                            Description = Program.AppName,
                        }.Save(Program.DesktopLnk);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"快捷方式创建失败：{ex.Message}", Program.AppName,
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                _doneDir.Text = $"安装位置：{dir}";
                SlideAnimator.SlideTo(_pages, _pageDone);
            });
        }
        catch (Exception ex)
        {
            BeginInvoke(() =>
            {
                MessageBox.Show(this, ex.Message, "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            });
        }
    }
}
