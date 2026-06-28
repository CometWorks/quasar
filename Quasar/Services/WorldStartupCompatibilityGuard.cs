using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Quasar.Services;

public static class WorldStartupCompatibilityGuard
{
    private const int MaxReportedReferences = 5;

    private static readonly string[] OxygenReferenceTokens =
    [
        "MyObjectBuilder_OxygenGenerator",
        "MyObjectBuilder_OxygenTank",
        "MyObjectBuilder_OxygenFarm",
        "MyObjectBuilder_AirVent",
        "MyObjectBuilder_GasProperties/Oxygen",
    ];

    public static WorldStartupCompatibilityIssue? Check(string worldPath)
    {
        if (string.IsNullOrWhiteSpace(worldPath) || !Directory.Exists(worldPath))
            return null;

        var sandboxConfigPath = Path.Combine(worldPath, WorldSandboxConfigEditor.SandboxConfigFileName);
        if (!IsOxygenDisabled(sandboxConfigPath))
            return null;

        var references = FindOxygenReferences(worldPath).ToList();
        if (references.Count == 0)
            return null;

        return new WorldStartupCompatibilityIssue(
            BuildOxygenDisabledMessage(references),
            references);
    }

    private static bool IsOxygenDisabled(string sandboxConfigPath)
    {
        if (!File.Exists(sandboxConfigPath))
            return false;

        try
        {
            var document = XDocument.Load(sandboxConfigPath, LoadOptions.None);
            var value = document.Root?
                .Element("Settings")?
                .Element("EnableOxygen")?
                .Value?
                .Trim();

            return bool.TryParse(value, out var enabled) && !enabled;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<WorldOxygenReference> FindOxygenReferences(string worldPath)
    {
        var index = 0;
        foreach (var filePath in EnumerateWorldDataFiles(worldPath))
        {
            foreach (var reference in FindOxygenReferences(worldPath, filePath))
            {
                index++;
                yield return reference with { Index = index };
                if (index >= MaxReportedReferences)
                    yield break;
            }
        }
    }

    private static IEnumerable<WorldOxygenReference> FindOxygenReferences(string worldPath, string filePath)
    {
        var settings = new XmlReaderSettings
        {
            CloseInput = true,
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            XmlResolver = null,
        };

        XmlReader reader;
        try
        {
            reader = XmlReader.Create(File.OpenRead(filePath), settings);
        }
        catch
        {
            yield break;
        }

        using (reader)
        {
            var lineInfo = reader as IXmlLineInfo;
            while (true)
            {
                try
                {
                    if (!reader.Read())
                        yield break;
                }
                catch
                {
                    yield break;
                }

                if (reader.NodeType != XmlNodeType.Element)
                    continue;

                if (TryMatchOxygenReference(reader.Name, out var nameToken))
                    yield return BuildReference(worldPath, filePath, nameToken, lineInfo);

                if (!reader.HasAttributes)
                    continue;

                for (var attributeIndex = 0; attributeIndex < reader.AttributeCount; attributeIndex++)
                {
                    reader.MoveToAttribute(attributeIndex);
                    if (TryMatchOxygenReference(reader.Value, out var attributeToken))
                        yield return BuildReference(worldPath, filePath, attributeToken, lineInfo);
                }

                reader.MoveToElement();
            }
        }
    }

    private static IEnumerable<string> EnumerateWorldDataFiles(string worldPath)
    {
        foreach (var filePath in Directory.EnumerateFiles(worldPath, "*", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(filePath);
            if (string.Equals(fileName, WorldSandboxConfigEditor.SandboxConfigFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            var extension = Path.GetExtension(filePath);
            if (!string.Equals(extension, ".sbc", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".sbs", StringComparison.OrdinalIgnoreCase))
                continue;

            if (IsUnderBackupDirectory(worldPath, filePath))
                continue;

            yield return filePath;
        }
    }

    private static bool IsUnderBackupDirectory(string worldPath, string filePath)
    {
        var relativePath = Path.GetRelativePath(worldPath, filePath);
        return relativePath
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => string.Equals(segment, "Backup", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryMatchOxygenReference(string value, out string token)
    {
        foreach (var candidate in OxygenReferenceTokens)
        {
            if (value.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                token = candidate;
                return true;
            }
        }

        token = string.Empty;
        return false;
    }

    private static WorldOxygenReference BuildReference(
        string worldPath,
        string filePath,
        string token,
        IXmlLineInfo? lineInfo)
    {
        var lineNumber = lineInfo?.HasLineInfo() == true ? lineInfo.LineNumber : 0;
        return new WorldOxygenReference(
            Index: 1,
            RelativePath: Path.GetRelativePath(worldPath, filePath),
            Token: token,
            LineNumber: lineNumber > 0 ? lineNumber : null);
    }

    private static string BuildOxygenDisabledMessage(IReadOnlyList<WorldOxygenReference> references)
    {
        var first = references[0];
        var location = first.LineNumber is > 0
            ? $"{first.RelativePath}:{first.LineNumber.Value.ToString(CultureInfo.InvariantCulture)}"
            : first.RelativePath;

        return "Startup blocked: oxygen is disabled but the save contains oxygen-capable blocks or definitions " +
               $"({first.Token} in {location}). Enable Oxygen or remove the oxygen blocks before starting.";
    }
}

public sealed record WorldStartupCompatibilityIssue(
    string Message,
    IReadOnlyList<WorldOxygenReference> References);

public sealed record WorldOxygenReference(
    int Index,
    string RelativePath,
    string Token,
    int? LineNumber);
