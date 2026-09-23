using System.Formats.Tar;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Quasar.Models;
using Quasar.Services;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterArtifactTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("corrupt")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("escape")]
    [InlineData("duplicate")]
    public async Task ExportRetrievalRequiresExactVerifiedInventory(string scenario)
    {
        string root = Path.Combine(Path.GetTempPath(), "cluster-export-" + Guid.NewGuid());
        string credential = "EXPORT_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(credential, "secret");
        try
        {
            byte[] content = Encoding.UTF8.GetBytes("world data");
            string hash = Convert.ToHexString(SHA256.HashData(content));
            string manifest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("Sandbox.sbc:" + hash)));
            var descriptor = new Admin.ArtifactDescriptor("export-1", "vanilla", null, Admin.ExportConsistency.Quiescent,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), content.Length, manifest,
                new Dictionary<string, string> { ["Sandbox.sbc"] = hash }, new(Admin.ArtifactRetrievalKind.SharedPath), []);
            using var archive = new MemoryStream();
            using (var writer = new TarWriter(archive, leaveOpen: true))
            {
                void Add(string name, byte[] bytes) => writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
                    { DataStream = new MemoryStream(bytes) });
                if (scenario != "missing") Add(scenario == "escape" ? "../Sandbox.sbc" : "Sandbox.sbc",
                    scenario == "corrupt" ? new byte[content.Length] : content);
                if (scenario is "extra" or "duplicate") Add(scenario == "extra" ? "extra" : "Sandbox.sbc", content);
            }
            var client = new ClusterHostClient(new HttpClient(new Handler(archive.ToArray())));
            var cluster = new ClusterDefinition { UniqueName = "demo", HostCommandUrl = "http://host.test",
                HostCommandTokenEnvironmentVariable = credential };
            if (scenario == "valid")
            {
                await client.RetrieveArtifactAsync(cluster, descriptor, root, default);
                Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(root, "Sandbox.sbc")));
            }
            else await Assert.ThrowsAsync<InvalidDataException>(() => client.RetrieveArtifactAsync(cluster, descriptor, root, default));
        }
        finally
        {
            Environment.SetEnvironmentVariable(credential, null);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class Handler(byte[] archive) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("secret", request.Headers.Authorization?.Parameter);
            Assert.Equal("/host/v1/artifacts/demo/export-1", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        }
    }
}
