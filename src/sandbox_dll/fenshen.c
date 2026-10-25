// ============================================================================
//  FenShen sandbox DLL —— 应用分身深度隔离组件（含设备分身）
//
//  由分身启动器在目标进程挂起状态注入（CreateRemoteThread -> LoadLibraryW），
//  DllMain 阶段按环境变量分级启用：
//
//  1. 注册表隔离（FENSHEN_REG_SUBKEY）：
//     RegOverridePredefKey 把本进程 HKEY_CURRENT_USER 指向分身私有键。
//
//  2. 多开解锁（FENSHEN_MUTEX_TAG）：
//     IAT Hook 改名命名互斥体/信号量，使每个分身都认为自己是“第一个实例”。
//
//  3. 设备分身（FENSHEN_DEVICE_FAKE=1 + FENSHEN_DEVICE_NAME）：
//     - Hook RegOpenKeyEx/RegCreateKeyEx/RegGetValue：把设备指纹类 HKLM 路径
//       （MachineGuid/ProductId/ComputerName/BIOS 序列号等）重定向到启动器
//       预先生成好的分身私有键 HKCU\FenShenDevice\...（真实 HKLM 不动）；
//     - Hook GetComputerName/Ex：返回分身独立设备名；
//     - Hook GetVolumeInformation：返回分身独立卷序列号；
//     - Hook UuidCreateSequential：返回由分身标签派生的稳定 UUID；
//     - Hook GetAdaptersInfo / GetAdaptersAddresses：返回分身独立网卡 MAC。
//
//  4. 文件路径隔离（FENSHEN_CLONE_HOME/ROAMING/LOCAL/LOCAL_LOW，深度隔离即生效）：
//     Hook SHGetKnownFolderPath / SHGetFolderPathW / GetUserProfileDirectory / GetUserName。
//     Electron/Node 不读 APPDATA 环境变量，直接向 shell32/userenv 查询 ——
//     不拦这些调用，各分身会共用真实 AppData 与登录态。
//
//  以上 3/4 都靠 Hook LoadLibrary*：新模块加载完成后重新打补丁，
//  覆盖延迟加载的原生模块（Electron/Node 常见方式）。
//
//  FSDLL_DEBUG 构建会把 DllMain 与 Hook 命中情况写入 D:\fenshen\test\dll.log。
// ============================================================================

#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>   // 必须在 windows.h 之前：iphlpapi.h 用到 winsock2 的 SOCKET 类型
#include <windows.h>
#include <iphlpapi.h>
#include <wchar.h>
#include <string.h>
#include <stdarg.h>
#include <stdio.h>

#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "rpcrt4.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "iphlpapi.lib")

// 只用到 COM 任务内存分配，避免为了 objbase.h 引入额外依赖；
// 与 SHGetKnownFolderPath 的约定一致：调用方用 CoTaskMemFree 释放。
__declspec(dllimport) LPVOID WINAPI CoTaskMemAlloc(SIZE_T cb);

#ifdef FSDLL_DEBUG
static void DbgLog(const char *fmt, ...)
{
    DWORD lastError = GetLastError();
    HANDLE h = CreateFileW(L"D:\\fenshen\\test\\dll.log",
                           FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL,
                           OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h != INVALID_HANDLE_VALUE)
    {
        char buf[512];
        va_list args;
        va_start(args, fmt);
        int n = _vsnprintf_s(buf, sizeof(buf), _TRUNCATE, fmt, args);
        va_end(args);
        DWORD written = 0;
        if (n > 0) WriteFile(h, buf, (DWORD)n, &written, NULL);
        CloseHandle(h);
    }
    SetLastError(lastError);
}
#else
#define DbgLog(...)
#endif

// ---------------------------------------------------------------------------
// 环境（DllMain 读取一次）
// ---------------------------------------------------------------------------

static wchar_t g_tag[48]      = L"";   // 互斥体改名标签
static BOOL    g_deviceFake   = FALSE; // 设备分身总开关
static wchar_t g_devName[32]  = L"";   // 分身设备名
static int     g_devNameLen   = 0;
static HKEY    g_hRedirectedRoot = NULL;
static HMODULE g_self = NULL;
static wchar_t g_taskbarGroup[128] = L"";  // 任务栏分组 AppUserModelID（可选，空=不设置）
// 文件隔离：已知文件夹重定向目标（空=不重定向）。只改环境变量盖不住 Electron/Node，
// 它们用 SHGetKnownFolderPath / GetUserProfileDirectory 直接问 shell32。
static wchar_t g_cloneHome[520]  = L"";
static wchar_t g_roaming[520]    = L"";
static wchar_t g_local[520]      = L"";
static wchar_t g_localLow[520]   = L"";
static wchar_t g_userName[64]    = L"";

static void UpperCopy(wchar_t *dst, const wchar_t *src, size_t cch)
{
    size_t i;
    for (i = 0; src[i] && i < cch - 1; i++)
    {
        wchar_t c = src[i];
        dst[i] = (c >= L'a' && c <= L'z') ? (wchar_t)(c - L'a' + L'A') : c;
    }
    dst[i] = 0;
}

static void LoadEnv(void)
{
    wchar_t buf[256];
    if (GetEnvironmentVariableW(L"FENSHEN_MUTEX_TAG", buf, 128) > 0 && buf[0] &&
        wcslen(buf) < 48)
        wcscpy_s(g_tag, 48, buf);

    if (GetEnvironmentVariableW(L"FENSHEN_DEVICE_FAKE", buf, 16) > 0 &&
        wcscmp(buf, L"1") == 0)
    {
        DWORD n = GetEnvironmentVariableW(L"FENSHEN_DEVICE_NAME", g_devName, 32);
        if (n > 0 && n < 32)
        {
            g_deviceFake = TRUE;
            g_devNameLen = (int)n;
        }
    }

    // 任务栏分组：AppUserModelID 只认进程自身设置的值，快捷方式属性不会传给子进程
    if (GetEnvironmentVariableW(L"FENSHEN_TASKBAR_GROUP", g_taskbarGroup, 128) < 1)
        g_taskbarGroup[0] = 0;

    // 已知文件夹重定向（深度隔离必填；缺项时对应 API 走原逻辑）
    if (GetEnvironmentVariableW(L"FENSHEN_CLONE_HOME", g_cloneHome, 520) >= 520)
        g_cloneHome[0] = 0;
    if (GetEnvironmentVariableW(L"FENSHEN_ROAMING", g_roaming, 520) >= 520)
        g_roaming[0] = 0;
    if (GetEnvironmentVariableW(L"FENSHEN_LOCAL", g_local, 520) >= 520)
        g_local[0] = 0;
    if (GetEnvironmentVariableW(L"FENSHEN_LOCAL_LOW", g_localLow, 520) >= 520)
        g_localLow[0] = 0;

    // 假用户名：随设备分身一起启用；未设置时 GetUserName 保持真实值
    if (GetEnvironmentVariableW(L"FENSHEN_USER_NAME", g_userName, 64) >= 64)
        g_userName[0] = 0;
}

