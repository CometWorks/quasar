using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CometWorks.Diagnostics;

namespace Quasar.Diagnostics;

/// <summary>Captures a launched Windows process at its unhandled exception, without installing OS-wide WER policy.</summary>
internal static class WindowsCrashMonitor
{
    private const uint ExceptionEvent = 1, ExitEvent = 5, Continue = 0x00010002, NotHandled = 0x80010001;
    private const uint Breakpoint = 0x80000003;
    private static readonly SemaphoreSlim DumpGate = new(1, 1); // DbgHelp is single-threaded.

    internal static void Start(string directory, int pid, CancellationToken consent)
    {
        if (!OperatingSystem.IsWindows() || pid == Environment.ProcessId) return;
        var thread = new Thread(() => Monitor(directory, pid, consent)) { IsBackground = true, Name = "Native crash capture" };
        thread.Start();
    }

    private static void Monitor(string directory, int pid, CancellationToken consent)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) { Unavailable(directory, "Windows native capture requires an x64 Host."); return; }
        bool attached = false, initialBreakpoint = true;
        IntPtr debugEvent = Marshal.AllocHGlobal(176); // DEBUG_EVENT, 64-bit Windows ABI.
        try
        {
            consent.ThrowIfCancellationRequested();
            if (!DebugActiveProcess((uint)pid)) throw new Win32Exception(Marshal.GetLastWin32Error());
            attached = true;
            if (!DebugSetProcessKillOnExit(false)) throw new Win32Exception(Marshal.GetLastWin32Error());
            while (!consent.IsCancellationRequested)
            {
                if (!WaitForDebugEventEx(debugEvent, 250))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 121) continue; // ERROR_SEM_TIMEOUT.
                    throw new Win32Exception(error);
                }
                uint kind = unchecked((uint)Marshal.ReadInt32(debugEvent));
                uint processId = unchecked((uint)Marshal.ReadInt32(debugEvent, 4));
                uint threadId = unchecked((uint)Marshal.ReadInt32(debugEvent, 8));
                uint disposition = Continue;
                try
                {
                    if (kind == ExceptionEvent)
                    {
                        uint code = unchecked((uint)Marshal.ReadInt32(debugEvent, 16));
                        bool firstChance = Marshal.ReadInt32(debugEvent, 168) != 0;
                        // Attaching injects one breakpoint. All application exceptions retain normal dispatch.
                        if (initialBreakpoint && firstChance && code == Breakpoint) initialBreakpoint = false;
                        else
                        {
                            disposition = NotHandled;
                            if (!firstChance && !consent.IsCancellationRequested)
                            {
                                DiagnosticCollector.ReportNativeException(directory, code, threadId,
                                    unchecked((ulong)Marshal.ReadInt64(debugEvent, 32)));
                                WriteDump(directory, processId, threadId, debugEvent, consent);
                            }
                        }
                    }
                    // Debugging supplies file handles which the debugger must release.
                    if (kind is 3 or 6 && Marshal.ReadIntPtr(debugEvent, 16) is var file && file != IntPtr.Zero)
                        CloseHandle(file);
                }
                finally
                {
                    if (!ContinueDebugEvent(processId, threadId, disposition)) throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                if (kind == ExitEvent) { attached = false; break; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or InvalidDataException)
        { Unavailable(directory, "Windows native capture failed (" + error.GetType().Name + "). Another debugger or process permissions may prevent attachment."); }
        finally
        {
            if (attached && !DebugActiveProcessStop((uint)pid))
                Unavailable(directory, "Windows debugger detach failed; inspect the child process before restarting capture.");
            Marshal.FreeHGlobal(debugEvent);
        }
    }

    private static void WriteDump(string directory, uint pid, uint threadId, IntPtr debugEvent, CancellationToken consent)
    {
        string temporary = Path.Combine(directory, "windows-core.tmp");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(consent);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        bool acquired = false;
        try
        {
            DumpGate.Wait(deadline.Token);
            acquired = true;
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (DiagnosticStorage.AvailableFreeSpace(directory) < DiagnosticsProtocol.MaxArchiveBytes + (1L << 30))
                    throw new IOException("Insufficient dump reserve.");
                IntPtr process = OpenProcess(0x0400 | 0x0010, false, pid); // Query information and read memory only.
                if (process == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    if (!IsWow64Process(process, out bool wow64)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (wow64) throw new InvalidDataException("32-bit Windows targets require a compatible debugger.");
                    using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    using var exception = new ExceptionContext(threadId, debugEvent);
                    bool bounded = false;
                    Callback callback = (_, input, result) =>
                    {
                        if (Marshal.PtrToStructure<CallbackInputHeader>(input).CallbackType == 6) // CancelCallback.
                        {
                            try { bounded |= output.Length >= DiagnosticsProtocol.MaxArchiveBytes; }
                            catch (IOException) { bounded = true; }
                            Marshal.WriteInt32(result, 0, 1);
                            Marshal.WriteInt32(result, 4, deadline.IsCancellationRequested || bounded ? 1 : 0);
                        }
                        return true;
                    };
                    var callbacks = new CallbackInfo { Routine = Marshal.GetFunctionPointerForDelegate(callback) };
                    // Full memory + unloaded modules + memory/thread information. No machine-wide configuration.
                    bool written = MiniDumpWriteDump(process, pid, output.SafeFileHandle.DangerousGetHandle(),
                        0x00000002 | 0x00000020 | 0x00000800 | 0x00001000, exception.Information, IntPtr.Zero, ref callbacks);
                    GC.KeepAlive(callback);
                    deadline.Token.ThrowIfCancellationRequested();
                    if (bounded || output.Length >= DiagnosticsProtocol.MaxArchiveBytes) throw new InvalidDataException("Dump exceeds size limit.");
                    if (!written) throw new Win32Exception(Marshal.GetLastWin32Error());
                    output.Flush(true);
                }
                finally { CloseHandle(process); }
                File.Move(temporary, Path.Combine(directory, "native.dmp"), true);
            }
        }
        catch (OperationCanceledException) when (!consent.IsCancellationRequested)
        { Unavailable(directory, "Windows dump capture timed out."); }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException or InvalidDataException)
        { Unavailable(directory, "Windows dump unavailable (" + error.GetType().Name + ")."); }
        finally
        {
            if (acquired) DumpGate.Release();
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // DbgHelp receives local pointers while the faulting thread is suspended by the debug event.
    private sealed class ExceptionContext : IDisposable
    {
        public IntPtr Information { get; }
        private readonly IntPtr memory;
        public ExceptionContext(uint threadId, IntPtr debugEvent)
        {
            memory = Marshal.AllocHGlobal(1472);
            try
            {
                IntPtr context = new((memory.ToInt64() + 15) & ~15L); // AMD64 CONTEXT must be aligned to 16 bytes.
                Marshal.Copy(new byte[1232], 0, context, 1232);
                Marshal.WriteInt32(context, 48, 0x0010001f);
                IntPtr thread = OpenThread(0x0008, false, threadId);
                if (thread == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try { if (!GetThreadContext(thread, context)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
                finally { CloseHandle(thread); }
                IntPtr record = IntPtr.Add(context, 1232);
                byte[] bytes = new byte[152]; Marshal.Copy(IntPtr.Add(debugEvent, 16), bytes, 0, bytes.Length);
                Marshal.Copy(bytes, 0, record, bytes.Length);
                Marshal.WriteIntPtr(record, 8, IntPtr.Zero); // A nested target-process record is not a local pointer.
                IntPtr pointers = IntPtr.Add(record, 152);
                Marshal.WriteIntPtr(pointers, record); Marshal.WriteIntPtr(pointers, 8, context);
                Information = IntPtr.Add(pointers, 16);
                Marshal.StructureToPtr(new ExceptionInformation { ThreadId = threadId, Pointers = pointers, ClientPointers = false }, Information, false);
            }
            catch { Marshal.FreeHGlobal(memory); throw; }
        }
        public void Dispose() => Marshal.FreeHGlobal(memory);
    }

    private static void Unavailable(string directory, string reason)
    { try { File.WriteAllText(Path.Combine(directory, "core-unavailable"), reason); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)] private delegate bool Callback(IntPtr parameter, IntPtr input, IntPtr output);
    // minidumpapiset.h applies pshpack4.h even on x64; EXCEPTION_POINTERS/CONTEXT retain their own ABI.
    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct CallbackInfo { public IntPtr Routine; public IntPtr Parameter; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] internal struct CallbackInputHeader
    { public uint ProcessId; public IntPtr ProcessHandle; public uint CallbackType; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] internal struct ExceptionInformation
    { public uint ThreadId; public IntPtr Pointers; [MarshalAs(UnmanagedType.Bool)] public bool ClientPointers; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DebugActiveProcess(uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DebugActiveProcessStop(uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DebugSetProcessKillOnExit([MarshalAs(UnmanagedType.Bool)] bool kill);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WaitForDebugEventEx(IntPtr debugEvent, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ContinueDebugEvent(uint processId, uint threadId, uint status);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint threadId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetThreadContext(IntPtr thread, IntPtr context);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWow64Process(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool wow64);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("dbghelp.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool MiniDumpWriteDump(IntPtr process, uint pid, IntPtr file, uint type,
        IntPtr exception, IntPtr streams, ref CallbackInfo callback);
}
