using Microsoft.Extensions.Configuration;
using Xunit;

namespace Quasar.Tests;

public sealed class WorkerConfigurationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "worker-config-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => Directory.Delete(root, true);

    private string Settings(string folder, int port)
    {
        string directory = Path.Combine(root, folder);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "appsettings.json"), "{\"Quasar\":{\"Port\":" + port + "}}");
        return directory;
    }

    [Fact]
    public void InstalledWorkerIgnoresOtherInstallsAboveIt()
    {
        Settings("Quasar", 8080); // an older install in the home folder
        string install = Settings("test/install", 25109);
        string worker = Settings("test/install/ManagedRuntime/WebService/1.0", 25109);

        var directories = Program.GetConfigurationDirectories(worker + "/", worker, install);

        Assert.Equal([worker, Path.Combine(worker, "WebService"), install], directories);
        var configuration = new ConfigurationBuilder();
        foreach (string directory in directories)
            configuration.AddJsonFile(Path.Combine(directory, "appsettings.json"), optional: true);
        Assert.Equal("25109", configuration.Build()["Quasar:Port"]);
    }

    [Fact]
    public void DataDirectoryWinsWhenTheWorkerRunsFromIt()
    {
        string install = Settings("install", 25109);
        string elsewhere = Settings("elsewhere", 8080);
        Assert.Equal([elsewhere, Path.Combine(install, "WebService"), install],
            Program.GetConfigurationDirectories(install, elsewhere, install));
    }

    [Fact]
    public void SourceTreeRunReadsTheProjectSettingsOnly()
    {
        Settings("Quasar", 8080);
        string project = Settings("repo/Quasar", 8080);
        File.WriteAllText(Path.Combine(project, "Quasar.csproj"), "<Project />");
        string bin = Path.Combine(project, "bin", "Debug", "net10.0");
        string data = Path.Combine(root, "data");

        Assert.Equal([bin, Path.Combine(bin, "WebService"), project, data],
            Program.GetConfigurationDirectories(bin, bin, data));
    }
}
