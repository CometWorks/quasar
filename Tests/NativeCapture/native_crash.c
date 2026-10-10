#define WIN32_LEAN_AND_MEAN
#include <windows.h>

/* Raise outside the CLR so its SEH-to-managed translation cannot replace the native fault code. */
int main(void)
{
    DWORD started = GetTickCount();
    SetErrorMode(SEM_NOGPFAULTERRORBOX | SEM_FAILCRITICALERRORS);
    while (!IsDebuggerPresent() && GetTickCount() - started < 10000)
        Sleep(50);
    if (!IsDebuggerPresent())
        return 2;
    RaiseException(0xe0421001, EXCEPTION_NONCONTINUABLE, 0, NULL);
    return 3;
}
