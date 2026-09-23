using System.Text.Json;
using Quasar.ClusterDeployment;

namespace Quasar.Host;

internal static class DeploymentTransfer
{
    internal static async Task<int> RunAsync(string action, string[] args)
    {
        var options = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i += 2)
            if (i + 1 == args.Length || !options.TryAdd(args[i], args[i + 1])) return Usage();
        try
        {
            if (action == "export" && options.Count == 2 && options.ContainsKey("--installation") && options.ContainsKey("--file"))
            {
                string path = Path.GetFullPath(options["--file"]);
                string temporary = path + "." + Guid.NewGuid().ToString("N");
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { await ClusterDeploymentFiles.ExportAsync(options["--installation"], stream, default); stream.Flush(true); }
                    File.Move(temporary, path); // Never replace an operator's archive.
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                return 0;
            }
            if (action == "import" && options.Count == 3 && options.ContainsKey("--file")
                && options.ContainsKey("--sha256") && options.ContainsKey("--directory"))
            {
                using var input = File.OpenRead(options["--file"]);
                var result = await ClusterDeploymentFiles.ImportAsync(input, options["--sha256"], options["--directory"], default);
                Console.WriteLine(JsonSerializer.Serialize(result, ClusterDeploymentFiles.JsonOptions));
                return 0;
            }
            return Usage();
        }
        catch (Exception error) when (error is IOException or ArgumentException or JsonException or InvalidDataException
            or UnauthorizedAccessException or InvalidOperationException)
        { Console.Error.WriteLine(error.Message); return 3; }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: Quasar.Host deployment export --installation DIR --file ARCHIVE.tar | import --file ARCHIVE.tar --sha256 INPUTS_SHA256 --directory DIR");
        return 2;
    }
}
