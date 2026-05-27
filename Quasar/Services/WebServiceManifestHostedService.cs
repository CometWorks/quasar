using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Magnetar.Protocol.Discovery;
using Magnetar.Protocol.Runtime;

namespace Quasar.Services;

public sealed class WebServiceManifestHostedService : IHostedService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly WebServiceOptions _options;
    private readonly WebServiceState _state;
    private readonly IServer _server;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<WebServiceManifestHostedService> _logger;

    public WebServiceManifestHostedService(
        WebServiceOptions options,
        WebServiceState state,
        IServer server,
        IHostApplicationLifetime lifetime,
        ILogger<WebServiceManifestHostedService> logger)
    {
        _options = options;
        _state = state;
        _server = server;
        _lifetime = lifetime;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.OwnManifest)
            return Task.CompletedTask;

        Directory.CreateDirectory(MagnetarPaths.GetWebServiceDirectory());
        _lifetime.ApplicationStarted.Register(WriteManifest);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_options.OwnManifest)
            return Task.CompletedTask;

        var path = MagnetarPaths.GetWebServiceManifestPath();

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to delete Quasar supervisor manifest at {Path}", path);
        }

        return Task.CompletedTask;
    }

    private void WriteManifest()
    {
        var baseUrl = ResolveBaseUrl();
        _state.CurrentManifest = new WebServiceDiscoveryManifest
        {
            InstanceId = _options.InstanceId,
            NodeId = _options.NodeId,
            MachineName = _options.NodeName,
            ProcessId = Environment.ProcessId,
            BaseUrl = baseUrl,
            StartedAtUtc = DateTimeOffset.UtcNow,
        };

        var path = MagnetarPaths.GetWebServiceManifestPath();
        File.WriteAllText(path, JsonSerializer.Serialize(_state.CurrentManifest, JsonOptions));
        _logger.LogInformation("Wrote Quasar supervisor manifest to {Path}", path);
        WriteActiveReleasePointer();

        if (!_options.IsServiceMode)
        {
            Console.WriteLine(WebServiceOptions.SupervisorName);
            Console.WriteLine(_state.CurrentManifest.BaseUrl);
        }

        if (BrowserLauncher.ShouldOpenBrowser(_options))
            BrowserLauncher.TryOpen(_state.CurrentManifest.BaseUrl);
    }

    private string ResolveBaseUrl()
    {
        var feature = _server.Features.Get<IServerAddressesFeature>();
        var address = feature?.Addresses.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(address) && Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            var host = uri.Host switch
            {
                "0.0.0.0" => "127.0.0.1",
                "[::]" => "127.0.0.1",
                "*" => "127.0.0.1",
                "+" => "127.0.0.1",
                _ => uri.Host,
            };

            return $"{uri.Scheme}://{host}:{uri.Port}";
        }

        return _options.BaseUrl;
    }

    private void WriteActiveReleasePointer()
    {
        var path = MagnetarPaths.GetQuasarActiveReleasePath();
        var releasePointer = BuildActiveReleasePointer();
        if (releasePointer is null)
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(releasePointer, JsonOptions));
        _logger.LogInformation("Wrote Quasar active release pointer to {Path}", path);
    }

    private QuasarActiveReleasePointer? BuildActiveReleasePointer()
    {
        var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
        var processPath = Environment.ProcessPath;

        if (string.IsNullOrWhiteSpace(processPath) && string.IsNullOrWhiteSpace(entryAssemblyPath))
            return null;

        if (!string.IsNullOrWhiteSpace(entryAssemblyPath) &&
            entryAssemblyPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(processPath) || IsDotNetHost(processPath)))
        {
            return new QuasarActiveReleasePointer
            {
                Version = _options.Version,
                FileName = string.IsNullOrWhiteSpace(processPath) ? "dotnet" : processPath,
                Arguments = $"\"{entryAssemblyPath}\"",
                WorkingDirectory = AppContext.BaseDirectory,
                ActivatedAtUtc = DateTimeOffset.UtcNow,
            };
        }

        return new QuasarActiveReleasePointer
        {
            Version = _options.Version,
            FileName = processPath ?? entryAssemblyPath ?? string.Empty,
            Arguments = string.Empty,
            WorkingDirectory = AppContext.BaseDirectory,
            ActivatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private static bool IsDotNetHost(string processPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(processPath);
        return string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase);
    }
}