// ---------------------------------------------------------------------------
// IAT Hook 基础设施
// ---------------------------------------------------------------------------

typedef struct { FARPROC old; void *hook; } HookPair;

static HookPair g_hooks[128];
static int      g_hookCount = 0;

static void AddPair(FARPROC old, void *hook)
{
    if (!old || g_hookCount >= (int)(sizeof(g_hooks) / sizeof(g_hooks[0])))
        return;
    for (int i = 0; i < g_hookCount; i++)
        if (g_hooks[i].old == old)
            return;
    g_hooks[g_hookCount].old = old;
    g_hooks[g_hookCount].hook = hook;
    g_hookCount++;
}

// 依次在多个模块中解析导出；每个解析到的地址都登记为匹配目标，
// 原函数指针取第一个非空结果（优先 kernelbase —— 一定是真实实现）。
static FARPROC AddHook(const char *name, void *hook, void **pOriginal,
                       HMODULE m1, HMODULE m2, HMODULE m3)
{
    HMODULE mods[3] = { m1, m2, m3 };
    FARPROC first = NULL;
    for (int i = 0; i < 3; i++)
    {
        FARPROC p = mods[i] ? GetProcAddress(mods[i], name) : NULL;
        if (p)
        {
            AddPair(p, hook);
            if (!first) first = p;
        }
    }
    if (pOriginal) *pOriginal = (void *)first;
    return first;
}

static void PatchSlot(void **slot, void *newFn)
{
    DWORD oldProt = 0;
    if (VirtualProtect(slot, sizeof(void *), PAGE_READWRITE, &oldProt))
    {
        *slot = newFn;
        VirtualProtect(slot, sizeof(void *), oldProt, &oldProt);
        FlushInstructionCache(GetCurrentProcess(), slot, sizeof(void *));
    }
}

// 系统模块排除名单：只 Hook 应用自身的模块
static BOOL IsSystemModule(const wchar_t *baseName)
{
    static const wchar_t *skip[] = {
        L"ntdll.dll", L"kernel32.dll", L"kernelbase.dll", L"ucrtbase.dll",
        L"msvcrt.dll", L"advapi32.dll", L"user32.dll", L"gdi32.dll",
        L"shell32.dll", L"ole32.dll", L"oleaut32.dll", L"combase.dll",
        L"shlwapi.dll", L"comdlg32.dll", L"sechost.dll", L"rpcrt4.dll",
        L"kernel.appcore.dll", L"bcryptprimitives.dll", L"cryptbase.dll",
    };
    for (int i = 0; i < (int)(sizeof(skip) / sizeof(skip[0])); i++)
        if (_wcsicmp(baseName, skip[i]) == 0)
            return TRUE;
    if (_wcsnicmp(baseName, L"api-ms-win-", 11) == 0)
        return TRUE;
    if (_wcsnicmp(baseName, L"ext-ms-", 7) == 0)
        return TRUE;
    return FALSE;
}

static void PatchModuleIat(HMODULE hMod)
{
    __try
    {
        if (!hMod || hMod == g_self)
            return;

        wchar_t path[MAX_PATH + 2] = L"";
        if (GetModuleFileNameW(hMod, path, MAX_PATH + 1) == 0)
            return;
        const wchar_t *base = path, *q;
        for (q = path; *q; q++) if (*q == L'\\') base = q + 1;
        if (IsSystemModule(base))
            return;

        BYTE *b = (BYTE *)hMod;
        IMAGE_DOS_HEADER *dos = (IMAGE_DOS_HEADER *)b;
        if (dos->e_magic != IMAGE_DOS_SIGNATURE)
            return;

        IMAGE_NT_HEADERS *nt = (IMAGE_NT_HEADERS *)(b + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE)
            return;

        IMAGE_DATA_DIRECTORY *dir =
            &nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
        if (!dir->VirtualAddress)
            return;

        IMAGE_IMPORT_DESCRIPTOR *desc =
            (IMAGE_IMPORT_DESCRIPTOR *)(b + dir->VirtualAddress);

        int patched = 0;
        for (; desc->Name; desc++)
        {
            IMAGE_THUNK_DATA *ft = (IMAGE_THUNK_DATA *)(b + desc->FirstThunk);
            for (; ft->u1.Function; ft++)
            {
                FARPROC cur = (FARPROC)ft->u1.Function;
                for (int i = 0; i < g_hookCount; i++)
                {
                    if (cur == g_hooks[i].old)
                    {
                        PatchSlot((void **)&ft->u1.Function, g_hooks[i].hook);
                        patched++;
                        break;
                    }
                }
            }
        }
        if (patched) DbgLog("patched %hs x%d\r\n", base, patched);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        DbgLog("patch-iat exception\r\n");
    }
}

// PEB 遍历已加载模块
typedef struct _FS_UNICODE_STRING {
    USHORT Length;
    USHORT MaximumLength;
    PWSTR  Buffer;
} FS_UNICODE_STRING;

static void HookAllLoadedModules(void)
{
#ifdef _WIN64
    BYTE *peb = (BYTE *)__readgsqword(0x60);
    BYTE *ldr = *(BYTE **)(peb + 0x18);
    LIST_ENTRY *head = (LIST_ENTRY *)(ldr + 0x10);
#else
    BYTE *peb = (BYTE *)__readfsdword(0x30);
    BYTE *ldr = *(BYTE **)(peb + 0x0C);
    LIST_ENTRY *head = (LIST_ENTRY *)(ldr + 0x0C);
#endif

    __try
    {
        typedef struct {
            LIST_ENTRY InLoadOrderLinks;
            LIST_ENTRY InMemoryOrderLinks;
            LIST_ENTRY InInitializationOrderLinks;
            PVOID DllBase;
            PVOID EntryPoint;
            ULONG SizeOfImage;
            FS_UNICODE_STRING FullDllName;
            FS_UNICODE_STRING BaseDllName;
        } LDR_ENTRY;

        for (LIST_ENTRY *p = head->Flink; p && p != head; p = p->Flink)
        {
            LDR_ENTRY *e = CONTAINING_RECORD(p, LDR_ENTRY, InLoadOrderLinks);
            if (e->DllBase && e->DllBase != (PVOID)g_self)
                PatchModuleIat((HMODULE)e->DllBase);
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
    }
}

// ---------------------------------------------------------------------------
// 注册表隔离
// ---------------------------------------------------------------------------

static void DoRegistryRedirect(void)
{
    wchar_t sub[1024];
    DWORD n = GetEnvironmentVariableW(L"FENSHEN_REG_SUBKEY", sub, 1024);
    if (n == 0 || n >= 1024)
        return;

    HKEY hRoot = NULL;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, sub, 0, MAXIMUM_ALLOWED, &hRoot) == ERROR_SUCCESS)
    {
        if (RegOverridePredefKey(HKEY_CURRENT_USER, hRoot) == ERROR_SUCCESS)
            g_hRedirectedRoot = hRoot;
        else
            RegCloseKey(hRoot);
    }
}

// ---------------------------------------------------------------------------
// 设备分身：HKLM 指纹路径重定向
// ---------------------------------------------------------------------------

