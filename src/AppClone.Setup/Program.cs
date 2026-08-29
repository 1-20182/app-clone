using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Win32;

namespace AppCloneSetup;

internal static class Program
{
    public const string AppName = "应用分身";
    public const string AppExe = "AppClone.exe";
    public const string Version = "1.0.0";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{FENSHEN-APPCLONE}";

    public static string DefaultDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", AppName);

    public static string SelfExe => Application.ExecutablePath;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "/uninstall", StringComparison.OrdinalIgnoreCase)))
            return RunUninstall();

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new WizardForm());
        return 0;
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

            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        }
        catch { }

        // 自删除：延时由外部 cmd 完成目录清理
        try
        {
            var psi = new ProcessStartInfo("cmd.exe",
                $"/c ping -n 3 127.0.0.1 > nul & rd /s /q \"{dir}\"")
            { CreateNoWindow = true, UseShellExecute = false };
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
    private string _dir = Program.DefaultDir;
    private bool _desktopShortcut = true;

    private Panel _pages = null!;
    private Panel _pageWelcome = null!;
    private Panel _pageInstalling = null!;
    private Panel _pageDone = null!;
    private RoundedProgressBar _progress = null!;
    private Label _progressText = null!;
    private Label _stepText = null!;
    private CheckBox _chkDesktop = null!;
    private CheckBox _chkLaunch = null!;

    public WizardForm()
    {
        Text = $"{Program.AppName} 安装向导";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(640, 460);
        BackColor = Ui.Bg;
        Font = Ui.Font(10F);
        Icon = TryOwnIcon();

        Ui.RoundWindow(this);
        BuildPages();
    }

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
            Size = new Size(88, 88),
            SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point(60, 42),
            Image = TryOwnIcon()?.ToBitmap(),
        };

        var title = new Label
        {
            Text = $"欢迎使用「{Program.AppName}」安装向导",
            Font = Ui.Font(15F, true),
            ForeColor = Ui.TextPrimary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(168, 56),
        };
        var sub = new Label
        {
            Text = $"版本 {Program.Version} · 数据隔离 · 沙盒多开 · 设备分身",
            Font = Ui.Font(9.5F),
            ForeColor = Ui.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(170, 92),
        };

        var body = new Label
        {
            Text = "即将安装到以下位置（约 100 MB 空间）：",
            Font = Ui.Font(9.5F),
            ForeColor = Ui.TextPrimary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(60, 180),
        };

        var txtDir = new TextBox
        {
            Text = _dir,
            Font = Ui.Font(9.5F),
            Location = new Point(60, 210),
            Size = new Size(420, 28),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        txtDir.TextChanged += (_, _) => _dir = txtDir.Text;

        var btnBrowse = new AppleButton("浏览…", filled: false, 9.5F)
        { Size = new Size(84, 30), Location = new Point(496, 208) };
        btnBrowse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = _dir, Description = "选择安装位置" };
            if (dlg.ShowDialog(this) == DialogResult.OK) txtDir.Text = dlg.SelectedPath;
        };

        _chkDesktop = new CheckBox
        {
            Text = "创建桌面快捷方式",
            Checked = true,
            Font = Ui.Font(9.5F),
            ForeColor = Ui.TextPrimary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(60, 262),
        };

        var note = new Label
        {
            Text = "安装即表示同意：本工具仅在本机隔离应用数据，请遵守目标应用的服务条款。",
            Font = Ui.Font(8.5F),
            ForeColor = Ui.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(60, 310),
        };

        var btnInstall = new AppleButton("开始安装") { Size = new Size(132, 40), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        btnInstall.Click += (_, _) =>
        {
            _desktopShortcut = _chkDesktop.Checked;
            if (string.IsNullOrWhiteSpace(_dir)) { MessageBox.Show(this, "请选择安装位置。"); return; }
            try { Directory.CreateDirectory(_dir); }
            catch (Exception ex) { MessageBox.Show(this, $"无法创建目录：{ex.Message}", "安装位置无效"); return; }
            SlideAnimator.SlideTo(_pages, _pageInstalling);
            Task.Run(() => DoInstall(_dir));
        };

        page.Controls.Add(iconBox);
        page.Controls.Add(title);
        page.Controls.Add(sub);
        page.Controls.Add(body);
        page.Controls.Add(txtDir);
        page.Controls.Add(btnBrowse);
        page.Controls.Add(_chkDesktop);
        page.Controls.Add(note);
        page.Controls.Add(btnInstall);
        page.Resize += (_, _) => btnInstall.Location = new Point(page.Width - 132 - 40, page.Height - 40 - 28);

        return page;
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
            Location = new Point(60, 70),
        };
        _stepText = new Label
        {
            Text = "准备中…",
            Font = Ui.Font(10F),
            ForeColor = Ui.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(62, 118),
        };
        _progress = new RoundedProgressBar
        {
            Location = new Point(60, 170),
            Size = new Size(500, 10),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        _progressText = new Label
        {
            Text = "",
            Font = Ui.Font(8.5F),
            ForeColor = Ui.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(62, 192),
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
            Location = new Point(60, 52),
        };
        var title = new Label
        {
            Text = $"「{Program.AppName}」已安装完成",
            Font = Ui.Font(15F, true),
            ForeColor = Ui.TextPrimary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(118, 66),
        };
        var body = new Label
        {
            Text = "1. 添加应用 → 2. 新建分身 → 3. 一键启动，每个分身都是独立的账号与设备。",
            Font = Ui.Font(9.5F),
            ForeColor = Ui.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(62, 118),
        };

        _chkLaunch = new CheckBox
        {
            Text = "立即运行应用分身",
            Checked = true,
            Font = Ui.Font(9.5F),
            ForeColor = Ui.TextPrimary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(60, 170),
        };

        var btnFinish = new AppleButton("完成") { Size = new Size(120, 40), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        btnFinish.Click += (_, _) =>
        {
            if (_chkLaunch.Checked) Program.LaunchApp(_dir);
            Close();
        };

        page.Controls.Add(check);
        page.Controls.Add(title);
        page.Controls.Add(body);
        page.Controls.Add(_chkLaunch);
        page.Controls.Add(btnFinish);
        page.Resize += (_, _) => btnFinish.Location = new Point(page.Width - 120 - 40, page.Height - 40 - 28);

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
