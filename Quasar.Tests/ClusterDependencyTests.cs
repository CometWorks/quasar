using System.Security.Claims;
using System.Text.Json;
using Quasar.ClusterDeployment;
using CometWorks.ClusterGateway.AdminContract.V1;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Models;
using Quasar.Services;
using Quasar.Services.Auth;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterDependencyTests
{
    [LinuxFact]
    public async Task SteamNativeLibraryBelongsToMagnetarRelease()
    {
        using var fixture = await Fixture.CreateAsync();
        Assert.False(File.Exists(Path.Combine(fixture.Sources["CommonPlugins"], "linux-compat/libsteam_api.so")));
        await fixture.Service.InspectAsync(fixture.Cluster, default);
        File.Delete(Path.Combine(fixture.Sources["Magnetar"], "Libraries/MagnetarInterim/libsteam_api.so"));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.InspectAsync(fixture.Cluster, default));
    }

    [LinuxFact]
    public async Task CopiesApprovedBytesAndReusesThemOfflineAfterSourcesChangeOrDisappear()
    {
        using var fixture = await Fixture.CreateAsync();
        var candidate = await fixture.Service.InspectAsync(fixture.Cluster, default);
        var request = new ClusterDependencyRequest(candidate.ManifestSha256, 1, null);
        var copies = await Task.WhenAll(fixture.Service.StageAsync(fixture.Cluster, request, default),
            fixture.Service.StageAsync(fixture.Cluster, request, default));
        Assert.All(copies, copy => Assert.Equal(candidate, copy));
        string payload = Path.Combine(fixture.Store, candidate.ManifestSha256, "payload");
        string ds = Path.Combine(payload, "DedicatedServer/DedicatedServer64/SpaceEngineers.Game.dll");
        Assert.Equal("fixture", File.ReadAllText(ds));
        File.WriteAllText(Path.Combine(fixture.Sources["DedicatedServer/DedicatedServer64"], "SpaceEngineers.Game.dll"), "updated");
        Assert.Equal("fixture", File.ReadAllText(ds));
        Directory.Delete(Path.Combine(fixture.Root, "inputs"), true);
        Assert.Equal(candidate, await fixture.Service.StageAsync(fixture.Cluster, request, default));
        Assert.Equal(candidate, await fixture.Service.VerifyAsync(fixture.Cluster, candidate.ManifestSha256, default));
        Assert.Empty(Directory.GetDirectories(fixture.Store, ".stage-*"));
        File.WriteAllText(ds, "tampered");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.VerifyAsync(fixture.Cluster, candidate.ManifestSha256, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.StageAsync(fixture.Cluster, request, default));
    }

    [LinuxFact]
    public async Task ChangedInputsCannotBeProvisionedUnderAnOldApproval()
    {
        using var fixture = await Fixture.CreateAsync();
        var candidate = await fixture.Service.InspectAsync(fixture.Cluster, default);
        File.WriteAllText(Path.Combine(fixture.Sources["DedicatedServer/Content"], "world.sbc"), "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.StageAsync(fixture.Cluster,
            new(candidate.ManifestSha256, 1, null), default));
        Assert.False(Directory.Exists(Path.Combine(fixture.Store, candidate.ManifestSha256)));
        Assert.NotEqual(candidate.ManifestSha256, (await fixture.Service.InspectAsync(fixture.Cluster, default)).ManifestSha256);
    }

    [LinuxFact]
    public async Task IncompleteUnpinnedAndLinkedInputsFailClosed()
    {
        using var fixture = await Fixture.CreateAsync();
        string metadata = Path.Combine(fixture.Sources["DirectTransport"], "DirectTransport.xml");
        File.WriteAllText(metadata, "<PluginData><Id>direct-transport</Id><Commit>TODO</Commit></PluginData>");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.InspectAsync(fixture.Cluster, default));
        File.Copy(metadata + ".original", metadata, true);
        File.WriteAllText(metadata, File.ReadAllText(metadata).Replace("CoreCLR", "NETCoreApp"));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.InspectAsync(fixture.Cluster, default));
        File.Copy(metadata + ".original", metadata, true);
        string extra = Path.Combine(fixture.Sources["DirectTransport"], "linked.dll");
        File.CreateSymbolicLink(extra, metadata);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.InspectAsync(fixture.Cluster, default));
        File.Delete(extra);
        File.Delete(Path.Combine(fixture.Sources["DirectTransport"], "LiteNetLib.dll"));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.InspectAsync(fixture.Cluster, default));
    }

    [LinuxFact]
    public async Task ProvisioningPersistsFencedSelectionWithoutActivatingAndReportsTampering()
    {
        using var fixture = await Fixture.CreateAsync();
        string catalogPath = Path.Combine(fixture.Root, "catalog");
        Directory.CreateDirectory(Path.Combine(catalogPath, "demo"));
        File.WriteAllText(Path.Combine(catalogPath, "demo", "cluster.json"), """
        {"uniqueName":"demo","gatewayUrl":"http://gateway.test","goalState":"On","updatedAtUtc":"2026-09-19T12:00:00Z"}
        """);
        using var catalog = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["Quasar:ClusterCatalogPath"] = catalogPath }).Build());
        var installed = await fixture.Packages.Service.GetInstalledAsync(fixture.Packages.Request, default);
        await catalog.SelectPackageAsync("demo", 0, installed, "package", default);
        var before = catalog.GetCluster("demo")!;
        var context = new DefaultHttpContext();
        context.Request.Headers["Idempotency-Key"] = "deps-1";
        var operations = new ClusterOperationStore(Path.Combine(fixture.Root, "operations"));
        var candidate = Data<ClusterDependencyCandidate>(await ClusterApi.GetDependencyCandidate(
            "demo", context, catalog, fixture.Service, default));
        var request = new ClusterDependencyRequest(candidate.ManifestSha256, 1, null);
        var op = Data<ClusterOperation>(await ClusterApi.StageDependencies("demo", request,
            context, catalog, fixture.Service, operations, default));
        Assert.Equal(ClusterOperationState.Succeeded, op.State);
        Assert.Equal(candidate.ManifestSha256, catalog.GetCluster("demo")!.DependencyManifestSha256);
        Assert.Equal(before.UpdatedAtUtc, catalog.GetCluster("demo")!.UpdatedAtUtc);
        Assert.Equal(before.GoalState, catalog.GetCluster("demo")!.GoalState);
        Assert.True(Data<ClusterDependencyStatus>(await ClusterApi.GetDependencies(
            "demo", context, catalog, fixture.Service, default)).Verified);
        await catalog.SelectDependenciesAsync("demo", request, default); // interrupted result persistence

        var alternative = request with { ManifestSha256 = new string('a', 64) };
        await Assert.ThrowsAsync<ClusterOperationConflictException>(() => catalog.SelectDependenciesAsync("demo", alternative, default));
        await catalog.SelectDependenciesAsync("demo", alternative with { ExpectedDependencySha256 = candidate.ManifestSha256 }, default);
        await Assert.ThrowsAsync<ClusterOperationConflictException>(() => catalog.SelectDependenciesAsync("demo", request, default));
        await catalog.SelectDependenciesAsync("demo", request with { ExpectedDependencySha256 = alternative.ManifestSha256 }, default);

        File.AppendAllText(Path.Combine(fixture.Store, candidate.ManifestSha256, "manifest.json"), " ");
        Assert.False(Data<ClusterDependencyStatus>(await ClusterApi.GetDependencies(
            "demo", context, catalog, fixture.Service, default)).Verified);
        var replay = Data<ClusterOperation>(await ClusterApi.StageDependencies("demo", request,
            context, catalog, fixture.Service, new ClusterOperationStore(Path.Combine(fixture.Root, "operations")), default));
        Assert.Equal(op.OperationId, replay.OperationId);
        await catalog.SelectPackageAsync("demo", 1, installed, "next-package", default);
        Assert.Null(catalog.GetCluster("demo")!.DependencyManifestSha256);
        await Assert.ThrowsAsync<ClusterOperationConflictException>(() => catalog.SelectDependenciesAsync("demo", request, default));

        context.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(QuasarClaimTypes.Provider, QuasarAuthSchemes.ServicePrincipal),
            new Claim(QuasarClaimTypes.Cluster, "other")], "test"));
        Assert.Equal(403, ((IStatusCodeHttpResult)await ClusterApi.StageDependencies("demo", request,
            context, catalog, fixture.Service, operations, default)).StatusCode);
        Assert.Equal(403, ((IStatusCodeHttpResult)await ClusterApi.GetDependencyCandidate(
            "demo", context, catalog, fixture.Service, default)).StatusCode);
    }

    private static T Data<T>(IResult result) => Assert.IsType<AdminEnvelope<T>>(((IValueHttpResult)result).Value).Data;

    [LinuxFact]
    public async Task HostPreparationCopiesPinnedInputsAndCanVerifyOffline()
    {
        using var fixture = await Fixture.CreateAsync();
        var candidate = await fixture.Service.InspectAsync(fixture.Cluster, default);
        await fixture.Service.StageAsync(fixture.Cluster, new(candidate.ManifestSha256, 1, null), default);
        fixture.Cluster.DependencyManifestSha256 = candidate.ManifestSha256;
        var inputs = await fixture.Service.GetDeploymentInputsAsync(fixture.Cluster, default);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(inputs, ClusterDeploymentFiles.JsonOptions);
        string hash = ClusterDeploymentFiles.Hash(bytes);
        string destination = Path.Combine(fixture.Root, "host-deployments");
        var prepared = await ClusterDeploymentFiles.PrepareAsync(bytes, hash, destination, default);
        Assert.Equal(File.ReadAllBytes(Path.Combine(inputs.PackageDirectory, "plugins/ClusterNode/ClusterNode.dll")),
            File.ReadAllBytes(Path.Combine(prepared.Directory, "Package/plugins/ClusterNode/ClusterNode.dll")));
        var environment = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(prepared.EnvironmentFile))!;
        Assert.Equal(Path.Combine(prepared.Directory, "Dependencies/payload/CommonPlugins"), environment["CLUSTER_COMMON_PLUGINS"]);
        Assert.Equal("", environment["DIRECT_TRANSPORT_SOURCE"]);
        Directory.Delete(inputs.PackageDirectory, true);
        Directory.Delete(inputs.DependencyDirectory, true);
        Assert.Equal(prepared, await ClusterDeploymentFiles.PrepareAsync(bytes, hash, destination, default));
        File.AppendAllText(Path.Combine(prepared.Directory, "Dependencies/payload/CommonPlugins/linux-compat/libHavok.so"), "tampered");
        await Assert.ThrowsAsync<InvalidDataException>(() => ClusterDeploymentFiles.PrepareAsync(bytes, hash, destination, default));
        Assert.Empty(Directory.GetDirectories(destination, ".stage-*"));
    }

    [LinuxFact]
    public async Task MissingNativeAssetsAndUnsafeAssetPathsCannotBePinned()
    {
        using var fixture = await Fixture.CreateAsync();
        string linux = Path.Combine(fixture.Sources["CommonPlugins"], "linux-compat");
        string manifest = Path.Combine(linux, "linux-compat.xml");
        string original = File.ReadAllText(manifest);
        foreach (string asset in new[] { "<Asset Name=\"bad\" Path=\"../outside\"/>",
                     "<Asset Name=\"bad\" Url=\"https://example.test/latest\"/>",
                     "<DependencyIds><Id>not-in-the-snapshot</Id></DependencyIds>" })
        {
            File.WriteAllText(manifest, original.Replace("</PluginData>", asset + "</PluginData>"));
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.InspectAsync(fixture.Cluster, default));
        }
        File.WriteAllText(manifest, original);
        File.Delete(Path.Combine(linux, "libHavok.so"));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.InspectAsync(fixture.Cluster, default));
    }

    [LinuxFact]
    public async Task HostRejectsChangedInputsLinksBadApprovalAndUnsupportedPackages()
    {
        using var fixture = await Fixture.CreateAsync();
        var candidate = await fixture.Service.InspectAsync(fixture.Cluster, default);
        await fixture.Service.StageAsync(fixture.Cluster, new(candidate.ManifestSha256, 1, null), default);
        fixture.Cluster.DependencyManifestSha256 = candidate.ManifestSha256;
        var inputs = await fixture.Service.GetDeploymentInputsAsync(fixture.Cluster, default);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(inputs, ClusterDeploymentFiles.JsonOptions);
        string hash = ClusterDeploymentFiles.Hash(bytes);
        string destination = Path.Combine(fixture.Root, "host");
        await Assert.ThrowsAsync<InvalidDataException>(() => ClusterDeploymentFiles.PrepareAsync(bytes, new string('0', 64), destination, default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClusterDeploymentFiles.PrepareAsync(bytes, hash, destination, cancelled.Token));
        string native = Path.Combine(inputs.DependencyDirectory, "payload/CommonPlugins/linux-compat/libHavok.so");
        File.Delete(native);
        File.CreateSymbolicLink(native, Path.Combine(fixture.Sources["CommonPlugins"], "linux-compat/libHavok.so"));
        await Assert.ThrowsAsync<InvalidDataException>(() => ClusterDeploymentFiles.PrepareAsync(bytes, hash, destination, default));
        File.Delete(native);
        File.WriteAllText(native, "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => ClusterDeploymentFiles.PrepareAsync(bytes, hash, destination, default));
        Assert.False(Directory.Exists(destination));
        using var package = new ClusterPackageTests.Fixture();
        var old = await package.Service.StageAsync(package.Request, default);
        Assert.Throws<FileNotFoundException>(() => ClusterDeploymentFiles.ValidateCapabilities(old.PackagePath));
    }

    private sealed class LinuxFactAttribute : FactAttribute
    { public LinuxFactAttribute() { if (!OperatingSystem.IsLinux()) Skip = "Cluster dependencies require Linux."; } }

    private sealed class Fixture : IDisposable
    {
        public ClusterPackageTests.Fixture Packages { get; } = new(ClusterPackageTests.CreateArchive(deploymentCapabilities: true));
        public string Root => Packages.Root;
        public string Store => Path.Combine(Root, "dependencies");
        public Dictionary<string, string> Sources { get; } = new();
        public ClusterDependencyService Service { get; private set; } = null!;
        public ClusterDefinition Cluster { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            var package = await fixture.Packages.Service.StageAsync(fixture.Packages.Request, default);
            fixture.Packages.Offline = true;
            fixture.Cluster = new() { UniqueName = "demo", GatewayUrl = "http://gateway.test",
                PackageSelection = new(1, package.Release.Version, package.Release.Sha256, package.Commit, "package") };
            foreach (string prefix in new[] { "DedicatedServer/DedicatedServer64", "DedicatedServer/Content", "Magnetar", "DirectTransport", "CommonPlugins" })
            {
                string path = Path.Combine(fixture.Root, "inputs", prefix);
                fixture.Sources.Add(prefix, path);
                Directory.CreateDirectory(path);
            }
            foreach (string file in new[] { "DedicatedServer/DedicatedServer64/SpaceEngineersDedicated.exe",
                "DedicatedServer/DedicatedServer64/SpaceEngineers.Game.dll", "DedicatedServer/DedicatedServer64/Sandbox.Game.dll",
                "DedicatedServer/DedicatedServer64/VRage.dll", "DedicatedServer/Content/world.sbc", "Magnetar/MagnetarInterim.bin",
                "Magnetar/Libraries/MagnetarInterim/PluginSdk.dll", "Magnetar/Libraries/MagnetarInterim/libsteam_api.so",
                "DirectTransport/DirectTransport.dll", "DirectTransport/LiteNetLib.dll" })
            {
                string path = Path.Combine(fixture.Root, "inputs", file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "fixture");
            }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Path.Combine(fixture.Sources["Magnetar"], "MagnetarInterim.bin"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
            foreach (string name in new[] { "DirectTransport.xml", "DirectTransport.dll.xml", "DirectTransport.xml.original" })
                File.WriteAllText(Path.Combine(fixture.Sources["DirectTransport"], name),
                    "<PluginData><Id>direct-transport</Id><Commit>" + new string('c', 40)
                    + "</Commit><Runtimes>CoreCLR</Runtimes><Platforms>Linux</Platforms></PluginData>");
            fixture.Service = new(() => new(fixture.Sources), fixture.Store, fixture.Packages.Service);
            foreach (string id in new[] { "dotnet-compat", "linux-compat" })
            {
                string folder = Path.Combine(fixture.Sources["CommonPlugins"], id);
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, id + ".dll"), "fixture");
                File.WriteAllText(Path.Combine(folder, id + ".xml"),
                    "<PluginData xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:type=\"GitHubPlugin\">"
                    + "<Id>" + id + "</Id><Commit>" + new string('d', 40)
                    + "</Commit><Runtimes>CoreCLR</Runtimes><Platforms>Linux</Platforms><Asset Name=\"bundle\" Path=\".\"/></PluginData>");
                if (id == "linux-compat")
                    foreach (string name in new[] { "libHavok.so", "libRecastDetour.so", "libVRageNative.so", "libEOSSDK-Linux-Shipping.so" })
                        File.WriteAllText(Path.Combine(folder, name), "native");
            }
            return fixture;
        }
        public void Dispose() => Packages.Dispose();
    }
}