static const wchar_t *g_fakePrefixes[] = {
    L"SOFTWARE\\MICROSOFT\\CRYPTOGRAPHY",
    L"SOFTWARE\\MICROSOFT\\WINDOWS NT\\CURRENTVERSION",
    L"SOFTWARE\\MICROSOFT\\SQMCLIENT",
    L"SYSTEM\\CURRENTCONTROLSET\\CONTROL\\COMPUTERNAME",
    L"SYSTEM\\CURRENTCONTROLSET\\CONTROL\\SYSTEMINFORMATION",
    L"SYSTEM\\CURRENTCONTROLSET\\SERVICES\\TCPIP\\PARAMETERS",
    L"HARDWARE\\DESCRIPTION\\SYSTEM\\BIOS",
};

static BOOL TryRewriteHklmPath(const wchar_t *sub, wchar_t *out, size_t cch)
{
    if (!g_deviceFake || !sub || !sub[0] || wcslen(sub) >= 900)
        return FALSE;

    wchar_t up[1024];
    UpperCopy(up, sub, 1024);
    for (int i = 0; i < (int)(sizeof(g_fakePrefixes) / sizeof(g_fakePrefixes[0])); i++)
    {
        if (_wcsnicmp(up, g_fakePrefixes[i], wcslen(g_fakePrefixes[i])) == 0)
        {
            _snwprintf_s(out, cch, _TRUNCATE, L"FenShenDevice\\%s", sub);
            return TRUE;
        }
    }
    return FALSE;
}

// ---------------------------------------------------------------------------
// Hook 实现
// ---------------------------------------------------------------------------

// ---- 多开解锁：互斥体 / 信号量改名 ----

#define NAME_BUF 640

static void RenameName(const wchar_t *orig, wchar_t *out)
{
    const wchar_t *rest = orig;
    if (_wcsnicmp(orig, L"Global\\", 7) == 0) rest = orig + 7;
    else if (_wcsnicmp(orig, L"Local\\", 6) == 0)  rest = orig + 6;
    _snwprintf_s(out, NAME_BUF, _TRUNCATE, L"Local\\FenShen_%ls_%ls", g_tag, rest);
}

typedef HANDLE (WINAPI *PFN_CreateMutexW)(LPSECURITY_ATTRIBUTES, BOOL, LPCWSTR);
typedef HANDLE (WINAPI *PFN_CreateMutexA)(LPSECURITY_ATTRIBUTES, BOOL, LPCSTR);
typedef HANDLE (WINAPI *PFN_CreateMutexExW)(LPSECURITY_ATTRIBUTES, LPCWSTR, DWORD, DWORD);
typedef HANDLE (WINAPI *PFN_CreateMutexExA)(LPSECURITY_ATTRIBUTES, LPCSTR, DWORD, DWORD);
typedef HANDLE (WINAPI *PFN_OpenMutexW)(DWORD, BOOL, LPCWSTR);
typedef HANDLE (WINAPI *PFN_OpenMutexA)(DWORD, BOOL, LPCSTR);
typedef HANDLE (WINAPI *PFN_CreateSemaphoreW)(LPSECURITY_ATTRIBUTES, LONG, LONG, LPCWSTR);
typedef HANDLE (WINAPI *PFN_CreateSemaphoreA)(LPSECURITY_ATTRIBUTES, LONG, LONG, LPCSTR);

static PFN_CreateMutexW    p_CreateMutexW;
static PFN_CreateMutexA    p_CreateMutexA;
static PFN_CreateMutexExW  p_CreateMutexExW;
static PFN_CreateMutexExA  p_CreateMutexExA;
static PFN_OpenMutexW      p_OpenMutexW;
static PFN_OpenMutexA      p_OpenMutexA;
static PFN_CreateSemaphoreW p_CreateSemaphoreW;
static PFN_CreateSemaphoreA p_CreateSemaphoreA;

static void RenameAnsi(const char *name, char *out)
{
    wchar_t w[NAME_BUF / 2], r[NAME_BUF];
    MultiByteToWideChar(CP_ACP, 0, name, -1, w, NAME_BUF / 2);
    RenameName(w, r);
    WideCharToMultiByte(CP_ACP, 0, r, -1, out, NAME_BUF, NULL, NULL);
}

HANDLE WINAPI Hook_CreateMutexW(LPSECURITY_ATTRIBUTES sa, BOOL initial, LPCWSTR name)
{
    wchar_t buf[NAME_BUF];
    if (name && g_tag[0]) { RenameName(name, buf); name = buf; }
    return p_CreateMutexW(sa, initial, name);
}
HANDLE WINAPI Hook_CreateMutexA(LPSECURITY_ATTRIBUTES sa, BOOL initial, LPCSTR name)
{
    char buf[NAME_BUF];
    if (name && g_tag[0]) { RenameAnsi(name, buf); name = buf; }
    return p_CreateMutexA(sa, initial, name);
}
HANDLE WINAPI Hook_CreateMutexExW(LPSECURITY_ATTRIBUTES sa, LPCWSTR name, DWORD flags, DWORD access)
{
    wchar_t buf[NAME_BUF];
    if (name && g_tag[0]) { RenameName(name, buf); name = buf; }
    return p_CreateMutexExW(sa, name, flags, access);
}
HANDLE WINAPI Hook_CreateMutexExA(LPSECURITY_ATTRIBUTES sa, LPCSTR name, DWORD flags, DWORD access)
{
    char buf[NAME_BUF];
    if (name && g_tag[0]) { RenameAnsi(name, buf); name = buf; }
    return p_CreateMutexExA(sa, name, flags, access);
}
HANDLE WINAPI Hook_OpenMutexW(DWORD access, BOOL inherit, LPCWSTR name)
{
    wchar_t buf[NAME_BUF];
    if (name && g_tag[0]) { RenameName(name, buf); name = buf; }
    return p_OpenMutexW(access, inherit, name);
}
HANDLE WINAPI Hook_OpenMutexA(DWORD access, BOOL inherit, LPCSTR name)
{
    char buf[NAME_BUF];
    if (name && g_tag[0]) { RenameAnsi(name, buf); name = buf; }
    return p_OpenMutexA(access, inherit, name);
}
HANDLE WINAPI Hook_CreateSemaphoreW(LPSECURITY_ATTRIBUTES sa, LONG init, LONG max, LPCWSTR name)
{
    wchar_t buf[NAME_BUF];
    if (name && g_tag[0]) { RenameName(name, buf); name = buf; }
    return p_CreateSemaphoreW(sa, init, max, name);
}
HANDLE WINAPI Hook_CreateSemaphoreA(LPSECURITY_ATTRIBUTES sa, LONG init, LONG max, LPCSTR name)
{
    char buf[NAME_BUF];
    if (name && g_tag[0]) { RenameAnsi(name, buf); name = buf; }
    return p_CreateSemaphoreA(sa, init, max, name);
}

// ---- 设备分身：注册表路径重定向 ----

