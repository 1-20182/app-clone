using System.Runtime.InteropServices;
using System.Text;

namespace AppClone.Shared;

/// <summary>
/// 原生 .lnk 快捷方式（IShellLinkW + IPersistFile）。
/// 不依赖 PowerShell/WScript，需在 COM 已初始化的线程调用（WinForms UI 线程即 STA）。
/// </summary>
public sealed class NativeLink
{
    public string Target { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string? WorkingDirectory { get; set; }
    public string? IconPath { get; set; }
    public int IconIndex { get; set; }
    public string Description { get; set; } = "";

    /// <summary>
    /// 实验性（F6）：把 AppUserModelID 写进 .lnk 的属性存储，便于任务栏把多个分身分组区分。
    /// 各 Windows 版本对「快捷方式 AUMID」与「进程自身 AUMID」的匹配行为并不一致，
    /// 因此默认关闭，且**任何失败都静默忽略** —— 分组是锦上添花，不能拖垮创建快捷方式本身。
    /// </summary>
    public string? AppUserModelId { get; set; }

    public void Save(string lnkPath)
    {
        IShellLinkW link = (IShellLinkW)new ShellLinkClass();
        try
        {
            link.SetPath(Target);
            if (Arguments.Length > 0) link.SetArguments(Arguments);
            var wd = WorkingDirectory ?? Path.GetDirectoryName(Target);
            if (!string.IsNullOrEmpty(wd)) link.SetWorkingDirectory(wd);
            if (!string.IsNullOrEmpty(IconPath)) link.SetIconLocation(IconPath, IconIndex);
            if (Description.Length > 0) link.SetDescription(Description);

            ((IPersistFile)link).Save(lnkPath, true);
        }
        finally
        {
            _ = Marshal.ReleaseComObject(link);
        }

        if (!string.IsNullOrWhiteSpace(AppUserModelId))
            TrySetAppUserModelId(lnkPath, AppUserModelId!);
    }

    // ------------------------------------------------------------------ 实验性：AUMID

    private const uint GPS_READWRITE = 0x2;
    private const uint PRSPEC_PROPID = 0x0;

    /// <summary>PKEY_AppUserModel_ID = {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, pid = 5。</summary>
    private static readonly PROPERTYKEY PkeyAppUserModelId = new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 5,
    };

    private static void TrySetAppUserModelId(string lnkPath, string aumid)
    {
        IPropertyStore? store = null;
        IntPtr strPtr = IntPtr.Zero;
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreFromParsingName(lnkPath, IntPtr.Zero, GPS_READWRITE, ref iid, out store) != 0)
                return;
            if (store == null) return;

            // vt = VT_LPWSTR(31)，字符串指针置于 union（偏移 8）
            strPtr = Marshal.StringToCoTaskMemUni(aumid);
            var spec = new PROPSPEC { ulKind = PRSPEC_PROPID, unionValue = (IntPtr)PkeyAppUserModelId.pid };
            var variant = new PROPVARIANT { vt = 31, pointerValue = strPtr };
            store.SetValue(ref spec, ref variant);
            store.Commit();
        }
        catch { }
        finally
        {
            if (strPtr != IntPtr.Zero) { try { Marshal.FreeCoTaskMem(strPtr); } catch { } }
            if (store != null) { try { Marshal.ReleaseComObject(store); } catch { } }
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHGetPropertyStoreFromParsingName(
        string pszPath, IntPtr pbc, uint flags, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ppv);

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue(ref PROPSPEC key, out PROPVARIANT pv);
        void SetValue(ref PROPSPEC key, ref PROPVARIANT pv);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    /// <summary>
    /// PROPSPEC 是个 union（PROPID 或 LPOLESTR），在 x64 上必须按 8 字节指针对齐后才与原生布局一致。
    /// 这里用 IntPtr 承载 union，保证 32/64 位下的尺寸都正确 —— 布局写错会直接踩内存。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PROPSPEC
    {
        public uint ulKind;
        public IntPtr unionValue;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointerValue;
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkClass
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pk);
        void SetHotkey(short pk);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int iIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid clsid);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string name, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string name);
    }
}
