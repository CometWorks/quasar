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
        string mutableInputs = temporary + ".inputs";
        try
        {
            // The packaged generator edits its copied checkpoint/configuration. Give
            // copytree writable copies while keeping shared snapshot objects read-only.
            // It only copies the mods (#1397): they stay the read-only input instead of a
            // second 1.7 GB copy in the temporary folder, which is RAM on a tmpfs host.
            await ClusterWorldFiles.CopyAsync(request.WorldDirectory, Path.Combine(mutableInputs, "world"), token);
            await File.WriteAllBytesAsync(temporary, specification, token);
            var start = new ProcessStartInfo("python3") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-B"); // Imports must not write bytecode into the verified package.
            foreach (string value in new[] { Path.Combine(root, "Package/cli/managed_deployment.py"), "--installation", root,
                "prepare", "--spec", temporary, "--sha256", request.SpecificationSha256, "--host", hostId,
                "--world", Path.Combine(mutableInputs, "world"), "--destination", request.ConfigurationDirectory }) start.ArgumentList.Add(value);
            if (request.ModDirectory is { } mods)
            { start.ArgumentList.Add("--mods"); start.ArgumentList.Add(Path.GetFullPath(mods)); }
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
            using (var content = await ClusterContentStore.OpenAsync(Path.GetDirectoryName(request.ConfigurationDirectory)!, token))
                foreach (var (relative, pin) in await ClusterDeploymentFiles.InspectAsync(request.ConfigurationDirectory, token))
                    await content.AdoptAsync(Path.Combine(request.ConfigurationDirectory, relative), pin, token);
            return new(hostId, bundle.Manifest.Revision, manifest, hash);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (Directory.Exists(mutableInputs)) Directory.Delete(mutableInputs, true);
        }
    }

    internal static async Task<int> RunAsync(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
            if (i + 1 == args.Length || !values.TryAdd(args[i], args[i + 1])) return 2;
        string[] required = ["--installation", "--file", "--sha256", "--host", "--world", "--directory"];
        if (values.Count is < 6 or > 7 || required.Any(key => !values.ContainsKey(key))
            || values.Keys.Any(key => !required.Contains(key) && key != "--mods"))
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
            string specification = await File.ReadAllTextAsync(values["--file"]);
            using var document = JsonDocument.Parse(specification);
            var prepared = await PrepareAsync(new(document.RootElement.GetProperty("clusterId").GetString()!, root, hash,
                specification, values["--sha256"], values["--world"], values["--directory"], values.GetValueOrDefault("--mods")),
                values["--host"], default);
            Console.WriteLine(JsonSerializer.Serialize(prepared, ClusterDeploymentFiles.JsonOptions));
            return 0;
        }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidDataException
            or InvalidOperationException or JsonException or System.Security.Cryptography.CryptographicException)
        { Console.Error.WriteLine(error.Message); return 3; }
    }
}