typedef LSTATUS (WINAPI *PFN_RegOpenKeyExW)(HKEY, LPCWSTR, DWORD, REGSAM, PHKEY);
typedef LSTATUS (WINAPI *PFN_RegOpenKeyExA)(HKEY, LPCSTR, DWORD, REGSAM, PHKEY);
typedef LSTATUS (WINAPI *PFN_RegCreateKeyExW)(HKEY, LPCWSTR, DWORD, LPWSTR, DWORD, REGSAM,
    const SECURITY_ATTRIBUTES *, PHKEY, LPDWORD);
typedef LSTATUS (WINAPI *PFN_RegCreateKeyExA)(HKEY, LPCSTR, DWORD, LPSTR, DWORD, REGSAM,
    const SECURITY_ATTRIBUTES *, PHKEY, LPDWORD);
typedef LSTATUS (WINAPI *PFN_RegGetValueW)(HKEY, LPCWSTR, LPCWSTR, DWORD, LPDWORD, PVOID, LPDWORD);
typedef LSTATUS (WINAPI *PFN_RegGetValueA)(HKEY, LPCSTR, LPCSTR, DWORD, LPDWORD, PVOID, LPDWORD);

static PFN_RegOpenKeyExW   p_RegOpenKeyExW;
static PFN_RegOpenKeyExA   p_RegOpenKeyExA;
static PFN_RegCreateKeyExW p_RegCreateKeyExW;
static PFN_RegCreateKeyExA p_RegCreateKeyExA;
static PFN_RegGetValueW    p_RegGetValueW;
static PFN_RegGetValueA    p_RegGetValueA;

LSTATUS WINAPI Hook_RegOpenKeyExW(HKEY hKey, LPCWSTR sub, DWORD opts, REGSAM sam, PHKEY out)
{
    wchar_t rew[1024];
    if (hKey == HKEY_LOCAL_MACHINE && TryRewriteHklmPath(sub, rew, 1024))
    {
        HKEY hFake;
        if (p_RegOpenKeyExW(HKEY_CURRENT_USER, rew, opts, sam, &hFake) == ERROR_SUCCESS)
        {
            *out = hFake;
            return ERROR_SUCCESS;
        }
        // 假键缺失时回落真实路径，避免应用异常
    }
    return p_RegOpenKeyExW(hKey, sub, opts, sam, out);
}

LSTATUS WINAPI Hook_RegOpenKeyExA(HKEY hKey, LPCSTR sub, DWORD opts, REGSAM sam, PHKEY out)
{
    char rew[1024];
    if (hKey == HKEY_LOCAL_MACHINE && sub)
    {
        wchar_t w[512], rewW[1024];
        MultiByteToWideChar(CP_ACP, 0, sub, -1, w, 512);
        if (TryRewriteHklmPath(w, rewW, 1024))
        {
            WideCharToMultiByte(CP_ACP, 0, rewW, -1, rew, 1024, NULL, NULL);
            HKEY hFake;
            if (p_RegOpenKeyExA(HKEY_CURRENT_USER, rew, opts, sam, &hFake) == ERROR_SUCCESS)
            {
                *out = hFake;
                return ERROR_SUCCESS;
            }
        }
    }
    return p_RegOpenKeyExA(hKey, sub, opts, sam, out);
}

LSTATUS WINAPI Hook_RegCreateKeyExW(HKEY hKey, LPCWSTR sub, DWORD reserved, LPWSTR cls,
    DWORD opts, REGSAM sam, const SECURITY_ATTRIBUTES *sa, PHKEY out, LPDWORD disp)
{
    wchar_t rew[1024];
    if (hKey == HKEY_LOCAL_MACHINE && TryRewriteHklmPath(sub, rew, 1024))
        return p_RegCreateKeyExW(HKEY_CURRENT_USER, rew, reserved, cls, opts, sam, sa, out, disp);
    return p_RegCreateKeyExW(hKey, sub, reserved, cls, opts, sam, sa, out, disp);
}

LSTATUS WINAPI Hook_RegCreateKeyExA(HKEY hKey, LPCSTR sub, DWORD reserved, LPSTR cls,
    DWORD opts, REGSAM sam, const SECURITY_ATTRIBUTES *sa, PHKEY out, LPDWORD disp)
{
    char rew[1024];
    if (hKey == HKEY_LOCAL_MACHINE && sub)
    {
        wchar_t w[512], rewW[1024];
        MultiByteToWideChar(CP_ACP, 0, sub, -1, w, 512);
        if (TryRewriteHklmPath(w, rewW, 1024))
        {
            WideCharToMultiByte(CP_ACP, 0, rewW, -1, rew, 1024, NULL, NULL);
            return p_RegCreateKeyExA(HKEY_CURRENT_USER, rew, reserved, cls, opts, sam, sa, out, disp);
        }
    }
    return p_RegCreateKeyExA(hKey, sub, reserved, cls, opts, sam, sa, out, disp);
}

LSTATUS WINAPI Hook_RegGetValueW(HKEY hKey, LPCWSTR sub, LPCWSTR value, DWORD flags,
    LPDWORD type, PVOID data, LPDWORD dataLen)
{
    wchar_t rew[1024];
    if (hKey == HKEY_LOCAL_MACHINE && TryRewriteHklmPath(sub, rew, 1024))
    {
        HKEY hFake;
        if (p_RegOpenKeyExW(HKEY_CURRENT_USER, rew, 0, KEY_READ, &hFake) == ERROR_SUCCESS)
        {
            LSTATUS rc = p_RegGetValueW(hFake, NULL, value, flags, type, data, dataLen);
            RegCloseKey(hFake);
            return rc;
        }
    }
    return p_RegGetValueW(hKey, sub, value, flags, type, data, dataLen);
}

LSTATUS WINAPI Hook_RegGetValueA(HKEY hKey, LPCSTR sub, LPCSTR value, DWORD flags,
    LPDWORD type, PVOID data, LPDWORD dataLen)
{
    char rew[1024];
    if (hKey == HKEY_LOCAL_MACHINE && sub)
    {
        wchar_t w[512], rewW[1024];
        MultiByteToWideChar(CP_ACP, 0, sub, -1, w, 512);
        if (TryRewriteHklmPath(w, rewW, 1024))
        {
            WideCharToMultiByte(CP_ACP, 0, rewW, -1, rew, 1024, NULL, NULL);
            HKEY hFake;
            if (p_RegOpenKeyExA(HKEY_CURRENT_USER, rew, 0, KEY_READ, &hFake) == ERROR_SUCCESS)
            {
                LSTATUS rc = p_RegGetValueA(hFake, NULL, value, flags, type, data, dataLen);
                RegCloseKey(hFake);
                return rc;
            }
        }
    }
    return p_RegGetValueA(hKey, sub, value, flags, type, data, dataLen);
}

// ---- 设备分身：计算机名 ----

typedef BOOL (WINAPI *PFN_GetComputerNameW)(LPWSTR, LPDWORD);
typedef BOOL (WINAPI *PFN_GetComputerNameA)(LPSTR, LPDWORD);
typedef BOOL (WINAPI *PFN_GetComputerNameExW)(int, LPWSTR, LPDWORD);
typedef BOOL (WINAPI *PFN_GetComputerNameExA)(int, LPSTR, LPDWORD);

