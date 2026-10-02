using System.Text.Json;
using Quasar.ClusterDeployment;

namespace Quasar.Host;

internal static class DeploymentPreparation
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i += 2)
            if (i + 1 == args.Length || args[i] is not ("--file" or "--sha256" or "--directory")
                || !options.TryAdd(args[i], args[i + 1])) return Usage();
        if (options.Count != 3) return Usage();
        try
        {
            if (new FileInfo(options["--file"]).Length > 64 * 1024 * 1024)
                throw new InvalidDataException("Deployment input file exceeds 64 MiB.");
            byte[] bytes = await File.ReadAllBytesAsync(options["--file"]);
            var result = await ClusterDeploymentFiles.PrepareAsync(bytes, options["--sha256"], options["--directory"], default);
            Console.WriteLine(JsonSerializer.Serialize(result, ClusterDeploymentFiles.JsonOptions));
            return 0;
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException
            or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or PlatformNotSupportedException
            or KeyNotFoundException or System.Xml.XmlException)
        {
            Console.Error.WriteLine(error.Message);
            return 3;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: Quasar.Host deployment prepare --file FILE --sha256 SHA256 --directory DIRECTORY");
        return 2;
    }
}
