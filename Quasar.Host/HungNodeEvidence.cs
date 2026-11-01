using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Quasar.Diagnostics;

namespace Quasar.Host;

/// <summary>
/// Keeps why a node hung: a kill leaves nothing to look at, and a hang is hard to reproduce. Thread states come from
/// /proc and stacks from CoreCLR's createdump, which also works on a frozen process. The stacks are method names only,
/// so they need no dump consent; a minidump is kept only under a current dump grant. Bounded in time, and the caller
/// kills the node whatever comes of it.
/// </summary>
internal static class HungNodeEvidence
{
    private static readonly TimeSpan DefaultLimit = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CpuSample = TimeSpan.FromSeconds(1);
    private const long MaxDumpBytes = 2L << 30;
    private const long MaxCrashReportBytes = 64L << 20;
    private const int MaxEvidenceChars = 1 << 20;
    private const int ClockTicksPerSecond = 100; // USER_HZ, fixed by the Linux ABI on x64.

    internal static bool Captured(string directory) =>
        File.Exists(Path.Combine(directory, DiagnosticCollector.HangEvidenceFile));

    /// <summary>Never throws, and completes shortly after the limit even if a tool or the disk is stuck.</summary>
    internal static async Task CaptureAsync(
        string directory,
        int pid,
        bool keepDump,
        string kind,
        string message,
        TimeSpan? limit = null,
        string? tool = null
    )
    {
        TimeSpan bound = limit ?? DefaultLimit;
        try
        {
            await Task.Run(() =>
                    CaptureCoreAsync(directory, pid, keepDump, kind, message, bound, tool)
                )
                .WaitAsync(bound + TimeSpan.FromSeconds(5));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Hang evidence of process {pid} is incomplete ({exception.GetType().Name}): {exception.Message}"
            );
            DiagnosticCollector.ReportFailure(directory, kind, message, exception);
        }
    }

    private static async Task CaptureCoreAsync(
        string directory,
        int pid,
        bool keepDump,
        string kind,
        string message,
        TimeSpan limit,
        string? tool
    )
    {
        using var deadline = new CancellationTokenSource(limit);
        string path = Path.Combine(directory, DiagnosticCollector.HangEvidenceFile);
        string header = $"{message}\nCaptured {DateTimeOffset.UtcNow:O}, process {pid}.\n";
        if (!OperatingSystem.IsLinux())
        {
            Write(path, header + "Thread capture is not available on this OS.\n");
            DiagnosticCollector.ReportFailure(directory, kind, message);
            return;
        }
        header += MachineState(pid);
        Dictionary<int, long> before = ReadThreads(pid).ToDictionary(t => t.Id, t => t.CpuTicks);
        // Which threads spin and which wait is the first thing to know.
        await Task.Delay(CpuSample, deadline.Token);
        Thread[] samples = ReadThreads(pid)
            .Select(t =>
                t with
                {
                    CpuTicks = t.CpuTicks - before.GetValueOrDefault(t.Id, t.CpuTicks),
                }
            )
            .ToArray();
        // Readable evidence exists from here on, whatever the stack capture does.
        Write(path, Render(header, "Stacks: still capturing.", samples, []));

        string dump = Path.Combine(directory, "hang.dmp");
        string report = dump + ".crashreport.json";
        if (keepDump && DiagnosticStorage.AvailableFreeSpace(directory) < MaxDumpBytes + (1L << 30))
            keepDump = false;
        string? refused = await RunCreateDumpAsync(
            tool ?? FindCreateDump(pid),
            pid,
            dump,
            keepDump,
            deadline.Token
        );
        string? fallback = null;
        // Yama's ptrace_scope=1 (the Ubuntu default) refuses a sibling. The runtime can run the tool itself if it still answers.
        if (!File.Exists(report) && !deadline.IsCancellationRequested)
            fallback = await RequestFromRuntimeAsync(pid, dump, keepDump, deadline.Token);
        var stacks = new Dictionary<int, string[]>();
        string status;
        try
        {
            if (File.Exists(report) && new FileInfo(report).Length <= MaxCrashReportBytes)
                stacks = ReadStacks(report);
            status =
                stacks.Count > 0
                    ? "Stacks: from createdump."
                    : $"Stacks: unavailable. createdump: {refused ?? "no report"}; runtime: {fallback ?? "no report"}.";
        }
        catch (Exception exception)
            when (exception
                    is JsonException
                        or IOException
                        or KeyNotFoundException
                        or InvalidOperationException
                        or FormatException
            )
        {
            status = $"Stacks: unreadable crash report ({exception.GetType().Name}).";
        }
        if (File.Exists(dump) && new FileInfo(dump).Length >= MaxDumpBytes)
            File.Delete(dump);
        status += File.Exists(dump) ? " Minidump: hang.dmp." : " Minidump: none.";

        string text = Render(header, status, samples, stacks);
        Write(path, text);
        Console.WriteLine($"{message} Evidence: {path}");
        // The report comes last, so that a collector sealing it finds the finished dump.
        DiagnosticCollector.ReportFailure(directory, kind, message, null, text);
    }

    private sealed record Thread(int Id, string Comm, char State, long CpuTicks, string Wchan);

    private static string Render(
        string header,
        string status,
        Thread[] threads,
        Dictionary<int, string[]> stacks
    )
    {
        var text = new StringBuilder(header).Append(status).Append("\n\n");
        // The report keeps only the head of this text: managed stacks say the most, so they go first.
        foreach (
            Thread thread in threads
                .OrderByDescending(t => stacks.GetValueOrDefault(t.Id)?.Any(IsManaged) == true)
                .ThenBy(t => t.Id)
        )
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"thread {thread.Id} \"{thread.Comm}\" state={thread.State} cpu={thread.CpuTicks / (double)ClockTicksPerSecond:0.00}s/{CpuSample.TotalSeconds:0}s wchan={thread.Wchan}\n"
            );
            foreach (string frame in stacks.GetValueOrDefault(thread.Id) ?? [])
                text.Append("  ").Append(frame).Append('\n');
        }
        return text.Length <= MaxEvidenceChars
            ? text.ToString()
            : text.ToString(0, MaxEvidenceChars);
    }

    private static bool IsManaged(string frame) => !frame.StartsWith('[');

    private static string MachineState(int pid)
    {
        try
        {
            string[] status = File.ReadAllLines($"/proc/{pid}/status");
            string Field(string name) =>
                status
                    .FirstOrDefault(line => line.StartsWith(name + ":", StringComparison.Ordinal))
                    ?[(name.Length + 1)..].Trim()
                ?? "?";
            string available =
                File.ReadLines("/proc/meminfo")
                    .FirstOrDefault(line =>
                        line.StartsWith("MemAvailable:", StringComparison.Ordinal)
                    )
                    ?["MemAvailable:".Length..].Trim()
                ?? "?";
            return $"RSS {Field("VmRSS")}, state {Field("State")}. Machine: load {File.ReadAllText("/proc/loadavg").Trim()}, available memory {available}.\n";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static Thread[] ReadThreads(int pid)
    {
        var threads = new List<Thread>();
        try
        {
            foreach (string task in Directory.GetDirectories($"/proc/{pid}/task"))
            {
                try
                {
                    string stat = File.ReadAllText(Path.Combine(task, "stat"));
                    int open = stat.IndexOf('('),
                        close = stat.LastIndexOf(')');
                    string[] fields = stat[(close + 2)..].Split(' ');
                    threads.Add(
                        new(
                            int.Parse(stat[..(open - 1)], CultureInfo.InvariantCulture),
                            stat[(open + 1)..close],
                            fields[0][0],
                            long.Parse(fields[11], CultureInfo.InvariantCulture)
                                + long.Parse(fields[12], CultureInfo.InvariantCulture),
                            File.ReadAllText(Path.Combine(task, "wchan")).Trim()
                        )
                    );
                }
                catch (Exception exception)
                    when (exception is IOException or UnauthorizedAccessException) { } // The thread just ended.
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { } // The process just ended.
        return threads.ToArray();
    }

    /// <summary>createdump comes with the runtime the node runs on, and has to match it.</summary>
    private static string FindCreateDump(int pid)
    {
        const string runtime = "/libcoreclr.so";
        foreach (string line in File.ReadLines($"/proc/{pid}/maps"))
            if (line.EndsWith(runtime, StringComparison.Ordinal))
                return Path.Combine(line[line.IndexOf('/')..^runtime.Length], "createdump");
        return "createdump";
    }

    /// <summary>Returns why it failed, or null. A bigger dump than the limit is stopped and removed by the caller's size check.</summary>
    private static async Task<string?> RunCreateDumpAsync(
        string tool,
        int pid,
        string dump,
        bool keepDump,
        CancellationToken token
    )
    {
        var start = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (
            string argument in keepDump
                ? new[] { "--normal", "--crashreport" }
                : ["--crashreportonly"]
        )
            start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--name");
        start.ArgumentList.Add(dump);
        start.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));
        Process process;
        try
        {
            process = Process.Start(start)!;
        }
        catch (Exception exception)
            when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return exception.Message;
        }
        using (process)
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            Task<string> error = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                while (!process.HasExited)
                {
                    await Task.Delay(50, token);
                    if (File.Exists(dump) && new FileInfo(dump).Length >= MaxDumpBytes)
                        return "dump over the size limit";
                }
                if (process.ExitCode == 0)
                    return null;
                string said = (await output + await error).Trim();
                return $"exit {process.ExitCode}: {said[Math.Max(0, said.Length - 512)..]}";
            }
            catch (OperationCanceledException)
            {
                return "timed out";
            }
            finally
            {
                // Dropping ptrace leaves the node as it was; it dies next either way.
                if (!process.HasExited)
                    process.Kill(true);
            }
        }
    }

    /// <summary>The diagnostics IPC's GenerateCoreDump2 command. Returns why it failed, or null.</summary>
    private static async Task<string?> RequestFromRuntimeAsync(
        int pid,
        string dump,
        bool keepDump,
        CancellationToken token
    )
    {
        const uint normalDump = 1,
            crashReport = 4,
            crashReportOnly = 8;
        const byte dumpCommandSet = 1,
            generateCoreDump2 = 2,
            serverCommandSet = 0xFF,
            serverOk = 0;
        string? socketPath = Directory
            .EnumerateFiles(Path.GetTempPath(), $"dotnet-diagnostic-{pid}-*-socket")
            .FirstOrDefault();
        if (socketPath is null)
            return "no diagnostics socket";
        try
        {
            using var socket = new Socket(
                AddressFamily.Unix,
                SocketType.Stream,
                ProtocolType.Unspecified
            );
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), token);
            using var payload = new MemoryStream();
            using (var writer = new BinaryWriter(payload, Encoding.Unicode, leaveOpen: true))
            {
                writer.Write(dump.Length + 1);
                writer.Write(Encoding.Unicode.GetBytes(dump + '\0'));
                writer.Write(normalDump);
                writer.Write(keepDump ? crashReport : crashReportOnly);
            }
            using var message = new MemoryStream();
            using (var writer = new BinaryWriter(message, Encoding.ASCII, leaveOpen: true))
            {
                writer.Write(Encoding.ASCII.GetBytes("DOTNET_IPC_V1\0"));
                writer.Write((ushort)(20 + payload.Length));
                writer.Write(dumpCommandSet);
                writer.Write(generateCoreDump2);
                writer.Write((ushort)0);
                writer.Write(payload.ToArray());
            }
            await using var stream = new NetworkStream(socket);
            await stream.WriteAsync(message.ToArray(), token);
            var response = new byte[20];
            int read = await stream.ReadAtLeastAsync(
                response,
                response.Length,
                throwOnEndOfStream: false,
                token
            );
            return
                read == response.Length
                && response[16] == serverCommandSet
                && response[17] == serverOk
                ? null
                : "refused";
        }
        catch (OperationCanceledException)
        {
            return "no answer";
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            return exception.Message;
        }
    }

    private static Dictionary<int, string[]> ReadStacks(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var stacks = new Dictionary<int, string[]>();
        foreach (
            JsonElement thread in document
                .RootElement.GetProperty("payload")
                .GetProperty("threads")
                .EnumerateArray()
        )
        {
            int id = Convert.ToInt32(thread.GetProperty("native_thread_id").GetString(), 16);
            var frames = new List<string>();
            int repeat = 0;
            string? module = null;
            void FlushNative()
            {
                if (module is not null)
                    frames.Add(repeat > 1 ? $"[{module} x{repeat}]" : $"[{module}]");
                module = null;
                repeat = 0;
            }
            if (!thread.TryGetProperty("stack_frames", out JsonElement stack))
                continue;
            foreach (JsonElement frame in stack.EnumerateArray())
            {
                if (
                    frame.TryGetProperty("method_name", out JsonElement method)
                    && method.GetString() is { Length: > 0 } name
                )
                {
                    FlushNative();
                    frames.Add(name);
                    continue;
                }
                // The crash report keeps every address; a run of frames in one native module says nothing more here.
                string current = frame.TryGetProperty("native_module", out JsonElement native)
                    ? native.GetString() ?? "native"
                    : "native";
                if (current != module)
                    FlushNative();
                module = current;
                repeat++;
            }
            FlushNative();
            stacks[id] = frames.ToArray();
        }
        return stacks;
    }

    private static void Write(string path, string text)
    {
        File.WriteAllText(path, text);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
