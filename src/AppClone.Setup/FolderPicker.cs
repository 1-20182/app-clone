using System.Runtime.InteropServices;

namespace AppCloneSetup;

/// <summary>
/// Windows 原生「选择文件夹」对话框（IFileOpenDialog + FOS_PICKFOLDERS）。
///
/// 为什么不用 WinForms 自带的 <see cref="FolderBrowserDialog"/>：
///   1) 初始目录无从指定 —— 目标目录不存在时它会自作主张弹在「此电脑」或桌面上，
///      用户每次都得自己一路点进去；
///   2) 地址栏不可靠地出现在导航窗格里，换盘符要多点两下；
///   3) 标题、确认按钮文案不可定制。
///
/// 本实现用资源管理器原生的对话框，于是白拿三件事：
///   · 顶部地址栏可直接粘贴 <c>D:\应用分身</c> 回车直达（换盘符真正的一步到位）；
///   · 左侧导航树含「此电脑」，可展开所有盘符；
///   · 自带「新建文件夹」。
/// 且初始目录会回溯到最近一个真实存在的祖先，保证「打开就落在用户上次的位置附近」。
/// </summary>
internal static class FolderPicker
{
    private const uint FOS_NOCHANGEDIR = 0x00000008;
    private const uint FOS_PICKFOLDERS = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint FOS_PATHMUSTEXIST = 0x00000800;

    private const uint SIGDN_FILESYSPATH = 0x80058000;

    // IShellItem 的 IID（shell32!SHCreateItemFromParsingName 需要）
    private static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    /// <summary>弹出文件夹选择器；用户取消或任何异常都返回 null（绝不让安装流程因选择器崩掉）。</summary>
    public static string? Pick(IWin32Window? owner, string? initialDir, string title, string okLabel)
    {
        IFileOpenDialog? dlg = null;
        try
        {
            dlg = (IFileOpenDialog)new FileOpenDialogRCW();

            dlg.GetOptions(out uint opts);
            dlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_NOCHANGEDIR);
            dlg.SetTitle(title);
            dlg.SetOkButtonLabel(okLabel);

            string? start = NearestExisting(initialDir);
            if (start is not null
                && SHCreateItemFromParsingName(start, IntPtr.Zero, IID_IShellItem, out IShellItem item) == 0)
            {
                dlg.SetFolder(item);
                Marshal.ReleaseComObject(item);
            }

            int hr = dlg.Show(owner is null ? IntPtr.Zero : owner.Handle);
            if (hr != 0) return null; // 用户取消（0x800704C7）等

            dlg.GetResult(out IShellItem result);
            result.GetDisplayName(SIGDN_FILESYSPATH, out string path);
            Marshal.ReleaseComObject(result);

            string p = (path ?? "").Trim().TrimEnd('\\');
            if (p.EndsWith(':')) p += "\\";   // 选中的是盘符根目录，补回分隔符
            return string.IsNullOrWhiteSpace(p) ? null : p;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (dlg is not null)
            {
                try { Marshal.ReleaseComObject(dlg); } catch { }
            }
        }
    }

    /// <summary>
    /// 从给定路径向上回溯，返回最近一个真实存在的目录；全部不存在时返回 null。
    /// 抽成 public 是为了能在无 GUI 的探针里直接断言（选择器本身弹不出来就没法测）。
    /// </summary>
    public static string? NearestExisting(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            string cur = path!.Trim();
            if (File.Exists(cur)) cur = Path.GetDirectoryName(cur) ?? cur;

            for (int i = 0; i < 64 && !string.IsNullOrEmpty(cur); i++)
            {
                if (Directory.Exists(cur)) return cur;
                string? parent = Path.GetDirectoryName(cur);
                if (string.IsNullOrEmpty(parent) || parent == cur) return null;
                cur = parent;
            }
        }
        catch { }
        return null;
    }

    // ---------------------------------------------------------------- COM 声明

    [ComImport]
    [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    [ClassInterface(ClassInterfaceType.None)]
    private class FileOpenDialogRCW { }

    [ComImport]
    [Guid("b4db1657-70d7-485e-8e3e-6fcb5a5c1802")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IModalWindow
    {
        [PreserveSig] int Show(IntPtr hwndOwner);
    }

    [ComImport]
    [Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog : IModalWindow
    {
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close([MarshalAs(UnmanagedType.Error)] int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
    }

    [ComImport]
    [Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog : IFileDialog
    {
        void GetResults(out IntPtr ppenum);
        void GetSelectedItems(out IntPtr ppsai);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);
}
