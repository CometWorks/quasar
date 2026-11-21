using Quasar.Updates;

internal static class WindowsServiceCommandTests
{
    internal static void Run()
    {
        const string main = @"C:\SERWERY\SE_Quasar\Quasar.exe";
        const string spacedMain = @"C:\Quasar Server\Quasar.exe";
        const string updater = @"C:\SERWERY\SE_Quasar\Updater\Quasar.Updater.exe";
        const string metadata = @"C:\ProgramData\Quasar-ServiceInstallations\Quasar.json";
        const string spacedMetadata = @"C:\Program Data\Quasar-ServiceInstallations\Quasar.json";
        foreach (var executable in new[] { main, spacedMain })
        {
            Check($"\"{executable}\" serve --service", executable, ["serve", "--service"], true);
            Check($"{executable} serve --service", executable, ["serve", "--service"], executable == main);
            foreach (var action in new[] { "serve", "--service serve", "serve --service --other", "serve --service --data-dir C:\\Other", "serve --serviceX", "serve --service ", "serve --service & evil", "serve --service\n" })
                Check($"\"{executable}\" {action}", executable, ["serve", "--service"], false);
        }
        foreach (var executableQuoted in new[] { false, true })
        foreach (var metadataQuoted in new[] { false, true })
        {
            var exe = executableQuoted ? $"\"{updater}\"" : updater;
            var path = metadataQuoted ? $"\"{metadata}\"" : metadata;
            Check($"{exe} --installation {path}", updater, ["--installation", metadata], true);
            path = metadataQuoted ? $"\"{spacedMetadata}\"" : spacedMetadata;
            Check($"{exe} --installation {path}", updater, ["--installation", spacedMetadata], metadataQuoted);
        }
        Check($"\"{main.ToLowerInvariant()}\" serve --service", main, ["serve", "--service"], true);
        foreach (var command in new[] {
            $"\"{main}.evil.exe\" serve --service", $"\"{main}\"evil serve --service",
            $"\"{main} serve --service", $"{main}\" serve --service", $"\"{main}\" \"serve --service\"",
            $"C:\\Other\\Quasar.exe serve --service", $"cmd.exe /c \"{main}\" serve --service",
            $"\"C:\\Quasar\" Server\\Quasar.exe serve --service", $"\"{main}\" serve\t--service" })
            Check(command, main, ["serve", "--service"], false);
        foreach (var command in new[] {
            $"\"{updater}\" --installation \"{metadata}.other\"", $"\"{updater}\" --installation \"{metadata}\" --installation C:\\Other.json",
            $"\"{updater}\" \"--installation {metadata}\"", $"\"{updater}\" {metadata} --installation",
            $"\"{updater}\" --installation \"{metadata}", $"\"{updater}\" --installation {metadata}\"" })
            Check(command, updater, ["--installation", metadata], false);
        Console.WriteLine("PASS Windows service command equivalence, exact arguments and ambiguous-path rejection");
    }

    private static void Check(string command, string executable, string[] arguments, bool expected)
    {
        if (NativeQuasarServices.MatchesWindowsCommand(command, executable, arguments) != expected)
            throw new Exception($"Windows service command check expected {expected}: {command}");
    }
}
