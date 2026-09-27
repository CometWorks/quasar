using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;
using Quasar.ClusterDeployment;

namespace Quasar.Services;

internal static class ClusterWorldConverter
{
    internal static async Task RunAsync(string installation, string command, string source, string destination,
        string[] dataRoots, CancellationToken token)
    {
        if (Directory.Exists(destination)) throw new InvalidOperationException("Conversion destination already exists.");
        string staging = destination + ".working";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { Path.Combine(installation, "Package/tools/MagnetarWorld/MagnetarWorld.dll"), command, source, staging }.Concat(dataRoots))
            start.ArgumentList.Add(arg);
        string ds = Path.Combine(installation, "Dependencies/payload/DedicatedServer/DedicatedServer64");
        start.Environment["SPACE_ENGINEERS_BIN64"] = ds;
        start.Environment["SPACE_ENGINEERS_ROOT"] = Path.GetDirectoryName(ds);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromHours(2));
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the packaged world converter.");
            var output = DrainAsync(process.StandardOutput, deadline.Token);
            var error = DrainAsync(process.StandardError, deadline.Token);
            try { await process.WaitForExitAsync(deadline.Token); }
            catch { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } throw; }
            await output;
            string failure = await error;
            if (process.ExitCode != 0) throw new InvalidDataException("World converter failed: " + failure);
            Validate(staging);
            await ClusterDeploymentFiles.InspectAsync(staging, token);
            Directory.Move(staging, destination);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    internal static void Validate(string world)
    {
        using var reader = XmlReader.Create(Path.Combine(world, "Sandbox.sbc"), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(reader);
        if (document.Root?.Name.LocalName != "MyObjectBuilder_Checkpoint")
            throw new InvalidDataException("Converted world has no valid checkpoint.");
    }

    private static async Task<string> DrainAsync(StreamReader reader, CancellationToken token)
    {
        char[] buffer = new char[8192]; string tail = ""; int read;
        while ((read = await reader.ReadAsync(buffer, token)) != 0)
        { tail += new string(buffer, 0, read); if (tail.Length > 8192) tail = tail[^8192..]; }
        return tail;
    }
}