static PFN_GetComputerNameW   p_GetComputerNameW;
static PFN_GetComputerNameA   p_GetComputerNameA;
static PFN_GetComputerNameExW p_GetComputerNameExW;
static PFN_GetComputerNameExA p_GetComputerNameExA;

static BOOL FakeComputerNameEx(int type)
{
    // ComputerNameNetBIOS / DnsHostname / PhysicalNetBIOS / PhysicalDnsHostname
    return type == 0 || type == 1 || type == 5 || type == 7;
}

BOOL WINAPI Hook_GetComputerNameW(LPWSTR buffer, LPDWORD size)
{
    if (!g_deviceFake || !buffer || !size || !g_devName[0])
        return p_GetComputerNameW(buffer, size);
    if (*size <= (DWORD)g_devNameLen)
    {
        *size = (DWORD)g_devNameLen + 1;
        SetLastError(ERROR_BUFFER_OVERFLOW);
        return FALSE;
    }
    lstrcpynW(buffer, g_devName, g_devNameLen + 1);
    *size = (DWORD)g_devNameLen;
    return TRUE;
}

BOOL WINAPI Hook_GetComputerNameA(LPSTR buffer, LPDWORD size)
{
    if (!g_deviceFake || !buffer || !size || !g_devName[0])
        return p_GetComputerNameA(buffer, size);
    char ansi[32];
    WideCharToMultiByte(CP_ACP, 0, g_devName, -1, ansi, 32, NULL, NULL);
    int len = (int)strlen(ansi);
    if (*size <= (DWORD)len)
    {
        *size = (DWORD)len + 1;
        SetLastError(ERROR_BUFFER_OVERFLOW);
        return FALSE;
    }
    lstrcpyA(buffer, ansi);
    *size = (DWORD)len;
    return TRUE;
}

BOOL WINAPI Hook_GetComputerNameExW(int type, LPWSTR buffer, LPDWORD size)
{
    if (!g_deviceFake || !FakeComputerNameEx(type) || !buffer || !size || !g_devName[0])
        return p_GetComputerNameExW(type, buffer, size);
    if (*size <= (DWORD)g_devNameLen)
    {
        *size = (DWORD)g_devNameLen + 1;
        SetLastError(ERROR_MORE_DATA);
        return FALSE;
    }
    lstrcpynW(buffer, g_devName, g_devNameLen + 1);
    *size = (DWORD)g_devNameLen;
    return TRUE;
}

BOOL WINAPI Hook_GetComputerNameExA(int type, LPSTR buffer, LPDWORD size)
{
    if (!g_deviceFake || !FakeComputerNameEx(type) || !buffer || !size || !g_devName[0])
        return p_GetComputerNameExA(type, buffer, size);
    char ansi[32];
    WideCharToMultiByte(CP_ACP, 0, g_devName, -1, ansi, 32, NULL, NULL);
    int len = (int)strlen(ansi);
    if (*size <= (DWORD)len)
    {
        *size = (DWORD)len + 1;
        SetLastError(ERROR_MORE_DATA);
        return FALSE;
    }
    lstrcpyA(buffer, ansi);
    *size = (DWORD)len;
    return TRUE;
}

// ---- 设备分身：卷序列号 ----

typedef BOOL (WINAPI *PFN_GetVolumeInformationW)(LPCWSTR, LPWSTR, DWORD, LPDWORD,
    LPDWORD, LPDWORD, LPWSTR, DWORD);
typedef BOOL (WINAPI *PFN_GetVolumeInformationA)(LPCSTR, LPSTR, DWORD, LPDWORD,
    LPDWORD, LPDWORD, LPSTR, DWORD);

static PFN_GetVolumeInformationW p_GetVolumeInformationW;
static PFN_GetVolumeInformationA p_GetVolumeInformationA;

static DWORD Fnv1a(const wchar_t *s, DWORD seed)
{
    DWORD h = seed;
    for (; *s; s++) { h ^= (DWORD)(*s & 0xFF); h *= 16777619u; h ^= (DWORD)(*s >> 8); h *= 16777619u; }
    return h ? h : 0xA5A5A5A5u;
}

static DWORD FakeVolumeSerial(LPCWSTR root)
{
    wchar_t r[8] = L"C:";
    if (root && root[0]) { r[0] = root[0]; r[1] = L':'; r[2] = 0; }
    return Fnv1a(r, Fnv1a(g_tag, 2166136261u));
}

BOOL WINAPI Hook_GetVolumeInformationW(LPCWSTR root, LPWSTR vol, DWORD volSize, LPDWORD serial,
    LPDWORD maxCompLen, LPDWORD fsFlags, LPWSTR fs, DWORD fsSize)
{
    BOOL r = p_GetVolumeInformationW(root, vol, volSize, serial, maxCompLen, fsFlags, fs, fsSize);
    if (g_deviceFake && serial)
        *serial = FakeVolumeSerial(root);
    return r;
}

BOOL WINAPI Hook_GetVolumeInformationA(LPCSTR root, LPSTR vol, DWORD volSize, LPDWORD serial,
    LPDWORD maxCompLen, LPDWORD fsFlags, LPSTR fs, DWORD fsSize)
{
    BOOL r = p_GetVolumeInformationA(root, vol, volSize, serial, maxCompLen, fsFlags, fs, fsSize);
    if (g_deviceFake && serial)
    {
        wchar_t wr[8] = L"C:";
        if (root && root[0]) { wchar_t w[8]; MultiByteToWideChar(CP_ACP, 0, root, -1, w, 8); wr[0] = w[0]; }
        *serial = FakeVolumeSerial(wr);
    }
    return r;
}

// ---- 设备分身：顺序 UUID（常被用作 MAC 相关指纹） ----

typedef LONG (WINAPI *PFN_UuidCreateSequential)(GUID *);

static PFN_UuidCreateSequential p_UuidCreateSequential;

static void DeriveBytes(unsigned char out[16])
{
    // 由互斥体标签派生 16 字节稳定数据（每个分身不同、每次启动相同）
    DWORD seed = 2166136261u;
    for (int b = 0; b < 16; b++)
    {
        seed ^= (DWORD)(g_tag[b % (wcslen(g_tag) ? wcslen(g_tag) : 1)] & 0xFF);
        seed *= 16777619u;
        seed += (DWORD)b * 2654435761u;
        out[b] = (unsigned char)((seed >> 13) & 0xFF);
    }
    // 版本 4 / 变体 10，看起来仍像标准 UUID
    out[7] = (unsigned char)((out[7] & 0x0F) | 0x40);
    out[8] = (unsigned char)((out[8] & 0x3F) | 0x80);
}

LONG WINAPI Hook_UuidCreateSequential(GUID *id)
{
    if (!g_deviceFake || !id)
        return p_UuidCreateSequential(id);
    unsigned char raw[16];
    DeriveBytes(raw);
    memcpy(id, raw, 16);
    return 0;   // RPC_S_OK
}

