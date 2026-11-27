using System.Reflection;
using System.Text.RegularExpressions;
using Quasar.Ui.Components;
using Xunit;

namespace Quasar.Tests.Ui;

public sealed partial class QuasarIconCoverageTests
{
    [Fact]
    public void EveryMaterialIconReferenceHasAMatchingVariantConstant()
    {
        var root = FindRepositoryRoot();
        var missing = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in SourceFiles(root))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in IconPattern.Matches(text))
            {
                var variant = match.Groups["variant"].Value;
                var name = match.Groups["name"].Value;
                var constant = variant switch
                {
                    "Filled" => name,
                    "Outlined" => name + "Outlined",
                    "Rounded" => name + "Rounded",
                    "Sharp" => name + "Sharp",
                    "TwoTone" => name + "TwoTone",
                    _ => name,
                };

                if (typeof(QuasarIcons).GetField(constant, BindingFlags.Public | BindingFlags.Static) is null)
                    missing.Add($"{variant}.{name} requires QuasarIcons.{constant}");
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void ProductionCodeDoesNotReferenceMudBlazorCustomIcons()
    {
        var root = FindRepositoryRoot();
        var offenders = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in SourceFiles(root))
        {
            if (CustomIconPattern.IsMatch(File.ReadAllText(file)))
                offenders.Add(Path.GetRelativePath(root, file));
        }

        Assert.Empty(offenders);
    }

    [GeneratedRegex(@"Icons\.Material\.(?<variant>Filled|Outlined|Rounded|Sharp|TwoTone)\.(?<name>[A-Za-z0-9_]+)", RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture)]
    private static partial Regex IconPattern { get; }

    [GeneratedRegex(@"Icons\.Custom\.", RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture)]
    private static partial Regex CustomIconPattern { get; }

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(file =>
                (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                 file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)) &&
                !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !file.Contains($"{Path.DirectorySeparatorChar}Quasar.Tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !file.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Quasar.sln")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
