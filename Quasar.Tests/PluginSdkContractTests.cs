using System.Reflection;
using System.Reflection.Emit;
using Quasar.ClusterDeployment;
using Xunit;

namespace Quasar.Tests;

public sealed class PluginSdkContractTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "plugin-sdk-contract-" + Guid.NewGuid()
    );

    public PluginSdkContractTests() => Directory.CreateDirectory(Path.Combine(_root, "cli"));

    public void Dispose() => Directory.Delete(_root, true);

    // A PluginSdk.dll stand-in, optionally declaring [assembly: AssemblyMetadata("PluginSdkContract", contract)].
    private string Sdk(string? contract, string name = "PluginSdk")
    {
        var builder = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
        var metadata = typeof(AssemblyMetadataAttribute).GetConstructor([
            typeof(string),
            typeof(string),
        ])!;
        builder.SetCustomAttribute(
            new CustomAttributeBuilder(metadata, ["RepositoryUrl", "https://example.invalid"])
        );
        if (contract is not null)
            builder.SetCustomAttribute(
                new CustomAttributeBuilder(metadata, ["PluginSdkContract", contract])
            );
        builder.DefineDynamicModule(name);
        string path = Path.Combine(_root, name + "-" + Guid.NewGuid().ToString("N") + ".dll");
        builder.Save(path);
        return path;
    }

    private string Package(string capabilities)
    {
        File.WriteAllText(Path.Combine(_root, "cli/deployment-capabilities.json"), capabilities);
        return _root;
    }

    [Fact]
    public void ReadsTheContractWithoutLoadingTheAssembly()
    {
        Assert.Equal(new PluginSdkContract(1, 2), PluginSdkContract.Read(Sdk("1.2")));
        Assert.Null(PluginSdkContract.Read(Sdk(null)));
        Assert.Throws<InvalidDataException>(() => PluginSdkContract.Read(Sdk("1.2.3")));
        Assert.Throws<InvalidDataException>(() => PluginSdkContract.Read(Sdk("latest")));
    }

    [Theory]
    [InlineData("1.0", "1.0", true)]
    [InlineData("1.0", "1.3", true)]
    [InlineData("1.2", "1.1", false)]
    [InlineData("1.0", "2.0", false)]
    [InlineData("2.0", "1.9", false)]
    public void SameMajorAndEqualOrHigherMinorSatisfies(
        string required,
        string actual,
        bool satisfied
    ) =>
        Assert.Equal(
            satisfied,
            PluginSdkContract.Parse(required).IsSatisfiedBy(PluginSdkContract.Parse(actual))
        );

    [Fact]
    public void ComparesContractsWhenBothSidesDeclareOne()
    {
        string otherHash = new('a', 64);
        string package = Package(
            $$"""{"pluginServices":1,"pluginSdkContract":"1.1","pluginSdkSha256":"{{otherHash}}"}"""
        );
        Assert.Null(ClusterDeploymentFiles.PluginSdkMismatch(package, Sdk("1.1")));
        Assert.Null(ClusterDeploymentFiles.PluginSdkMismatch(package, Sdk("1.4")));

        string older = ClusterDeploymentFiles.PluginSdkMismatch(package, Sdk("1.0"))!;
        Assert.Contains("contract 1.1, but Magnetar has 1.0", older);
        Assert.Contains("newer Magnetar", older);

        string newerMajor = ClusterDeploymentFiles.PluginSdkMismatch(package, Sdk("2.0"))!;
        Assert.Contains("contract 1.1, but Magnetar has 2.0", newerMajor);
        Assert.Contains("cluster release built for contract 2.x", newerMajor);
    }

    [Fact]
    public void FallsBackToTheExactHashWhenEitherSideHasNoContract()
    {
        string sdk = Sdk(null);
        string hash = ClusterDeploymentFiles.Hash(File.ReadAllBytes(sdk));
        Assert.Null(
            ClusterDeploymentFiles.PluginSdkMismatch(
                Package(
                    $$"""{"pluginServices":1,"pluginSdkContract":"1.0","pluginSdkSha256":"{{hash}}"}"""
                ),
                sdk
            )
        );

        string contracted = Sdk("1.0");
        string contractedHash = ClusterDeploymentFiles.Hash(File.ReadAllBytes(contracted));
        string hashOnly = Package($$"""{"pluginServices":1,"pluginSdkSha256":"{{hash}}"}""");
        Assert.Null(ClusterDeploymentFiles.PluginSdkMismatch(hashOnly, sdk));
        string mismatch = ClusterDeploymentFiles.PluginSdkMismatch(hashOnly, contracted)!;
        Assert.Contains(
            $"built against PluginSdk {hash}, but Magnetar has {contractedHash}",
            mismatch
        );
        Assert.Contains("release none, Magnetar 1.0", mismatch);

        string noHash = ClusterDeploymentFiles.PluginSdkMismatch(
            Package("""{"pluginServices":1,"pluginSdkContract":"1.0"}"""),
            sdk
        )!;
        Assert.Contains("declares no contract", noHash);
    }

    [Fact]
    public void ReleasesWithoutPluginServicesPinNothing() =>
        Assert.Null(
            ClusterDeploymentFiles.PluginSdkMismatch(Package("""{"schemaVersion":1}"""), Sdk("9.9"))
        );
}
