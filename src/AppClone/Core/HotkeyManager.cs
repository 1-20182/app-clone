using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace AppClone.Core;

/// <summary>
/// 全局热键管理器：注册 / 转发 WM_HOTKEY。
/// 注册必须在宿主窗体句柄有效后（构造末尾或 OnHandleCreated）进行；
/// MainForm.WndProc 捕获 m.Msg == HotkeyManager.WmHotkey 后调用 HandleWmHotkey 转发。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    public const int WmHotkey = 0x0312;
    public event Action<string, string>? HotkeyFired;   // (appId, cloneId)

    private readonly Form _owner;   // 必须在构造赋值（避免 CS0191）
    private int _nextId = 1;
    private readonly Dictionary<int, (string AppId, string CloneId)> _regs = new();
    private readonly object _lock = new();

    public HotkeyManager(Form owner)
    {
        _owner = owner ?? throw new System.ArgumentNullException(nameof(owner));
    }

    /// <summary>注册组合键（如 "Ctrl+Alt+1"）。成功返回 true；冲突失败返回 false（静默记录）。</summary>
    public bool Register(string combo, string appId, string cloneId)
    {
        if (_owner.IsDisposed || _owner.Handle == IntPtr.Zero) return false;
        if (string.IsNullOrWhiteSpace(combo)) return false;
        var (mod, vk) = Parse(combo);
        if (vk == 0) return false;

        int id;
        lock (_lock) { id = _nextId++; _regs[id] = (appId, cloneId); }
        bool ok = RegisterHotKey(_owner.Handle, id, mod, vk);
        if (!ok)
        {
            lock (_lock) { _regs.Remove(id); _nextId--; }
            return false;   // 冲突：静默失败（由 MainForm 标红处理）
        }
        return true;
    }

    public void UnregisterAll()
    {
        lock (_lock)
        {
            foreach (var id in _regs.Keys.ToArray())
            {
                try { UnregisterHotKey(_owner.Handle, id); } catch { }
            }
            _regs.Clear();
        }
    }

    /// <summary>由 MainForm.WndProc 转发：msg 应为 WmHotkey，wParam 为热键 id。</summary>
    public void HandleWmHotkey(int msg, IntPtr wParam)
    {
        if (msg != WmHotkey) return;
        int id = (int)wParam;
        (string AppId, string CloneId) entry;
        lock (_lock)
        {
            if (!_regs.TryGetValue(id, out entry)) return;
        }
        HotkeyFired?.Invoke(entry.AppId, entry.CloneId);
    }

    private static (uint mod, uint vk) Parse(string combo)
    {
        uint mod = 0;
        Keys vk = Keys.None;
        foreach (var part in combo.Split('+'))
        {
            var s = part.Trim().ToLowerInvariant();
            switch (s)
            {
                case "ctrl": case "control": mod |= 0x2; break;   // MOD_CONTROL
                case "alt": mod |= 0x1; break;                    // MOD_ALT
                case "shift": mod |= 0x4; break;                  // MOD_SHIFT
                case "win": case "windows": mod |= 0x8; break;    // MOD_WIN
                default: vk = ParseKey(s); break;
            }
        }
        return (mod, (uint)vk);
    }

    private static Keys ParseKey(string s)
    {
        if (Enum.TryParse<Keys>(s, true, out var k) && k != Keys.None) return k;
        if (s.Length == 1 && char.IsDigit(s[0])) return Keys.D0 + (s[0] - '0');
        return Keys.None;
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public void Dispose()
    {
        try { UnregisterAll(); } catch { }
    }
}