// ---- 设备分身：网卡 MAC（GetAdaptersInfo / GetAdaptersAddresses） ----
// MAC 是风控判定「同一台机器」最常用的信号之一（Node 的 os.networkInterfaces()
// 走 GetAdaptersAddresses）。只改返回缓冲区里的副本，真实网卡不动。
// 边界：设备分身开启即代表用户接受“伪造设备身份”，依赖 MAC 做授权的软件会因此失效。

typedef ULONG (WINAPI *PFN_GetAdaptersInfo)(PIP_ADAPTER_INFO, PULONG);
typedef ULONG (WINAPI *PFN_GetAdaptersAddresses)(ULONG, ULONG, PVOID, PIP_ADAPTER_ADDRESSES, PULONG);

static PFN_GetAdaptersInfo       p_GetAdaptersInfo;
static PFN_GetAdaptersAddresses  p_GetAdaptersAddresses;
static BYTE g_mac[6];   // 02 前缀 = 本地管理地址：合法格式，且不与真实厂商 OUI 冲突

static void DeriveMac(void)
{
    DWORD h = Fnv1a(g_devName, Fnv1a(g_tag, 2166136261u));
    g_mac[0] = 0x02;
    for (int i = 1; i < 6; i++) { g_mac[i] = (BYTE)(h & 0xFF); h >>= 8; }
}

static void FakeMacFor(int index, BYTE out[6])
{
    memcpy(out, g_mac, 6);
    if (index > 0) out[5] ^= (BYTE)index;   // 同一台机器上的多张网卡仍互不相同
}

ULONG WINAPI Hook_GetAdaptersInfo(PIP_ADAPTER_INFO info, PULONG size)
{
    ULONG r = p_GetAdaptersInfo(info, size);
    if (r == ERROR_SUCCESS && info && g_deviceFake)
    {
        int i = 0;
        for (PIP_ADAPTER_INFO a = info; a; a = a->Next, i++)
        {
            FakeMacFor(i, a->Address);
            memset(a->Address + 6, 0, sizeof(a->Address) - 6);
            a->AddressLength = 6;
        }
    }
    return r;
}

ULONG WINAPI Hook_GetAdaptersAddresses(ULONG family, ULONG flags, PVOID reserved,
                                      PIP_ADAPTER_ADDRESSES addrs, PULONG size)
{
    ULONG r = p_GetAdaptersAddresses(family, flags, reserved, addrs, size);
    if (r == ERROR_SUCCESS && addrs && g_deviceFake)
    {
        int i = 0;
        for (PIP_ADAPTER_ADDRESSES a = addrs; a; a = a->Next, i++)
        {
            FakeMacFor(i, a->PhysicalAddress);
            memset(a->PhysicalAddress + 6, 0, sizeof(a->PhysicalAddress) - 6);
            a->PhysicalAddressLength = 6;
        }
    }
    return r;
}

// ---- 文件隔离：已知文件夹 / 用户名（shell32 / userenv / advapi32） ----

typedef HRESULT (WINAPI *PFN_SHGetKnownFolderPath)(const GUID *, DWORD, HANDLE, PWSTR *);
typedef HRESULT (WINAPI *PFN_SHGetFolderPathW)(HWND, int, HANDLE, DWORD, LPWSTR);
typedef BOOL (WINAPI *PFN_GetUserNameW)(LPWSTR, LPDWORD);
typedef BOOL (WINAPI *PFN_GetUserNameA)(LPSTR, LPDWORD);
typedef BOOL (WINAPI *PFN_GetUserProfileDirectoryW)(HANDLE, LPWSTR, LPDWORD);
typedef BOOL (WINAPI *PFN_GetUserProfileDirectoryA)(HANDLE, LPSTR, LPDWORD);

static PFN_SHGetKnownFolderPath        p_SHGetKnownFolderPath;
static PFN_SHGetFolderPathW            p_SHGetFolderPathW;
static PFN_GetUserNameW                p_GetUserNameW;
static PFN_GetUserNameA                p_GetUserNameA;
static PFN_GetUserProfileDirectoryW    p_GetUserProfileDirectoryW;
static PFN_GetUserProfileDirectoryA    p_GetUserProfileDirectoryA;

// FOLDERID_RoamingAppData
static const GUID kRoaming  = { 0x3EB685DB, 0x65F9, 0x4CF6, { 0xA0, 0x3A, 0xE3, 0xEF, 0x65, 0x72, 0x9F, 0x3D } };
// FOLDERID_LocalAppData
static const GUID kLocal    = { 0xF1B32785, 0x6FBA, 0x4FCF, { 0x9D, 0x55, 0x7B, 0x8E, 0x7F, 0x15, 0x70, 0x91 } };
// FOLDERID_LocalAppDataLow
static const GUID kLocalLow = { 0xA520A1A4, 0x1780, 0x4FF6, { 0xBD, 0x18, 0x16, 0x73, 0x43, 0xC5, 0xAF, 0x16 } };
// FOLDERID_Profile
static const GUID kProfile  = { 0x5E6C858F, 0x0E22, 0x4760, { 0x9A, 0xFE, 0xEA, 0x33, 0x17, 0xB6, 0x71, 0x73 } };

static const wchar_t *MapKnownFolder(const GUID *id)
{
    if (!id) return NULL;
    if (memcmp(id, &kRoaming, sizeof(GUID)) == 0)  return g_roaming[0]   ? g_roaming   : NULL;
    if (memcmp(id, &kLocal, sizeof(GUID)) == 0)    return g_local[0]     ? g_local     : NULL;
    if (memcmp(id, &kLocalLow, sizeof(GUID)) == 0) return g_localLow[0]  ? g_localLow  : NULL;
    if (memcmp(id, &kProfile, sizeof(GUID)) == 0)  return g_cloneHome[0] ? g_cloneHome : NULL;
    return NULL;
}

// SHGetKnownFolderPath 的返回内存必须由 CoTaskMemAlloc 分配（调用方用 CoTaskMemFree 释放）
static HRESULT CopyTaskMem(const wchar_t *src, PWSTR *out)
{
    size_t bytes = (wcslen(src) + 1) * sizeof(wchar_t);
    PWSTR p = (PWSTR)CoTaskMemAlloc(bytes);
    if (!p) return E_OUTOFMEMORY;
    memcpy(p, src, bytes);
    *out = p;
    return S_OK;
}

HRESULT WINAPI Hook_SHGetKnownFolderPath(const GUID *rfid, DWORD flags, HANDLE token, PWSTR *ppszPath)
{
    if (ppszPath)
    {
        const wchar_t *mapped = MapKnownFolder(rfid);
        if (mapped) return CopyTaskMem(mapped, ppszPath);
    }
    return p_SHGetKnownFolderPath(rfid, flags, token, ppszPath);
}

