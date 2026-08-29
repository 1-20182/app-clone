// 设备指纹探针（延迟加载测试模块）：
// 由 selftest.exe 在运行时 LoadLibrary 加载，验证“运行时加载的模块同样被沙盒 Hook”。
// 用法: DevProbe <输出文件路径>  —— 追加一行 DEV2 设备指纹。
#include <windows.h>
#include <stdio.h>

#pragma comment(lib, "advapi32.lib")

__declspec(dllexport) int __stdcall DevProbe(const char *outPath)
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

    if (!outPath || !outPath[0]) return 1;
    FILE *f = NULL;
    errno_t e = fopen_s(&f, outPath, "a");
    if (e != 0 || !f) return 100 + e;
    fprintf(f, "DEV2: guid=%s cn=%s vol=%08lX\n", guid, cn, vol);
    fclose(f);
    return 0;
}

BOOL WINAPI DllMain(HINSTANCE h, DWORD reason, LPVOID r)
{
    (void)h; (void)r;
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(h);
    return TRUE;
}
