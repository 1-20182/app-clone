using System.Runtime.InteropServices;
using System.Text;

namespace AppClone.Core;

/// <summary>
/// 深度隔离的注册表侧：把真实 HKCU 逐键复制到分身私有键下，
/// 注入的 DLL 用 RegOverridePredefKey 把目标进程的 HKEY_CURRENT_USER 指到这个私有键。
///
/// 复制策略：全部手动逐键复制并容错（跳过无法读取的受限 ACL 分支）；
/// 目标键一律继承默认安全描述符，保证分身注册表随时可被完整删除。
/// 分身根位于 HKCU\Software\FenShenSandbox 下，复制 Software 时跳过该分支避免自包含复制。
/// </summary>
public static class RegClone
{
    private const string SandboxRootName = "Software\\FenShenSandbox";

    private static readonly IntPtr HKCU = new(unchecked((int)0x80000001));
    private static readonly IntPtr HKLM_PTR = new(unchecked((int)0x80000002));
    private const int KEY_READ = 0x20019;
    private const int KEY_WRITE = 0x20006;
    private const int KEY_ALL_ACCESS = 0xF003F;
    private const int ERROR_NO_MORE_ITEMS = 259;

    // ---------------------------- 原生 API ----------------------------

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegOpenKeyExW(IntPtr hKey, string? lpSubKey, int ulOptions, int samDesired, out IntPtr phkResult);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegCreateKeyExW(IntPtr hKey, string lpSubKey, int reserved, string? lpClass,
        int dwOptions, int samDesired, IntPtr lpSecurityAttributes, out IntPtr phkResult, out int lpdwDisposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegDeleteTreeW(IntPtr hKey, string? lpSubKey);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegCloseKey(IntPtr hKey);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegEnumKeyExW(IntPtr hKey, int index, StringBuilder name, ref int nameLen,
        IntPtr lpClass, IntPtr lpClassLen, IntPtr reserved, IntPtr lastWriteTime);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegEnumValueW(IntPtr hKey, int index, StringBuilder name, ref int nameLen,
        IntPtr reserved, out uint type, byte[]? data, ref int dataLen);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegSetValueExW(IntPtr hKey, string lpValueName, int reserved, uint type,
        byte[] data, int cbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegQueryValueExW(IntPtr hKey, string lpValueName, IntPtr lpReserved,
        out uint type, out int data, ref int cbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegQueryValueExW(IntPtr hKey, string lpValueName, IntPtr lpReserved,
        out uint type, byte[] data, ref int cbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "RegSetValueExW")]
    private static extern int RegSetValueExBytes(IntPtr hKey, string lpValueName, int reserved, uint type,
        byte[] data, int cbData);

    // ---------------------------- 对外接口 ----------------------------

    /// <summary>分身在注册表中的私有根（位于真实 HKCU 下）。</summary>
    public static string CloneSubKey(string appId, string cloneId) =>
        $"{SandboxRootName}\\{appId}\\{cloneId}\\HKCU";

    /// <summary>注入 DLL 使用的子键路径（相对真实 HKCU）。</summary>
    public static string EnvSubKey(string appId, string cloneId) => CloneSubKey(appId, cloneId);

    /// <summary>确保分身注册表快照存在且完整；首次会整树复制 HKCU。</summary>
    /// <remarks>快照完成前不可用（无 FsReady 标记），此时重做，避免与预热任务竞争拿到半成品。</remarks>
    public static bool EnsureCloneRegistry(string appId, string cloneId, out string error)
    {
        error = "";
        string sub = CloneSubKey(appId, cloneId);
        if (IsReady(sub)) return true;

        RegDeleteTreeW(HKCU, sub);
        int rc = RegCreateKeyExW(HKCU, sub, 0, null, 0, KEY_ALL_ACCESS, IntPtr.Zero, out IntPtr hRoot, out _);
        if (rc != 0) { error = $"创建注册表分身根失败: {rc}"; return false; }
        try
        {
            CopyUser(HKCU, hRoot);
            MarkReady(hRoot);
            return true;
        }
        finally { RegCloseKey(hRoot); }
    }

    public static void ResetCloneRegistry(string appId, string cloneId)
    {
        DeleteCloneRoot(appId, cloneId);
        EnsureCloneRegistry(appId, cloneId, out _);
    }

    public static void DeleteCloneRoot(string appId, string cloneId) =>
        RegDeleteTreeW(HKCU, CloneSubKey(appId, cloneId));

    // ---------------------------- 设备分身 ----------------------------
    //
    // 在分身私有注册表内生成一套"假设备"数据（HKCU\FenShenDevice\...），
    // 注入 DLL 把应用对 HKLM 指纹路径的读取重定向到这里：
    //   MachineGuid / ProductId / 计算机名 / 主机名 / BIOS·主板序列号 / SQM MachineId
    // 数据在首次启用时生成并持久化——同一分身永远同一台"假设备"，
    // 不同分身之间互相不同，真实 HKLM 不做任何修改。

    private static readonly (string Hklm, string Rel)[] DeviceMirror =
    {
        (@"SOFTWARE\Microsoft\Cryptography",                       @"Software\Microsoft\Cryptography"),
        (@"SOFTWARE\Microsoft\Windows NT\CurrentVersion",          @"Software\Microsoft\Windows NT\CurrentVersion"),
        (@"SOFTWARE\Microsoft\SQMClient\MachineId",                @"Software\Microsoft\SQMClient\MachineId"),
        (@"SYSTEM\CurrentControlSet\Control\ComputerName",         @"System\CurrentControlSet\Control\ComputerName"),
        (@"SYSTEM\CurrentControlSet\Control\SystemInformation",    @"System\CurrentControlSet\Control\SystemInformation"),
        (@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters",    @"System\CurrentControlSet\Services\Tcpip\Parameters"),
        (@"HARDWARE\DESCRIPTION\System\BIOS",                      @"Hardware\Description\System\BIOS"),
    };

    /// <summary>确保分身的假设备数据存在；返回分身设备名（供启动器写环境变量）。</summary>
    public static string EnsureDeviceFake(string appId, string cloneId)
    {
        string cloneSub = CloneSubKey(appId, cloneId);              // ...\HKCU
        string deviceSub = cloneSub + @"\FenShenDevice";

        // 设备名持久化（同一分身稳定）
        string name = GetString(deviceSub, "DeviceName");
        if (string.IsNullOrEmpty(name))
        {
            // 由 cloneId 确定性生成：DESKTOP-XXXXXXX（NetBIOS 15 字符内）
            int seed = StableHash(cloneId);
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var suffixChars = new char[7];
            for (int i = 0; i < suffixChars.Length; i++)
                suffixChars[i] = alphabet[Next(ref seed) % alphabet.Length];
            name = "DESKTOP-" + new string(suffixChars);
            SetString(deviceSub, "DeviceName", name);
        }

        // 指纹数据镜像 + 随机化（只做一次；重置分身数据后重新生成）
        if (GetInt(deviceSub, "Mirrored") != 1)
        {
            int seed = StableHash(cloneId + "#device");
            var rng = new Random(seed);

            foreach (var (hklm, rel) in DeviceMirror)
                MirrorHklmSubtree(hklm, deviceSub + @"\" + rel);

            SetString(deviceSub + @"\Software\Microsoft\Cryptography", "MachineGuid",
                NewGuid(rng).ToString());
            SetString(deviceSub + @"\Software\Microsoft\Windows NT\CurrentVersion", "ProductId",
                $"00330-80000-00000-AA{rng.Next(1000):000}");
            SetString(deviceSub + @"\Software\Microsoft\SQMClient\MachineId", "MachineId",
                "{" + NewGuid(rng) + "}");
            SetString(deviceSub + @"\System\CurrentControlSet\Control\ComputerName\ComputerName",
                "ComputerName", name);
            SetString(deviceSub + @"\System\CurrentControlSet\Control\ComputerName\ActiveComputerName",
                "ComputerName", name);
            SetString(deviceSub + @"\System\CurrentControlSet\Services\Tcpip\Parameters",
                "Hostname", name);
            SetString(deviceSub + @"\System\CurrentControlSet\Services\Tcpip\Parameters",
                "NV Hostname", name);
            SetString(deviceSub + @"\System\CurrentControlSet\Control\SystemInformation",
                "SystemSerialNumber", RandAlnum(ref rng, 12));
            SetString(deviceSub + @"\Hardware\Description\System\BIOS",
                "SystemSerialNumber", RandAlnum(ref rng, 12));

            SetInt(deviceSub, "Mirrored", 1);
        }

        return name;
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            int h = (int)2166136261u;
            foreach (char c in s) { h ^= c; h *= 16777619; }
            return h;
        }
    }

    private static int Next(ref int seed)
    {
        unchecked { seed = seed * 1103515245 + 12345; return (seed >> 8) & 0x7FFFFFFF; }
    }

    private static Guid NewGuid(Random rng)
    {
        var bytes = new byte[16];
        rng.NextBytes(bytes);
        return new Guid(bytes);
    }

    private static string RandAlnum(ref Random rng, int len)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ123456789";
        var chars = new char[len];
        for (int i = 0; i < len; i++)
            chars[i] = alphabet[rng.Next(alphabet.Length)];
        return new string(chars);
    }

    private static string? GetString(string sub, string value)
    {
        if (RegOpenKeyExW(HKCU, sub, 0, KEY_READ, out var h) != 0) return null;
        try
        {
            var buf = new byte[512];
            int size = buf.Length;
            uint type;
            int rc = RegQueryValueExW(h, value, IntPtr.Zero, out type, buf, ref size);
            return rc == 0 && type == 1 ? Encoding.Unicode.GetString(buf, 0, size).TrimEnd('\0') : null;
        }
        finally { RegCloseKey(h); }
    }

    private static void SetString(string sub, string value, string data)
    {
        if (RegCreateKeyExW(HKCU, sub, 0, null, 0, KEY_WRITE, IntPtr.Zero, out var h, out _) != 0) return;
        try
        {
            var bytes = Encoding.Unicode.GetBytes(data + "\0");
            RegSetValueExBytes(h, value, 0, 1 /*REG_SZ*/, bytes, bytes.Length);
        }
        finally { RegCloseKey(h); }
    }

    private static int GetInt(string sub, string value)
    {
        if (RegOpenKeyExW(HKCU, sub, 0, KEY_READ, out var h) != 0) return 0;
        try
        {
            int v = 0, size = 4;
            uint type;
            return RegQueryValueExW(h, value, IntPtr.Zero, out type, out v, ref size) == 0 && type == 4 ? v : 0;
        }
        finally { RegCloseKey(h); }
    }

    private static void SetInt(string sub, string value, int data)
    {
        if (RegCreateKeyExW(HKCU, sub, 0, null, 0, KEY_WRITE, IntPtr.Zero, out var h, out _) != 0) return;
        try { RegSetValueExW(h, value, 0, 4, BitConverter.GetBytes(data), 4); }
        finally { RegCloseKey(h); }
    }

    /// <summary>把 HKLM 下的指纹子树镜像到分身私有区（逐键容错，目标键用默认 ACL）。</summary>
    private static void MirrorHklmSubtree(string hklmPath, string destSubPath)
    {
        // 目标父键 = HKCU 下 destSubPath 的父级
        int cut = destSubPath.LastIndexOf('\\');
        string parent = cut > 0 ? destSubPath[..cut] : "";
        string leaf = cut > 0 ? destSubPath[(cut + 1)..] : destSubPath;

        IntPtr hDstParent;
        if (parent.Length == 0) hDstParent = HKCU;
        else if (RegCreateKeyExW(HKCU, parent, 0, null, 0, KEY_READ | KEY_WRITE, IntPtr.Zero,
                    out hDstParent, out _) != 0)
            return;

        try
        {
            if (RegOpenKeyExW(HKLM_PTR, hklmPath, 0, KEY_READ, out var hSrc) != 0) return;
            try { CopyTreeManual(hSrc, hDstParent, leaf); }
            finally { RegCloseKey(hSrc); }
        }
        finally { if (parent.Length > 0) RegCloseKey(hDstParent); }
    }

    // ---------------------------- 复制实现 ----------------------------

    private const string ReadyValue = "FsReady";

    private static bool IsReady(string sub)
    {
        if (RegOpenKeyExW(HKCU, sub, 0, KEY_READ, out var h) != 0) return false;
        try
        {
            int v = 0, size = 4;
            uint type;
            int rc = RegQueryValueExW(h, ReadyValue, IntPtr.Zero, out type, out v, ref size);
            return rc == 0 && type == 4 && v == 1;
        }
        finally { RegCloseKey(h); }
    }

    private static void MarkReady(IntPtr hRoot)
    {
        RegSetValueExW(hRoot, ReadyValue, 0, 4 /*REG_DWORD*/, BitConverter.GetBytes(1), 4);
    }

    /// <summary>把 HKCU 的内容复制到 hRoot；每个顶层键独立容错。</summary>
    /// <remarks>分身根键位于 HKCU\Software\FenShenSandbox 下——复制 Software 子树时
    /// 必须跳过 FenShenSandbox，否则目标区域属于源树，会出现自包含的无限复制。</remarks>
    private static void CopyUser(IntPtr hSrcRoot, IntPtr hRoot)
    {
        foreach (var name in EnumSubKeys(hSrcRoot))
        {
            if (name == "Software")
            {
                if (RegCreateKeyExW(hRoot, name, 0, null, 0, KEY_READ | KEY_WRITE, IntPtr.Zero,
                        out IntPtr hDstSoft, out _) != 0)
                    continue;
                try
                {
                    if (RegOpenKeyExW(hSrcRoot, name, 0, KEY_READ, out IntPtr hSrcSoft) != 0)
                        continue;
                    try
                    {
                        foreach (var sub in EnumSubKeys(hSrcSoft))
                        {
                            if (sub == SandboxRootName[(SandboxRootName.LastIndexOf('\\') + 1)..])
                                continue;   // 跳过 FenShenSandbox 本身
                            CopySubTree(hSrcSoft, sub, hDstSoft);
                        }
                    }
                    finally { RegCloseKey(hSrcSoft); }
                }
                finally { RegCloseKey(hDstSoft); }
                continue;
            }

            CopySubTree(hSrcRoot, name, hRoot);
        }
    }

    /// <summary>把 hSrcParent 下的子键 name 复制到 hDstParent（逐键容错，目标键继承默认 ACL，
    /// 保证分身注册表随时可以被完整删除）。</summary>
    private static void CopySubTree(IntPtr hSrcParent, string name, IntPtr hDstParent)
    {
        if (RegOpenKeyExW(hSrcParent, name, 0, KEY_READ, out var hSub) != 0)
            return;   // 受限 ACL：跳过
        try { CopyTreeManual(hSub, hDstParent, name); }
        finally { RegCloseKey(hSub); }
    }

    private static List<string> EnumSubKeys(IntPtr hKey)
    {
        var list = new List<string>();
        for (int i = 0; ; i++)
        {
            var sb = new StringBuilder(512);
            int len = sb.Capacity;
            int rc = RegEnumKeyExW(hKey, i, sb, ref len, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (rc == ERROR_NO_MORE_ITEMS) break;
            if (rc != 0) continue;
            list.Add(sb.ToString(0, len));
        }
        return list;
    }

    /// <summary>逐键容错复制：读不了的键/值直接跳过，不影响其余部分。</summary>
    private static void CopyTreeManual(IntPtr hSrc, IntPtr hDstParent, string name)
    {
        if (RegCreateKeyExW(hDstParent, name, 0, null, 0, KEY_READ | KEY_WRITE, IntPtr.Zero,
                out IntPtr hDst, out _) != 0)
            return;
        try
        {
            CopyValues(hSrc, hDst);
            foreach (var sub in EnumSubKeys(hSrc))
            {
                if (RegOpenKeyExW(hSrc, sub, 0, KEY_READ, out var hSub) == 0)
                {
                    try { CopyTreeManual(hSub, hDst, sub); }
                    finally { RegCloseKey(hSub); }
                }
            }
        }
        finally { RegCloseKey(hDst); }
    }

    private static void CopyValues(IntPtr hSrc, IntPtr hDst)
    {
        var nameBuf = new StringBuilder(16384);
        for (int i = 0; ; i++)
        {
            int nameLen = nameBuf.Capacity;
            int dataLen = 0;
            int rc = RegEnumValueW(hSrc, i, nameBuf, ref nameLen, IntPtr.Zero, out uint type, null, ref dataLen);
            if (rc == ERROR_NO_MORE_ITEMS) break;
            if (rc != 0 && rc != 234 /*ERROR_MORE_DATA*/) break;
            if (rc == 234 && dataLen <= 0) { continue; }

            byte[] data = new byte[dataLen];
            int nameLen2 = nameBuf.Capacity;
            int rc2 = RegEnumValueW(hSrc, i, nameBuf, ref nameLen2, IntPtr.Zero, out type, data, ref dataLen);
            if (rc2 != 0) continue;

            string name = nameLen2 > 0 ? nameBuf.ToString(0, nameLen2) : "";
            RegSetValueExW(hDst, name, 0, type, data, dataLen);
        }
    }
}
