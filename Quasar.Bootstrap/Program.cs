using System.Diagnostics;
using System.Text.Json;
using Magnetar.Protocol.Discovery;
using Magnetar.Protocol.Runtime;

namespace Quasar.Bootstrap;

internal static class Program
{
    private const string EnsureRunningCommand = "ensure-running";
    private const string SpawnMutexName = "Quasar.Bootstrap";
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2),
    };

    public static async Task<int> Main(string[] args)
    {
        var quiet = args.Any(static arg => string.Equals(arg, "--quiet", StringComparison.OrdinalIgnoreCase));
        var command = args.FirstOrDefault(arg => !arg.StartsWith("-", StringComparison.Ordinal)) ?? EnsureRunningCommand;

        if (!string.Equals(command, EnsureRunningCommand, StringComparison.OrdinalIgnoreCase))
        {
            if (!quiet)
                Console.Error.WriteLine("Usage: Quasar.Bootstrap ensure-running [--quiet]");

            return 2;
        }

        var existing = await TryGetHealthyServiceUriAsync().ConfigureAwait(false);
        if (existing is not null)
            return Complete(existing, quiet);

        using var spawnMutex = new Mutex(false, SpawnMutexName);
        try
        {
            spawnMutex.WaitOne(TimeSpan.FromSeconds(10));
        }
        catch
        {
        }

        existing = await TryGetHealthyServiceUriAsync().ConfigureAwait(false);
        if (existing is not null)
            return Complete(existing, quiet);

        if (!TryBuildLaunchSpec(out var fileName, out var arguments, out var workingDirectory))
        {
            if (!quiet)
                Console.Error.WriteLine("Quasar.Bootstrap could not locate the Quasar host binary.");

            return 3;
        }

        StartDetachedProcess(fileName, arguments, workingDirectory);

        for (var attempt = 0; attempt < 45; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            existing = await TryGetHealthyServiceUriAsync().ConfigureAwait(false);
            if (existing is not null)
                return Complete(existing, quiet);
        }

        if (!quiet)
            Console.Error.WriteLine("Quasar did not become healthy before timeout.");

        return 4;
    }

    private static int Complete(Uri uri, bool quiet)
    {
        if (!quiet)
        {
            Console.WriteLine(WebServiceOptionsFallback.SupervisorName);
            Console.WriteLine(uri.AbsoluteUri.TrimEnd('/'));
        }

        return 0;
    }

    private static async Task<Uri?> TryGetHealthyServiceUriAsync()
    {
        var manifest = ReadManifest();
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.BaseUrl))
            return null;

        if (!Uri.TryCreate(manifest.BaseUrl, UriKind.Absolute, out var baseUri))
            return null;

        try
        {
            using var response = await HttpClient.GetAsync(new Uri(baseUri, "/api/health")).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? baseUri : null;
        }
        catch
        {
            return null;
        }
    }

    private static WebServiceDiscoveryManifest? ReadManifest()
    {
        try
        {
            var path = MagnetarPaths.GetWebServiceManifestPath();
            if (!File.Exists(path))
                return null;

            return JsonSerializer.Deserialize<WebServiceDiscoveryManifest>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private static bool TryBuildLaunchSpec(out string fileName, out string arguments, out string workingDirectory)
    {
        if (TryGetActiveReleasePointer(out fileName, out arguments, out workingDirectory))
            return true;

        var envExe = FirstExistingFile("QUASAR_WEB_EXE", "MAGNETAR_WEB_EXE");
        if (!string.IsNullOrWhiteSpace(envExe))
        {
            fileName = envExe;
            arguments = string.Empty;
            workingDirectory = Path.GetDirectoryName(envExe) ?? AppContext.BaseDirectory;
            return true;
        }

        var envDll = FirstExistingFile("QUASAR_WEB_DLL", "MAGNETAR_WEB_DLL");
        if (!string.IsNullOrWhiteSpace(envDll))
        {
            fileName = "dotnet";
            arguments = $"\"{envDll}\"";
            workingDirectory = Path.GetDirectoryName(envDll) ?? AppContext.BaseDirectory;
            return true;
        }

        var candidateDll = FindCandidate("Quasar.dll");
        if (!string.IsNullOrWhiteSpace(candidateDll))
        {
            fileName = "dotnet";
            arguments = $"\"{candidateDll}\"";
            workingDirectory = Path.GetDirectoryName(candidateDll) ?? AppContext.BaseDirectory;
            return true;
        }

        var candidateExe = FindCandidate("Quasar.exe");
        if (!string.IsNullOrWhiteSpace(candidateExe))
        {
            fileName = candidateExe;
            arguments = string.Empty;
            workingDirectory = Path.GetDirectoryName(candidateExe) ?? AppContext.BaseDirectory;
            return true;
        }

        fileName = string.Empty;
        arguments = string.Empty;
        workingDirectory = string.Empty;
        return false;
    }

    private static string? FirstExistingFile(params string[] variableNames)
    {
        foreach (var variableName in variableNames)
        {
            var value = Environment.GetEnvironmentVariable(variableName);
            if (!string.IsNullOrWhiteSpace(value) && File.Exists(value))
                return value;
        }

        return null;
    }

    private static bool TryGetActiveReleasePointer(out string fileName, out string arguments, out string workingDirectory)
    {
        try
        {
            var path = MagnetarPaths.GetQuasarActiveReleasePath();
            if (!File.Exists(path))
                return EmptyLaunchSpec(out fileName, out arguments, out workingDirectory);

            var pointer = JsonSerializer.Deserialize<QuasarActiveReleasePointer>(File.ReadAllText(path));
            if (pointer is null || string.IsNullOrWhiteSpace(pointer.FileName))
                return EmptyLaunchSpec(out fileName, out arguments, out workingDirectory);

            var resolvedFileName = pointer.FileName.Trim();
            var isDotNet = string.Equals(resolvedFileName, "dotnet", StringComparison.OrdinalIgnoreCase);
            if (!isDotNet && !File.Exists(resolvedFileName))
                return EmptyLaunchSpec(out fileName, out arguments, out workingDirectory);

            fileName = resolvedFileName;
            arguments = pointer.Arguments ?? string.Empty;
            workingDirectory = !string.IsNullOrWhiteSpace(pointer.WorkingDirectory)
                ? pointer.WorkingDirectory
                : Path.GetDirectoryName(resolvedFileName) ?? AppContext.BaseDirectory;
            return true;
        }
        catch
        {
            return EmptyLaunchSpec(out fileName, out arguments, out workingDirectory);
        }
    }

    private static bool EmptyLaunchSpec(out string fileName, out string arguments, out string workingDirectory)
    {
        fileName = string.Empty;
        arguments = string.Empty;
        workingDirectory = string.Empty;
        return false;
    }

    private static string? FindCandidate(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
        {
            var direct = Path.Combine(directory.FullName, fileName);
            if (File.Exists(direct))
                return direct;

            var projectBin = Path.Combine(directory.FullName, "Quasar", "bin");
            if (Directory.Exists(projectBin))
            {
                var files = Directory.GetFiles(projectBin, fileName, SearchOption.AllDirectories);
                if (files.Length > 0)
                    return files[0];
            }
        }

        return null;
    }

    private static void StartDetachedProcess(string fileName, string arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process.Start(startInfo);
    }

    private static class WebServiceOptionsFallback
    {
        public const string SupervisorName = "Quasar";
    }
}
