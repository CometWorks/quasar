using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Auth;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterSetupTests
{
    private static EnrolledClusterHost Host(string id, string address) => new(id, id, address, 18400, "credential");
    private static ClusterSetupRequest Request(params ClusterSetupPlacement[] machines) => new("demo", "Demo", "world", "profile", "one", 28000, machines);
    [Fact]
    public void PlacementSupportsBothLocalAndRemoteButRefusesMixedLoopback()
    {
        Assert.Single(ClusterSetupService.Validate(Request(new ClusterSetupPlacement("one", 2)), [Host("one", "127.0.0.1")]));
        Assert.Equal(2, ClusterSetupService.Validate(Request(new ClusterSetupPlacement("one", 1), new("two", 1)), [Host("one", "10.1.0.1"), Host("two", "10.1.0.2")]).Length);
        Assert.Throws<ArgumentException>(() => ClusterSetupService.Validate(Request(new ClusterSetupPlacement("one", 1), new("two", 1)), [Host("one", "127.0.0.1"), Host("two", "10.1.0.2")]));
        Assert.Throws<ArgumentException>(() => ClusterSetupService.Validate(Request(new ClusterSetupPlacement("one", 1)), [Host("one", "10.1.0.1")]));
        Assert.Throws<ArgumentException>(() => ClusterSetupService.Validate(Request(new ClusterSetupPlacement("one", 2), new("one", 2)), [Host("one", "10.1.0.1")]));
        Assert.Throws<ArgumentException>(() => ClusterSetupService.Validate(Request(new ClusterSetupPlacement("missing", 2)), [Host("one", "10.1.0.1")]));
    }
    [Theory]
    [InlineData("10.1.0.1", true)]
    [InlineData("172.16.1.1", true)]
    [InlineData("172.32.1.1", false)]
    [InlineData("192.168.1.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::", false)]
    [InlineData("8.8.8.8", false)]
    public void ClusterControlAddressesStayOnPrivateNetworks(string address, bool allowed) =>
        Assert.Equal(allowed, ClusterHostCatalog.IsClusterAddress(address));

    [Fact]
    public async Task GuidedPortsAndPerHostRuntimePathsReachTheShippedSpecification()
    {
        var cluster = new ClusterDefinition { UniqueName = "demo", GatewayUrl = "http://10.1.0.1:28016", GatewayAdminTokenEnvironmentVariable = "ADMIN" };
        var request = new ServerToClusterRequest(Guid.NewGuid(), "", "", "1210014", "one", 28000, "JOIN", "TOKENS",
            ["10.1.0.1/32", "10.1.0.2/32"], [new("one", "http://one.quasar-host.invalid:18400", "HOST_ONE", "EXEC_ONE", "10.1.0.1", 1),
                new("two", "http://two.quasar-host.invalid:18400", "HOST_TWO", "EXEC_TWO", "10.1.0.2", 1)], NodePortBase: 28100);
        string world = Path.Combine(Path.GetTempPath(), "cluster-setup-world-" + Guid.NewGuid()); Directory.CreateDirectory(world);
        try
        {
            File.WriteAllText(Path.Combine(world, "Sandbox.sbc"), "world");
            // Test the actual specification builder, not a second copy of its port formula.
            var paths = new Dictionary<string, Quasar.Host.Contract.V1.HostConversionPaths>();
            foreach (string host in new[] { "one", "two" })
                paths[host] = JsonSerializer.Deserialize<Quasar.Host.Contract.V1.HostConversionPaths>(
                    $$"""{"hostId":"{{host}}","directory":"/world","configurationDirectory":"/config","runtimeDirectory":"/runs/{{host}}"}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            using var spec = JsonDocument.Parse(await ClusterConversionService.SpecificationAsync(cluster, new() { DisplayName = "Demo" }, new(), null, request, world, paths, default));
            var nodes = spec.RootElement.GetProperty("nodes").EnumerateArray().ToArray();
            Assert.Equal(3, nodes.Length);
            Assert.Contains(nodes, n => n.GetProperty("role").GetString() == "WA" && n.GetProperty("backend").GetString() == "10.1.0.1:28164");
            Assert.Contains(nodes, n => n.GetProperty("host").GetString() == "two" && n.GetProperty("control").GetString() == "10.1.0.2:28200");
            Assert.Equal("/runs/two", spec.RootElement.GetProperty("hosts")[1].GetProperty("runRoot").GetString());
        }
        finally { Directory.Delete(world, true); }
    }

    [Fact]
    public void BundledAgentGetsTraceableLoaderCompatibleMetadataWithoutChangingItsBytes()
    {
        string config = Path.Combine(Path.GetTempPath(), "cluster-agent-bundle-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(config, "Local"));
        Directory.CreateDirectory(Path.Combine(config, "Profiles"));
        try
        {
            foreach (string name in new[] { "Quasar.Agent.dll", "Magnetar.Protocol.dll", "0Harmony.dll" })
                File.WriteAllText(Path.Combine(config, "Local", name), name);
            File.WriteAllText(Path.Combine(config, "Profiles/Current.xml"), "<Profile><Local><string>Quasar.Agent.dll</string></Local></Profile>");
            ClusterSetupService.PrepareAgentMetadata(config);
            string bundle = Path.Combine(config, "Local/Quasar.Agent");
            Assert.Equal("Quasar.Agent.dll", File.ReadAllText(Path.Combine(bundle, "Quasar.Agent.dll")));
            Assert.False(File.Exists(Path.Combine(config, "Local/Quasar.Agent.dll")));
            var metadata = System.Xml.Linq.XDocument.Load(Path.Combine(bundle, "Quasar.Agent.xml")).Root!;
            Assert.Equal("quasar-agent", metadata.Element("Id")!.Value);
            Assert.Matches("^[a-fA-F0-9]{40}$", metadata.Element("Commit")!.Value);
            Assert.Contains("quasar-agent", File.ReadAllText(Path.Combine(config, "Profiles/Current.xml")));
        }
        finally { Directory.Delete(config, true); }
    }

    [Fact]
    public async Task OlderMagnetarIsOnlyInvokedWithHelpAndNeverStartedAsAServer()
    {
        if (!OperatingSystem.IsLinux()) return;
        string folder = Path.Combine(Path.GetTempPath(), "old-magnetar-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            string executable = Path.Combine(folder, "launcher");
            await File.WriteAllTextAsync(executable, "#!/bin/sh\nif [ \"$1\" = '-help' ]; then echo 'Older Magnetar help'; exit 0; fi\ntouch '" + Path.Combine(folder, "started") + "'\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ClusterSetupService.RequirePreparationCommandAsync(executable, default));
            Assert.False(File.Exists(Path.Combine(folder, "started")));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void MachineInstallationRoutesRequireSecurityAdministration()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<ClusterHostCatalog>(_ => null!);
        builder.Services.AddSingleton<ClusterHostTunnels>(_ => null!);
        builder.Services.AddSingleton<ClusterHostInstaller>(_ => null!);
        var app = builder.Build(); app.MapClusterHostEnrollmentApi();
        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>();
        foreach (var endpoint in routes.Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("POST")))
            Assert.Contains(endpoint.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>(), p => p.Policy == QuasarPolicyNames.CanManageSecurity);
    }
}
