using System.Diagnostics;
using System.Text.Json;
using Quasar.ClusterDeployment;

namespace Quasar.Host;

internal static class ManagedPreparation
{
    internal static async Task<global::Quasar.Host.Contract.V1.HostPreparedConfiguration> PrepareAsync(
        global::Quasar.Host.Contract.V1.HostDeploymentPreparation request, string hostId, CancellationToken token)
    {
        byte[] specification = System.Text.Encoding.UTF8.GetBytes(request.SpecificationJson);
        if (specification.Length > 16 * 1024 * 1024 || ClusterDeploymentFiles.Hash(specification) != request.SpecificationSha256)
            throw new InvalidDataException("Deployment specification hash mismatch or size limit exceeded.");
        using var document = JsonDocument.Parse(specification);
        if (document.RootElement.GetProperty("clusterId").GetString() != request.ClusterId)
            throw new InvalidDataException("Deployment specification identifies a different cluster.");
        string root = Path.GetFullPath(request.InstallationDirectory);
        byte[] inputs = await File.ReadAllBytesAsync(Path.Combine(root, "inputs.json"), token);
        if (Path.GetFileName(root) != request.InputsSha256)
            throw new InvalidDataException("Installation directory does not match its pinned inputs.");
        await ClusterDeploymentFiles.PrepareAsync(inputs, request.InputsSha256, Path.GetDirectoryName(root)!, token);
        string temporary = Path.Combine(Path.GetTempPath(), "quasar-spec-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllBytesAsync(temporary, specification, token);
            var start = new ProcessStartInfo("python3") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string value in new[] { Path.Combine(root, "Package/cli/managed_deployment.py"), "--installation", root,
                "prepare", "--spec", temporary, "--sha256", request.SpecificationSha256, "--host", hostId,
                "--world", request.WorldDirectory, "--destination", request.ConfigurationDirectory }) start.ArgumentList.Add(value);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start packaged preparation.");
            var output = process.StandardOutput.ReadToEndAsync(token);
            var error = process.StandardError.ReadToEndAsync(token);
            try { await process.WaitForExitAsync(token); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
            await output;
            if (process.ExitCode != 0) throw new InvalidDataException("Packaged preparation failed: " + await error);
            string manifest = Path.Combine(Path.GetFullPath(request.ConfigurationDirectory), "bundle.json");
            string hash = ClusterDeploymentFiles.Hash(await File.ReadAllBytesAsync(manifest, token));
            var bundle = ExecutionBundle.Load(manifest, hash);
            if (bundle.Manifest.ClusterId != request.ClusterId || bundle.Manifest.HostId != hostId)
                throw new InvalidDataException("Prepared deployment identity mismatch.");
            return new(hostId, bundle.Manifest.Revision, manifest, hash);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static async Task<int> RunAsync(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
            if (i + 1 == args.Length || !values.TryAdd(args[i], args[i + 1])) return 2;
        string[] required = ["--installation", "--file", "--sha256", "--host", "--world", "--directory"];
        if (values.Count != required.Length || required.Any(key => !values.ContainsKey(key)))
        {
            Console.Error.WriteLine("Usage: Quasar.Host deployment configure --installation DIR --file SPEC --sha256 SHA256 --host HOST --world SEED --directory DIR");
            return 2;
        }
        try
        {
            string root = Path.GetFullPath(values["--installation"]);
            byte[] inputs = await File.ReadAllBytesAsync(Path.Combine(root, "inputs.json"));
            string hash = ClusterDeploymentFiles.Hash(inputs);
            if (Path.GetFileName(root) != hash) throw new InvalidDataException("Installation path does not match its input hash.");
            await ClusterDeploymentFiles.PrepareAsync(inputs, hash, Path.GetDirectoryName(root)!, default);
            string script = Path.Combine(root, "Package/cli/managed_deployment.py");
            if (!File.Exists(script)) throw new InvalidDataException("Cluster release lacks managed deployment preparation.");
            var start = new ProcessStartInfo("python3") { UseShellExecute = false };
            foreach (string argument in new[] { script, "--installation", root, "prepare", "--spec", values["--file"],
                "--sha256", values["--sha256"], "--host", values["--host"], "--world", values["--world"],
                "--destination", values["--directory"] }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start packaged preparation.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) return process.ExitCode;
            // Verify the emitted execution manifest independently of the packaged generator.
            string manifest = Path.Combine(Path.GetFullPath(values["--directory"]), "bundle.json");
            ExecutionBundle.Load(manifest, ExecutionBundle.Hash(await File.ReadAllBytesAsync(manifest)));
            return 0;
        }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidDataException
            or InvalidOperationException or JsonException or System.Security.Cryptography.CryptographicException)
        { Console.Error.WriteLine(error.Message); return 3; }
    }
}
