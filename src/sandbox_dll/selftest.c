// 自检程序：验证分身沙盒的互斥体改名、注册表隔离与设备分身是否生效。
// 用法: selftest.exe <标记> [输出文件路径]
//  1. 创建全局命名互斥体并报告 FIRST/SECOND（多开解锁生效则每个分身都是 FIRST）
//  2. 读取 HKCU\Software\FenShenSelfTest\marker 并写入新标记（注册表隔离生效则不影响真实 HKCU）
//  3. 读取设备指纹（MachineGuid / 计算机名 / 卷序列号）报告 DEV 行（设备分身生效则各分身不同且稳定）
//  4. 运行时 LoadLibrary devprobe.dll 并二次采集（DEV2 行，验证延迟加载模块也被 Hook）
// 结果打印到 stdout；若提供输出文件路径则同时追加写入该文件。
#include <winsock2.h>   // 必须在 windows.h 之前：iphlpapi.h 用到 winsock2 的 SOCKET 类型
#include <windows.h>
#include <iphlpapi.h>
#include <userenv.h>
#include <stdio.h>
#include <string.h>

#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "userenv.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "iphlpapi.lib")

__declspec(dllimport) void WINAPI CoTaskMemFree(LPVOID p);

static FILE *g_out = NULL;

static void Report(const char *line)
{
    printf("%s\n", line);
    fflush(stdout);
    if (g_out) { fprintf(g_out, "%s\n", line); fflush(g_out); }
}

// 文件隔离自检：Electron/Node 的 app.getPath('appData') / os.userInfo() 走这些 API，
// 环境变量改不动它们 —— 这一项是「分身是否共享登录态」的直接证据。
// 必须用静态导入（走 IAT）才能验证 Hook：GetProcAddress 拿到的是原始地址，会绕过 IAT 补丁。
__declspec(dllimport) HRESULT WINAPI SHGetKnownFolderPath(const GUID *, DWORD, HANDLE, PWSTR *);
__declspec(dllimport) HRESULT WINAPI SHGetFolderPathW(HWND, int, HANDLE, DWORD, LPWSTR);

static void ReportKnownFolder(const char *label, const GUID *id)
{
    char line[640];
    PWSTR p = NULL;
    if (SUCCEEDED(SHGetKnownFolderPath(id, 0, NULL, &p)) && p)
    {
        sprintf_s(line, sizeof(line), "ISO: %s=%ls", label, p);
        CoTaskMemFree(p);
    }
    else
    {
        sprintf_s(line, sizeof(line), "ISO: %s=<query-failed>", label);
    }
    Report(line);
}

static void ReportIsolation(void)
{
    char line[640];
    wchar_t user[128] = L"";
    DWORD us = 128;
    if (!GetUserNameW(user, &us)) wcscpy_s(user, 128, L"<failed>");
    sprintf_s(line, sizeof(line), "ISO: user=%ls", user);
    Report(line);

    wchar_t prof[520] = L"";
    DWORD ps = 520;
    HANDLE tok = NULL;
    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &tok))
    {
        if (!GetUserProfileDirectoryW(tok, prof, &ps)) prof[0] = 0;
        CloseHandle(tok);
    }
    sprintf_s(line, sizeof(line), "ISO: profile=%ls", prof[0] ? prof : L"<failed>");
    Report(line);

    // FOLDERID_RoamingAppData / LocalAppData / LocalAppDataLow
    static const GUID k1 = { 0x3EB685DB, 0x65F9, 0x4CF6, { 0xA0,0x3A,0xE3,0xEF,0x65,0x72,0x9F,0x3D } };
    static const GUID k2 = { 0xF1B32785, 0x6FBA, 0x4FCF, { 0x9D,0x55,0x7B,0x8E,0x7F,0x15,0x70,0x91 } };
    static const GUID k3 = { 0xA520A1A4, 0x1780, 0x4FF6, { 0xBD,0x18,0x16,0x73,0x43,0xC5,0xAF,0x16 } };
    ReportKnownFolder("roaming", &k1);
    ReportKnownFolder("local", &k2);
    ReportKnownFolder("locallow", &k3);

    // 老式 Chromium / Electron 走 CSIDL_APPDATA（SHGetFolderPathW）
    wchar_t csidl[MAX_PATH] = L"";
    if (SUCCEEDED(SHGetFolderPathW(NULL, 0x1A, NULL, 0, csidl)))
        sprintf_s(line, sizeof(line), "ISO: csidl_roaming=%ls", csidl);
    else
        strcpy_s(line, sizeof(line), "ISO: csidl_roaming=<query-failed>");
    Report(line);

    // 网卡 MAC（设备分身应返回 02 开头的伪造地址）
    IP_ADAPTER_INFO ai[8];
    ULONG asz = sizeof(ai);
    if (GetAdaptersInfo(ai, &asz) == ERROR_SUCCESS)
    {
        PIP_ADAPTER_INFO a = ai;
        while (a && a->AddressLength < 6 && a->Next) a = a->Next;
        if (a && a->AddressLength >= 6)
            sprintf_s(line, sizeof(line), "ISO: mac=%02X-%02X-%02X-%02X-%02X-%02X",
                      a->Address[0], a->Address[1], a->Address[2],
                      a->Address[3], a->Address[4], a->Address[5]);
        else
            strcpy_s(line, sizeof(line), "ISO: mac=<none>");
    }
    else
    {
        strcpy_s(line, sizeof(line), "ISO: mac=<query-failed>");
    }
    Report(line);
}