HRESULT WINAPI Hook_SHGetFolderPathW(HWND hwnd, int csidl, HANDLE token, DWORD flags, LPWSTR path)
{
    if (path)
    {
        int base = csidl & 0xFF;
        const wchar_t *mapped = NULL;
        if (base == 0x1A) mapped = g_roaming[0]   ? g_roaming   : NULL;   // CSIDL_APPDATA
        if (base == 0x1C) mapped = g_local[0]     ? g_local     : NULL;   // CSIDL_LOCAL_APPDATA
        if (base == 0x28) mapped = g_cloneHome[0] ? g_cloneHome : NULL;   // CSIDL_PROFILE
        if (mapped)
        {
            if (wcslen(mapped) >= MAX_PATH) return E_FAIL;
            wcscpy_s(path, MAX_PATH, mapped);
            return S_OK;
        }
    }
    return p_SHGetFolderPathW(hwnd, csidl, token, flags, path);
}

BOOL WINAPI Hook_GetUserNameW(LPWSTR buf, LPDWORD size)
{
    if (g_userName[0] && buf && size)
    {
        DWORD need = (DWORD)wcslen(g_userName) + 1;
        if (*size < need) { *size = need; SetLastError(ERROR_MORE_DATA); return FALSE; }
        wcscpy_s(buf, *size, g_userName);
        *size = need - 1;
        return TRUE;
    }
    return p_GetUserNameW(buf, size);
}

BOOL WINAPI Hook_GetUserNameA(LPSTR buf, LPDWORD size)
{
    if (g_userName[0] && buf && size)
    {
        char ansi[128];
        int n = WideCharToMultiByte(CP_ACP, 0, g_userName, -1, ansi, 128, NULL, NULL);
        if (n <= 0) return p_GetUserNameA(buf, size);
        DWORD need = (DWORD)n;
        if (*size < need) { *size = need; SetLastError(ERROR_MORE_DATA); return FALSE; }
        memcpy(buf, ansi, need);
        *size = need - 1;
        return TRUE;
    }
    return p_GetUserNameA(buf, size);
}

BOOL WINAPI Hook_GetUserProfileDirectoryW(HANDLE token, LPWSTR dir, LPDWORD size)
{
    if (g_cloneHome[0] && dir && size)
    {
        DWORD need = (DWORD)wcslen(g_cloneHome);
        if (*size <= need) { *size = need + 1; SetLastError(ERROR_INSUFFICIENT_BUFFER); return FALSE; }
        wcscpy_s(dir, *size, g_cloneHome);
        *size = need;
        return TRUE;
    }
    return p_GetUserProfileDirectoryW(token, dir, size);
}

BOOL WINAPI Hook_GetUserProfileDirectoryA(HANDLE token, LPSTR dir, LPDWORD size)
{
    if (g_cloneHome[0] && dir && size)
    {
        char ansi[520];
        int n = WideCharToMultiByte(CP_ACP, 0, g_cloneHome, -1, ansi, 520, NULL, NULL);
        if (n <= 0) return p_GetUserProfileDirectoryA(token, dir, size);
        DWORD need = (DWORD)(n - 1);
        if (*size <= need) { *size = need + 1; SetLastError(ERROR_INSUFFICIENT_BUFFER); return FALSE; }
        memcpy(dir, ansi, (size_t)n);
        *size = need;
        return TRUE;
    }
    return p_GetUserProfileDirectoryA(token, dir, size);
}

// ---- 延迟加载模块：LoadLibrary 后重新打补丁 ----

typedef HMODULE (WINAPI *PFN_LoadLibraryW)(LPCWSTR);
typedef HMODULE (WINAPI *PFN_LoadLibraryA)(LPCSTR);
typedef HMODULE (WINAPI *PFN_LoadLibraryExW)(LPCWSTR, HANDLE, DWORD);
typedef HMODULE (WINAPI *PFN_LoadLibraryExA)(LPCSTR, HANDLE, DWORD);

static PFN_LoadLibraryW   p_LoadLibraryW;
static PFN_LoadLibraryA   p_LoadLibraryA;
static PFN_LoadLibraryExW p_LoadLibraryExW;
static PFN_LoadLibraryExA p_LoadLibraryExA;

HMODULE WINAPI Hook_LoadLibraryW(LPCWSTR file)
{
    HMODULE m = p_LoadLibraryW(file);
    if (m) PatchModuleIat(m);
    return m;
}
HMODULE WINAPI Hook_LoadLibraryA(LPCSTR file)
{
    HMODULE m = p_LoadLibraryA(file);
    if (m) PatchModuleIat(m);
    return m;
}
HMODULE WINAPI Hook_LoadLibraryExW(LPCWSTR file, HANDLE f, DWORD flags)
{
    HMODULE m = p_LoadLibraryExW(file, f, flags);
    if (m) PatchModuleIat(m);
    return m;
}
HMODULE WINAPI Hook_LoadLibraryExA(LPCSTR file, HANDLE f, DWORD flags)
{
    HMODULE m = p_LoadLibraryExA(file, f, flags);
    if (m) PatchModuleIat(m);
    return m;
}

// ---------------------------------------------------------------------------
// Hook 安装
// ---------------------------------------------------------------------------

