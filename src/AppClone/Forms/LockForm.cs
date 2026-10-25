using AppClone.Core;

namespace AppClone.Forms;

/// <summary>
/// 启动解锁窗口：密码不通过时由 Program 直接退出，避免锁只锁窗口、进程仍在后台常驻。
/// </summary>
public sealed class LockForm : Form
{
    private readonly TextBox _pwd;
    private readonly Label _err;

    public LockForm()
    {
        Ui.Theme = Settings.Current.Theme;

        Text = "应用分身 · 已锁定";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(400, 232);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        BackColor = Ui.BgOf();
        Font = Ui.Font(Ui.FBody);
        Icon = MainForm.TryAppIcon();

        var title = Ui.Label("应用分身已锁定", Ui.FTitle, true, Ui.TextPrimaryOf());
        title.Location = new Point(28, 24);
        var sub = Ui.Label("请输入启动密码解锁", Ui.FBody, false, Ui.TextSecondaryOf());
        sub.Location = new Point(28, 62);

        _pwd = new TextBox
        {
            Location = new Point(28, 96), Size = new Size(344, 30),
            UseSystemPasswordChar = true, Font = Ui.Font(12F),
            BackColor = Ui.CardOf(), ForeColor = Ui.TextPrimaryOf(),
        };

        _err = Ui.Label("密码错误，请重试", Ui.FBody, false, Ui.DangerOf());
        _err.Location = new Point(28, 134);
        _err.Visible = false;

        var btnOk = new AppleButton("解锁", AppleButton.Style.Filled) { Size = new Size(96, 34) };
        btnOk.Click += (_, _) => TryUnlock();
        var btnExit = new AppleButton("退出", AppleButton.Style.Secondary) { Size = new Size(84, 34) };
        btnExit.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        var actions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent, Location = new Point(200, 176),
        };
        btnExit.Margin = new Padding(8, 0, 0, 0);
        actions.Controls.Add(btnOk);
        actions.Controls.Add(btnExit);

        Controls.Add(title);
        Controls.Add(sub);
        Controls.Add(_pwd);
        Controls.Add(_err);
        Controls.Add(actions);

        AcceptButton = btnOk;
        CancelButton = btnExit;
        Shown += (_, _) => { Ui.RoundWindow(this); FadeAnimator.FadeIn(this); _pwd.Focus(); };
    }

    private void TryUnlock()
    {
        if (_pwd.Text.Length > 0 && AppLock.Verify(_pwd.Text))
        {
            DialogResult = DialogResult.OK;
            Close();
            return;
        }
        _err.Visible = true;
        _pwd.SelectAll();
        _pwd.Focus();
    }
}

/// <summary>
/// 设置 / 修改 / 关闭启动密码。留空新密码并保存 = 关闭启动锁。
/// 只有保存成功（AppLock.Set 已落盘）才返回 OK。
/// </summary>
public sealed class PasswordSetupForm : Form
{
    private readonly bool _hadPassword;
    private readonly TextBox? _old;
    private readonly TextBox _new;
    private readonly TextBox _confirm;
    private readonly Label _err;

    public PasswordSetupForm()
    {
        Ui.Theme = Settings.Current.Theme;
        _hadPassword = AppLock.Enabled;

        Text = _hadPassword ? "修改启动密码" : "设置启动密码";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(420, _hadPassword ? 292 : 240);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        BackColor = Ui.BgOf();
        Font = Ui.Font(Ui.FBody);

        int y = 22;
        if (_hadPassword)
        {
            Controls.Add(LabelAt("原密码", 28, y, Ui.FBody, Ui.TextPrimaryOf()));
            _old = Field(y);
            Controls.Add(_old);
            y += 50;
        }
        Controls.Add(LabelAt("新密码", 28, y, Ui.FBody, Ui.TextPrimaryOf()));
        _new = Field(y);
        Controls.Add(_new);
        y += 50;
        Controls.Add(LabelAt("确认新密码", 28, y, Ui.FBody, Ui.TextPrimaryOf()));
        _confirm = Field(y);
        Controls.Add(_confirm);
        y += 52;

        Controls.Add(LabelAt("留空新密码并保存 = 关闭启动锁", 140, y, Ui.FCaption, Ui.TextSecondaryOf()));

        _err = Ui.Label("", Ui.FBody, false, Ui.DangerOf());
        _err.Location = new Point(28, y + 24);
        _err.Visible = false;

        var btnOk = new AppleButton("保存", AppleButton.Style.Filled) { Size = new Size(96, 34) };
        btnOk.Click += (_, _) => TrySave();
        var btnCancel = new AppleButton("取消", AppleButton.Style.Secondary) { Size = new Size(84, 34) };
        btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        var actions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent, Location = new Point(200, y + 52),
        };
        btnCancel.Margin = new Padding(8, 0, 0, 0);
        actions.Controls.Add(btnOk);
        actions.Controls.Add(btnCancel);
        Controls.Add(actions);

        AcceptButton = btnOk;
        CancelButton = btnCancel;
        Shown += (_, _) => { Ui.RoundWindow(this); FadeAnimator.FadeIn(this); (_old ?? _new).Focus(); };
    }

    private TextBox Field(int y) => new()
    {
        Location = new Point(140, y), Size = new Size(232, 30),
        UseSystemPasswordChar = true, Font = Ui.Font(11F),
        BackColor = Ui.CardOf(), ForeColor = Ui.TextPrimaryOf(),
    };

    private static Label LabelAt(string text, int x, int y, float size, Color color)
    {
        var l = Ui.Label(text, size, false, color);
        l.Location = new Point(x, y);
        return l;
    }

    private void TrySave()
    {
        if (_hadPassword && !AppLock.Verify(_old!.Text))
        {
            Fail("原密码不正确");
            _old!.SelectAll(); _old.Focus();
            return;
        }
        if (_new.Text != _confirm.Text)
        {
            Fail("两次输入的新密码不一致");
            _confirm.SelectAll(); _confirm.Focus();
            return;
        }
        if (_new.Text.Length is > 0 and < 4)
        {
            Fail("新密码至少 4 位");
            _new.Focus();
            return;
        }
        if (!_hadPassword && _new.Text.Length == 0)
        {
            Fail("请输入新密码；如需取消，直接关闭本窗口");
            _new.Focus();
            return;
        }
        AppLock.Set(_new.Text);
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Fail(string msg) { _err.Text = msg; _err.Visible = true; }
}