static void ReportDevice(const char *prefix)
{
    char guid[128] = "?";
    DWORD sz = sizeof(guid);
    RegGetValueA(HKEY_LOCAL_MACHINE, "SOFTWARE\\Microsoft\\Cryptography",
                 "MachineGuid", RRF_RT_REG_SZ, NULL, guid, &sz);

    char cn[64] = "?";
    DWORD cnsz = sizeof(cn);
    GetComputerNameA(cn, &cnsz);

    DWORD vol = 0;
    GetVolumeInformationA("C:\\", NULL, 0, &vol, NULL, NULL, NULL, 0);

    char line[512];
    sprintf_s(line, sizeof(line), "%s: guid=%s cn=%s vol=%08lX", prefix, guid, cn, vol);
    Report(line);
}

int main(int argc, char **argv)
{
    const char *marker = argc > 1 ? argv[1] : "x";
    if (argc > 2)
        fopen_s(&g_out, argv[2], "a");

    HANDLE m = CreateMutexW(NULL, FALSE, L"Global\\FenShenSelfTest_InstanceMutex");
    if (!m)
    {
        char buf[128];
        sprintf_s(buf, sizeof(buf), "MUTEX: create-failed gle=%lu", GetLastError());
        Report(buf);
        return 1;
    }
    Report(GetLastError() == ERROR_ALREADY_EXISTS ? "MUTEX: SECOND" : "MUTEX: FIRST");

    HKEY h;
    LONG rc = RegCreateKeyExA(HKEY_CURRENT_USER, "Software\\FenShenSelfTest",
                              0, NULL, 0, KEY_READ | KEY_WRITE, NULL, &h, NULL);
    if (rc == ERROR_SUCCESS)
    {
        char prev[128] = "(none)";
        DWORD size = sizeof(prev), type = 0;
        if (RegQueryValueExA(h, "marker", NULL, &type, (BYTE *)prev, &size) != ERROR_SUCCESS)
            strcpy_s(prev, sizeof(prev), "(none)");

        RegSetValueExA(h, "marker", 0, REG_SZ, (const BYTE *)marker,
                       (DWORD)strlen(marker) + 1);

        char now[128] = "(?)";
        size = sizeof(now);
        RegQueryValueExA(h, "marker", NULL, &type, (BYTE *)now, &size);

        char buf[320];
        sprintf_s(buf, sizeof(buf), "REG: prev=%s now=%s", prev, now);
        Report(buf);
        RegCloseKey(h);
    }
    else
    {
        char buf[128];
        sprintf_s(buf, sizeof(buf), "REG: open-failed rc=%ld", rc);
        Report(buf);
    }

    ReportDevice("DEV");
    ReportIsolation();

    // 模拟运行时加载的原生模块（Electron/Node 场景）
    HMODULE hDev = LoadLibraryW(L"devprobe.dll");
    if (!hDev)
    {
        char buf[160];
        sprintf_s(buf, sizeof(buf), "DEV2: devprobe.dll not-found gle=%lu", GetLastError());
        Report(buf);
    }
    else
    {
        typedef int (__stdcall *FN_DevProbe)(const char *);
        FN_DevProbe probe = (FN_DevProbe)GetProcAddress(hDev, "DevProbe");
        if (!probe)
        {
            Report("DEV2: DevProbe export not-found");
        }
        else if (argc > 2)
        {
            // 先关闭本进程的输出句柄，避免与探针模块的追加写冲突
            if (g_out) { fclose(g_out); g_out = NULL; }
            int rc = probe(argv[2]);
            char buf[160];
            sprintf_s(buf, sizeof(buf), "DEV2: probe rc=%d", rc);
            printf("%s\n", buf);
        }
        else
        {
            Report("DEV2: no output path arg");
        }
    }

    if (g_out) fclose(g_out);
    Sleep(3000);
    return 0;
}