static void InstallHooks(void)
{
    HMODULE k32  = GetModuleHandleW(L"kernel32.dll");
    HMODULE kb   = GetModuleHandleW(L"kernelbase.dll");
    HMODULE adv  = GetModuleHandleW(L"advapi32.dll");
    HMODULE rpc  = GetModuleHandleW(L"rpcrt4.dll");
    DbgLog("k32=%p kb=%p adv=%p rpc=%p\r\n", (void *)k32, (void *)kb, (void *)adv, (void *)rpc);

    // 多开解锁（需要 FENSHEN_MUTEX_TAG）
    if (g_tag[0])
    {
        AddHook("CreateMutexW",     (void *)Hook_CreateMutexW,     (void **)&p_CreateMutexW,     k32, kb, NULL);
        AddHook("CreateMutexA",     (void *)Hook_CreateMutexA,     (void **)&p_CreateMutexA,     k32, kb, NULL);
        AddHook("CreateMutexExW",   (void *)Hook_CreateMutexExW,   (void **)&p_CreateMutexExW,   k32, kb, NULL);
        AddHook("CreateMutexExA",   (void *)Hook_CreateMutexExA,   (void **)&p_CreateMutexExA,   k32, kb, NULL);
        AddHook("OpenMutexW",       (void *)Hook_OpenMutexW,       (void **)&p_OpenMutexW,       k32, kb, NULL);
        AddHook("OpenMutexA",       (void *)Hook_OpenMutexA,       (void **)&p_OpenMutexA,       k32, kb, NULL);
        AddHook("CreateSemaphoreW", (void *)Hook_CreateSemaphoreW, (void **)&p_CreateSemaphoreW, k32, kb, NULL);
        AddHook("CreateSemaphoreA", (void *)Hook_CreateSemaphoreA, (void **)&p_CreateSemaphoreA, k32, kb, NULL);
    }

    // 设备分身（需要 FENSHEN_DEVICE_FAKE=1）
    if (g_deviceFake)
    {
        AddHook("RegOpenKeyExW",          (void *)Hook_RegOpenKeyExW,          (void **)&p_RegOpenKeyExW,          kb, adv, NULL);
        AddHook("RegOpenKeyExA",          (void *)Hook_RegOpenKeyExA,          (void **)&p_RegOpenKeyExA,          kb, adv, NULL);
        AddHook("RegCreateKeyExW",        (void *)Hook_RegCreateKeyExW,        (void **)&p_RegCreateKeyExW,        kb, adv, NULL);
        AddHook("RegCreateKeyExA",        (void *)Hook_RegCreateKeyExA,        (void **)&p_RegCreateKeyExA,        kb, adv, NULL);
        AddHook("RegGetValueW",           (void *)Hook_RegGetValueW,           (void **)&p_RegGetValueW,           kb, adv, NULL);
        AddHook("RegGetValueA",           (void *)Hook_RegGetValueA,           (void **)&p_RegGetValueA,           kb, adv, NULL);
        AddHook("GetComputerNameW",       (void *)Hook_GetComputerNameW,       (void **)&p_GetComputerNameW,       k32, kb, NULL);
        AddHook("GetComputerNameA",       (void *)Hook_GetComputerNameA,       (void **)&p_GetComputerNameA,       k32, kb, NULL);
        AddHook("GetComputerNameExW",     (void *)Hook_GetComputerNameExW,     (void **)&p_GetComputerNameExW,     k32, kb, NULL);
        AddHook("GetComputerNameExA",     (void *)Hook_GetComputerNameExA,     (void **)&p_GetComputerNameExA,     k32, kb, NULL);
        AddHook("GetVolumeInformationW",  (void *)Hook_GetVolumeInformationW,  (void **)&p_GetVolumeInformationW,  k32, kb, NULL);
        AddHook("GetVolumeInformationA",  (void *)Hook_GetVolumeInformationA,  (void **)&p_GetVolumeInformationA,  k32, kb, NULL);
        AddHook("UuidCreateSequential",   (void *)Hook_UuidCreateSequential,   (void **)&p_UuidCreateSequential,   rpc, NULL, NULL);

        // 网卡 MAC：iphlpapi 可能在进程启动后才被加载，这里先加载再登记
        DeriveMac();
        HMODULE ip = LoadLibraryW(L"iphlpapi.dll");
        if (ip)
        {
            AddHook("GetAdaptersInfo",      (void *)Hook_GetAdaptersInfo,
                    (void **)&p_GetAdaptersInfo,      ip, NULL, NULL);
            AddHook("GetAdaptersAddresses", (void *)Hook_GetAdaptersAddresses,
                    (void **)&p_GetAdaptersAddresses, ip, NULL, NULL);
        }
    }

    // 文件隔离：已知文件夹 + 用户名（深度隔离即生效，不依赖设备分身）
    // 说明：Electron / Node 的 app.getPath('appData')、os.userInfo() 直接问系统 API，
    //       不读 APPDATA / USERPROFILE 环境变量，必须在这里重定向才算真正隔离。
    if (g_cloneHome[0])
    {
        // 保持模块加载：原函数指针必须在整个进程生命周期内有效（提前 FreeLibrary 会让 Hook 调到已卸载地址）
        HMODULE sh = LoadLibraryW(L"shell32.dll");
        HMODULE ue = LoadLibraryW(L"userenv.dll");
        if (sh)
        {
            AddHook("SHGetKnownFolderPath", (void *)Hook_SHGetKnownFolderPath,
                    (void **)&p_SHGetKnownFolderPath, sh, NULL, NULL);
            AddHook("SHGetFolderPathW", (void *)Hook_SHGetFolderPathW,
                    (void **)&p_SHGetFolderPathW, sh, NULL, NULL);
        }
        if (ue)
        {
            AddHook("GetUserProfileDirectoryW", (void *)Hook_GetUserProfileDirectoryW,
                    (void **)&p_GetUserProfileDirectoryW, ue, NULL, NULL);
            AddHook("GetUserProfileDirectoryA", (void *)Hook_GetUserProfileDirectoryA,
                    (void **)&p_GetUserProfileDirectoryA, ue, NULL, NULL);
        }
        AddHook("GetUserNameW", (void *)Hook_GetUserNameW, (void **)&p_GetUserNameW, adv, NULL, NULL);
        AddHook("GetUserNameA", (void *)Hook_GetUserNameA, (void **)&p_GetUserNameA, adv, NULL, NULL);
    }

    // LoadLibrary 重打补丁：只要装了任何 Hook 就需要（覆盖延迟加载模块）
    if (g_hookCount > 0)
    {
        AddHook("LoadLibraryW",   (void *)Hook_LoadLibraryW,   (void **)&p_LoadLibraryW,   k32, kb, NULL);
        AddHook("LoadLibraryA",   (void *)Hook_LoadLibraryA,   (void **)&p_LoadLibraryA,   k32, kb, NULL);
        AddHook("LoadLibraryExW", (void *)Hook_LoadLibraryExW, (void **)&p_LoadLibraryExW, k32, kb, NULL);
        AddHook("LoadLibraryExA", (void *)Hook_LoadLibraryExA, (void **)&p_LoadLibraryExA, k32, kb, NULL);
    }

    DbgLog("hooks added=%d\r\n", g_hookCount);
    if (g_hookCount > 0)
        HookAllLoadedModules();
}

// ---------------------------------------------------------------------------
// 入口
// ---------------------------------------------------------------------------

// 任务栏分组（FENSHEN_TASKBAR_GROUP）：
// SetCurrentProcessExplicitAppUserModelID 必须由目标进程自己调用，.lnk 上的设置只影响该快捷方式本身。
// 放到独立线程里执行 —— DllMain 处于加载器锁内，直接 LoadLibrary 有死锁风险。
static DWORD WINAPI TaskbarGroupThread(LPVOID unused)
{
    (void)unused;
    typedef HRESULT (WINAPI *SetIdFn)(PCWSTR);
    Sleep(20);
    HMODULE shell = LoadLibraryW(L"shell32.dll");
    if (!shell) return 0;
    SetIdFn set = (SetIdFn)(void *)GetProcAddress(shell, "SetCurrentProcessExplicitAppUserModelID");
    if (set) set(g_taskbarGroup);
    FreeLibrary(shell);
    return 0;
}

BOOL WINAPI DllMain(HINSTANCE hInst, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH)
    {
        DbgLog("attach begin pid=%lu\r\n", GetCurrentProcessId());
        g_self = hInst;
        DisableThreadLibraryCalls(hInst);
        LoadEnv();
        DoRegistryRedirect();
        DbgLog("reg-redirect root=%p device=%d name=%ws tag=%ws\r\n",
               (void *)g_hRedirectedRoot, (int)g_deviceFake, g_devName, g_tag);
        InstallHooks();
        // 分组失败静默：任务栏行为随 Windows 版本差异很大，绝不能影响分身本身
        if (g_taskbarGroup[0])
        {
            HANDLE th = CreateThread(NULL, 0, TaskbarGroupThread, NULL, 0, NULL);
            if (th) CloseHandle(th);
        }
        DbgLog("attach done hooks=%d\r\n", g_hookCount);
    }
    else if (reason == DLL_PROCESS_DETACH)
    {
        if (g_hRedirectedRoot)
            RegCloseKey(g_hRedirectedRoot);
    }
    return TRUE;
}
