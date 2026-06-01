using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Magnetar.Protocol.Discovery;
using Magnetar.Protocol.Runtime;
using Newtonsoft.Json;

namespace Quasar.Agent
{
    public class WebServiceLocator
    {
        private const string BootstrapMutexName = "Quasar.Bootstrap";

        public async Task<Uri> EnsureWebServiceAsync(CancellationToken cancellationToken)
        {
            var uri = await TryGetHealthyServiceUriAsync(cancellationToken).ConfigureAwait(false);
            if (uri != null)
                return uri;

            using (var spawnMutex = new Mutex(false, BootstrapMutexName))
            {
                try
                {
                    spawnMutex.WaitOne(TimeSpan.FromSeconds(10));
                }
                catch
                {
                }

                uri = await TryGetHealthyServiceUriAsync(cancellationToken).ConfigureAwait(false);
                if (uri != null)
                    return uri;

                if (await TryRunBootstrapAsync(cancellationToken).ConfigureAwait(false))
                {
                    uri = await TryGetHealthyServiceUriAsync(cancellationToken).ConfigureAwait(false);
                    if (uri != null)
                        return uri;
                }
                else
                {
                    Log("Quasar supervisor not running and Quasar.Bootstrap was not found or failed.");
                    return null;
                }
            }

            for (var attempt = 0; attempt < 30 && !cancellationToken.IsCancellationRequested; attempt++)
            {
                uri = await TryGetHealthyServiceUriAsync(cancellationToken).ConfigureAwait(false);
                if (uri != null)
                    return uri;

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }

            return null;
        }

        private async Task<bool> TryRunBootstrapAsync(CancellationToken cancellationToken)
        {
            if (!TryBuildBootstrapLaunchSpec(out var fileName, out var arguments, out var workingDirectory))
                return false;

            try
            {
                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments ?? string.Empty,
                    WorkingDirectory = workingDirectory ?? AppContext.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }))
                {
                    if (process == null)
                        return false;

                    Log($"Started Quasar.Bootstrap using '{fileName} {arguments}'");

                    for (var attempt = 0; attempt < 45 && !cancellationToken.IsCancellationRequested; attempt++)
                    {
                        if (process.HasExited)
                            return process.ExitCode == 0;

                        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                    }

                    return process.HasExited && process.ExitCode == 0;
                }
            }
            catch (Exception exception)
            {
                Log($"Quasar.Bootstrap failed: {exception.Message}");
                return false;
            }
        }

        private async Task<Uri> TryGetHealthyServiceUriAsync(CancellationToken cancellationToken)
        {
            var manifest = ReadManifest();
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.BaseUrl))
                return null;

            if (!Uri.TryCreate(manifest.BaseUrl, UriKind.Absolute, out var baseUri))
                return null;

            var healthUri = new Uri(baseUri, "/api/health");
            var request = WebRequest.CreateHttp(healthUri);
            request.Method = "GET";
            request.Timeout = 2000;

            try
            {
                using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                {
                    return response.StatusCode == HttpStatusCode.OK ? baseUri : null;
                }
            }
            catch
            {
                return null;
            }
        }

        private static WebServiceDiscoveryManifest ReadManifest()
        {
            try
            {
                var path = MagnetarPaths.GetWebServiceManifestPath();
                if (!File.Exists(path))
                    return null;

                return JsonConvert.DeserializeObject<WebServiceDiscoveryManifest>(File.ReadAllText(path));
            }
            catch
            {
                return null;
            }
        }

        private static bool TryBuildBootstrapLaunchSpec(out string fileName, out string arguments, out string workingDirectory)
        {
            var envExe = FirstExistingFile("QUASAR_BOOTSTRAP_EXE");
            if (!string.IsNullOrWhiteSpace(envExe) && File.Exists(envExe))
            {
                fileName = envExe;
                arguments = "ensure-running --quiet";
                workingDirectory = Path.GetDirectoryName(envExe) ?? AppContext.BaseDirectory;
                return true;
            }

            var envDll = FirstExistingFile("QUASAR_BOOTSTRAP_DLL");
            if (!string.IsNullOrWhiteSpace(envDll) && File.Exists(envDll))
            {
                fileName = "dotnet";
                arguments = $"\"{envDll}\" ensure-running --quiet";
                workingDirectory = Path.GetDirectoryName(envDll) ?? AppContext.BaseDirectory;
                return true;
            }

            var candidateDll = FindCandidate("Quasar.Bootstrap.dll");
            if (!string.IsNullOrWhiteSpace(candidateDll))
            {
                fileName = "dotnet";
                arguments = $"\"{candidateDll}\" ensure-running --quiet";
                workingDirectory = Path.GetDirectoryName(candidateDll) ?? AppContext.BaseDirectory;
                return true;
            }

            var candidateExe = FindCandidate("Quasar.Bootstrap.exe");
            if (!string.IsNullOrWhiteSpace(candidateExe))
            {
                fileName = candidateExe;
                arguments = "ensure-running --quiet";
                workingDirectory = Path.GetDirectoryName(candidateExe) ?? AppContext.BaseDirectory;
                return true;
            }

            candidateExe = FindCandidate("Quasar.exe") ?? FindCandidate("Quasar");
            if (!string.IsNullOrWhiteSpace(candidateExe))
            {
                fileName = candidateExe;
                arguments = "ensure-running --quiet";
                workingDirectory = Path.GetDirectoryName(candidateExe) ?? AppContext.BaseDirectory;
                return true;
            }

            fileName = null;
            arguments = null;
            workingDirectory = null;
            return false;
        }

        private static string FirstExistingFile(params string[] variableNames)
        {
            foreach (var variableName in variableNames)
            {
                var value = Environment.GetEnvironmentVariable(variableName);
                if (!string.IsNullOrWhiteSpace(value) && File.Exists(value))
                    return value;
            }

            return null;
        }

        private static string FindCandidate(string fileName)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            for (var depth = 0; directory != null && depth < 8; depth++, directory = directory.Parent)
            {
                var direct = Path.Combine(directory.FullName, fileName);
                if (File.Exists(direct))
                    return direct;

                var projectBin = Path.Combine(directory.FullName, "Quasar.Bootstrap", "bin");
                if (Directory.Exists(projectBin))
                {
                    var files = Directory.GetFiles(projectBin, fileName, SearchOption.AllDirectories);
                    if (files.Length > 0)
                        return files[0];
                }
            }

            return null;
        }
        private static void Log(string message)
        {
            Console.WriteLine($"[Quasar.Agent] {message}");
        }
    }
}
