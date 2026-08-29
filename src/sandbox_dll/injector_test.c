// 独立注入器测试工具：定位注入链路问题
// 用法: injector_test.exe <目标exe> <dll路径> [目标参数]
#include <windows.h>
#include <stdio.h>
#include <tchar.h>

int main(int argc, char **argv)
{
    if (argc < 3) { printf("usage: injector_test <exe> <dll> [args]\n"); return 2; }

    STARTUPINFOW si = { sizeof(si) };
    PROCESS_INFORMATION pi;
    wchar_t cmd[2048], exe[1024], dll[1024];
    MultiByteToWideChar(CP_ACP, 0, argv[1], -1, exe, 1024);
    MultiByteToWideChar(CP_ACP, 0, argv[2], -1, dll, 1024);
    _snwprintf_s(cmd, 2048, _TRUNCATE, L"\"%s\" %hs", exe, argc > 3 ? argv[3] : "");

    if (!CreateProcessW(exe, cmd, NULL, NULL, FALSE,
                        CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT, NULL, NULL, &si, &pi))
    {
        printf("CreateProcess failed gle=%lu\n", GetLastError());
        return 1;
    }
    printf("created pid=%lu tid=%lu\n", pi.dwProcessId, pi.dwThreadId);

    BOOL wow64 = FALSE;
    IsWow64Process(pi.hProcess, &wow64);
    printf("wow64=%d\n", wow64);

    SIZE_T pathBytes = (wcslen(dll) + 1) * sizeof(wchar_t);
    LPVOID remote = VirtualAllocEx(pi.hProcess, NULL, pathBytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote) { printf("VirtualAllocEx failed gle=%lu\n", GetLastError()); ResumeThread(pi.hThread); return 1; }
    if (!WriteProcessMemory(pi.hProcess, remote, dll, pathBytes, NULL))
    { printf("WriteProcessMemory failed gle=%lu\n", GetLastError()); ResumeThread(pi.hThread); return 1; }

    HMODULE k32 = GetModuleHandleW(L"kernel32.dll");
    FARPROC loadLib = GetProcAddress(k32, "LoadLibraryW");
    printf("LoadLibraryW @ %p\n", (void *)loadLib);

    HANDLE th = CreateRemoteThread(pi.hProcess, NULL, 0, (LPTHREAD_START_ROUTINE)loadLib, remote, 0, NULL);
    if (!th) { printf("CreateRemoteThread failed gle=%lu\n", GetLastError()); ResumeThread(pi.hThread); return 1; }
    printf("remote thread created\n");

    DWORD wr = WaitForSingleObject(th, 10000);
    printf("remote thread wait=%lu ", wr);
    if (wr == WAIT_OBJECT_0)
    {
        DWORD code = 0;
        GetExitCodeThread(th, &code);
        printf("exit=0x%08lX (dll base)\n", code);
    }
    else printf("\n");

    ResumeThread(pi.hThread);
    printf("resumed; waiting target exit...\n");

    wr = WaitForSingleObject(pi.hProcess, 15000);
    if (wr == WAIT_OBJECT_0)
    {
        DWORD ec = 0;
        GetExitCodeProcess(pi.hProcess, &ec);
        printf("target exited code=%lu (0x%08lX)\n", ec, ec);
    }
    else printf("target still running after 15s (probably OK)\n");

    CloseHandle(th);
    CloseHandle(pi.hProcess);
    CloseHandle(pi.hThread);
    return 0;
}
