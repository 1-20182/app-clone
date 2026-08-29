// 自检程序：验证分身沙盒的互斥体改名、注册表隔离与设备分身是否生效。
// 用法: selftest.exe <标记> [输出文件路径]
//  1. 创建全局命名互斥体并报告 FIRST/SECOND（多开解锁生效则每个分身都是 FIRST）
//  2. 读取 HKCU\Software\FenShenSelfTest\marker 并写入新标记（注册表隔离生效则不影响真实 HKCU）
//  3. 读取设备指纹（MachineGuid / 计算机名 / 卷序列号）报告 DEV 行（设备分身生效则各分身不同且稳定）
//  4. 运行时 LoadLibrary devprobe.dll 并二次采集（DEV2 行，验证延迟加载模块也被 Hook）
// 结果打印到 stdout；若提供输出文件路径则同时追加写入该文件。
#include <windows.h>
#include <stdio.h>
#include <string.h>

#pragma comment(lib, "advapi32.lib")

static FILE *g_out = NULL;

static void Report(const char *line)
{
    printf("%s\n", line);
    fflush(stdout);
    if (g_out) { fprintf(g_out, "%s\n", line); fflush(g_out); }
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
