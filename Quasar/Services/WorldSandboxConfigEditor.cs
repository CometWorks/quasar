using System.Globalization;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Quasar.Models;

namespace Quasar.Services;

public static class WorldSandboxConfigEditor
{
    public const string SandboxConfigFileName = "Sandbox_config.sbc";

    public sealed record ConfigProfileImport(QuasarConfigProfile Profile, int SessionSettingCount, int ModCount);

    public static IReadOnlyList<QuasarModSelection> ReadMods(string sandboxConfigPath)
    {
        if (!File.Exists(sandboxConfigPath))
            return Array.Empty<QuasarModSelection>();

        var document = LoadDocument(sandboxConfigPath);
        var root = GetRoot(document, sandboxConfigPath);
        return ReadMods(root);
    }

    public static ConfigProfileImport ReadConfigProfile(string sandboxConfigPath)
    {
        var document = LoadDocument(sandboxConfigPath);
        var root = GetRoot(document, sandboxConfigPath);
        var profile = new QuasarConfigProfile
        {
            SessionSettings = new QuasarSessionSettings(),
            Mods = ReadMods(root).ToList(),
        };

        var sessionSettingCount = ApplyImportedSessionSettings(root, profile.SessionSettings);
        return new ConfigProfileImport(profile, sessionSettingCount, profile.Mods.Count);
    }

    private static IReadOnlyList<QuasarModSelection> ReadMods(XElement root)
    {
        var modsElement = ElementIgnoreCase(root, "Mods");
        if (modsElement is null)
            return Array.Empty<QuasarModSelection>();

        var results = new List<QuasarModSelection>();
        var seen = new HashSet<long>();
        foreach (var item in modsElement.Elements().Where(element =>
                     string.Equals(element.Name.LocalName, "ModItem", StringComparison.OrdinalIgnoreCase)))
        {
            var idText = (ElementIgnoreCase(item, "PublishedFileId")?.Value ?? string.Empty).Trim();
            if (!long.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var workshopId) || workshopId <= 0)
                continue;

            if (!seen.Add(workshopId))
                continue;

            var friendlyName = item.Attribute("FriendlyName")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(friendlyName))
                friendlyName = workshopId.ToString(CultureInfo.InvariantCulture);

            results.Add(new QuasarModSelection
            {
                WorkshopId = workshopId,
                DisplayName = friendlyName,
            });
        }

        return results;
    }

    public static async Task WriteModsAsync(
        string sandboxConfigPath,
        IReadOnlyList<QuasarModSelection> mods,
        CancellationToken cancellationToken = default)
    {
        var document = LoadDocument(sandboxConfigPath);
        ApplyMods(GetRoot(document, sandboxConfigPath), mods);

        var content = SerializeXml(document);
        await AtomicFileWriter.WriteTextAsync(sandboxConfigPath, content, cancellationToken);
    }

    public static async Task WriteProfileAsync(
        string sandboxConfigPath,
        QuasarConfigProfile profile,
        CancellationToken cancellationToken = default)
    {
        var document = LoadDocument(sandboxConfigPath);
        var root = GetRoot(document, sandboxConfigPath);

        ApplySessionSettings(root, profile.SessionSettings);
        ApplyMods(root, profile.Mods);

        var content = SerializeXml(document);
        await AtomicFileWriter.WriteTextAsync(sandboxConfigPath, content, cancellationToken);
    }

    private static XDocument LoadDocument(string sandboxConfigPath)
    {
        if (!File.Exists(sandboxConfigPath))
            throw new FileNotFoundException(
                $"{SandboxConfigFileName} not found at '{sandboxConfigPath}'.",
                sandboxConfigPath);

        try
        {
            return XDocument.Load(sandboxConfigPath, LoadOptions.PreserveWhitespace);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Failed to parse '{sandboxConfigPath}'.", exception);
        }
    }

    private static XElement GetRoot(XDocument document, string sandboxConfigPath)
    {
        return document.Root
            ?? throw new InvalidOperationException($"'{sandboxConfigPath}' has no root element.");
    }

    private static XElement? ElementIgnoreCase(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));

    private static int ApplyImportedSessionSettings(XElement root, QuasarSessionSettings sessionSettings)
    {
        var settingsElement = ElementIgnoreCase(root, "Settings");
        if (settingsElement is null)
            return 0;

        var count = 0;
        foreach (var option in QuasarConfigMetadata.Options.Where(option => option.Scope == QuasarConfigOptionScope.Session))
        {
            if (string.IsNullOrWhiteSpace(option.ElementName))
                continue;

            var element = ElementIgnoreCase(settingsElement, option.ElementName);
            if (element is null)
                continue;

            var property = QuasarConfigMetadata.GetProperty(option);
            if (!TryReadValue(option, property, element, out var value))
                continue;

            property.SetValue(sessionSettings, value);
            count++;
        }

        return count;
    }

    private static bool TryReadValue(
        QuasarConfigOptionDefinition option,
        PropertyInfo property,
        XElement element,
        out object value)
    {
        value = default!;
        var text = (element.Value ?? string.Empty).Trim();

        switch (option.Kind)
        {
            case QuasarConfigOptionKind.Boolean:
                if (TryParseBool(text, out var boolValue))
                {
                    value = boolValue;
                    return true;
                }

                return false;

            case QuasarConfigOptionKind.Integer:
                return TryReadInteger(property.PropertyType, text, out value);

            case QuasarConfigOptionKind.SelectInteger:
                if (TryParseSelectInteger(option, text, out var selectValue))
                {
                    value = selectValue;
                    return true;
                }

                return false;

            case QuasarConfigOptionKind.Decimal:
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
                {
                    value = doubleValue;
                    return true;
                }

                return false;

            case QuasarConfigOptionKind.KeyValueText
                when property.PropertyType == typeof(Dictionary<string, int>):
                value = ReadBlockTypeLimits(element);
                return true;

            case QuasarConfigOptionKind.Text:
            case QuasarConfigOptionKind.LongText:
            case QuasarConfigOptionKind.Password:
                value = text;
                return true;

            default:
                return false;
        }
    }

    private static bool TryReadInteger(Type propertyType, string text, out object value)
    {
        value = default!;

        if (propertyType == typeof(long))
        {
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
                return false;

            value = longValue;
            return true;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
            return false;

        value = intValue;
        return true;
    }

    private static bool TryParseBool(string text, out bool value)
    {
        if (bool.TryParse(text, out value))
            return true;

        if (string.Equals(text, "1", StringComparison.Ordinal))
        {
            value = true;
            return true;
        }

        if (string.Equals(text, "0", StringComparison.Ordinal))
        {
            value = false;
            return true;
        }

        return false;
    }

    private static bool TryParseSelectInteger(QuasarConfigOptionDefinition option, string text, out int value)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return true;

        foreach (var choice in option.SelectOptions)
        {
            if ((!string.IsNullOrEmpty(choice.XmlName) &&
                 string.Equals(choice.XmlName, text, StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(choice.Label, text, StringComparison.OrdinalIgnoreCase))
            {
                value = choice.Value;
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, int> ReadBlockTypeLimits(XElement blockTypeLimitsElement)
    {
        var limits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var items = blockTypeLimitsElement
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "item", StringComparison.OrdinalIgnoreCase));

        foreach (var item in items)
        {
            var key = (ElementIgnoreCase(item, "Key")?.Value ?? string.Empty).Trim();
            var valueText = (ElementIgnoreCase(item, "Value")?.Value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key) ||
                !int.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit))
            {
                continue;
            }

            limits[key] = Math.Clamp(limit, 0, short.MaxValue);
        }

        return limits
            .OrderBy(limit => limit.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(limit => limit.Key, limit => limit.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static void ApplySessionSettings(XElement root, QuasarSessionSettings sessionSettings)
    {
        var settingsElement = root.Element("Settings");
        if (settingsElement is null)
        {
            settingsElement = new XElement("Settings");
            root.AddFirst(settingsElement);
        }

        foreach (var option in QuasarConfigMetadata.Options.Where(option => option.Scope == QuasarConfigOptionScope.Session))
        {
            if (string.IsNullOrWhiteSpace(option.ElementName))
                continue;
            if (option.Kind == QuasarConfigOptionKind.KeyValueText)
                continue;

            UpsertElement(settingsElement, option.ElementName, QuasarConfigMetadata.FormatValue(option, sessionSettings));
        }

        UpsertBlockTypeLimits(settingsElement, sessionSettings.BlockTypeLimits);
    }

    private static void UpsertBlockTypeLimits(XElement settingsElement, IReadOnlyDictionary<string, int> limits)
    {
        var element = settingsElement.Element("BlockTypeLimits");
        if (element is null)
        {
            element = new XElement("BlockTypeLimits");
            settingsElement.Add(element);
        }

        element.RemoveNodes();
        element.Add(
            new XElement(
                "dictionary",
                limits
                    .Where(limit => !string.IsNullOrWhiteSpace(limit.Key))
                    .Select(limit =>
                        new XElement(
                            "item",
                            new XElement("Key", limit.Key),
                            new XElement("Value", Math.Clamp(limit.Value, 0, short.MaxValue).ToString(CultureInfo.InvariantCulture))))));
    }

    private static void ApplyMods(XElement root, IReadOnlyList<QuasarModSelection> mods)
    {
        var modsElement = root.Element("Mods");
        if (modsElement is null)
        {
            modsElement = new XElement("Mods");
            var settings = root.Element("Settings");
            if (settings is not null)
                settings.AddAfterSelf(modsElement);
            else
                root.Add(modsElement);
        }
        else
        {
            modsElement.RemoveNodes();
        }

        foreach (var mod in mods)
        {
            if (mod.WorkshopId <= 0)
                continue;

            var friendlyName = string.IsNullOrWhiteSpace(mod.DisplayName)
                ? mod.WorkshopId.ToString(CultureInfo.InvariantCulture)
                : mod.DisplayName.Trim();

            var idString = mod.WorkshopId.ToString(CultureInfo.InvariantCulture);
            modsElement.Add(new XElement(
                "ModItem",
                new XAttribute("FriendlyName", friendlyName),
                new XElement("Name", $"{idString}.sbm"),
                new XElement("PublishedFileId", idString),
                new XElement("PublishedServiceName", "Steam")));
        }
    }

    private static void UpsertElement(XElement parent, string name, string value)
    {
        var element = parent.Element(name);
        if (element is null)
        {
            parent.Add(new XElement(name, value));
            return;
        }

        element.Value = value;
    }

    private static string SerializeXml(XDocument document)
    {
        using var stringWriter = new Utf8StringWriter();
        using var xmlWriter = XmlWriter.Create(stringWriter, new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            OmitXmlDeclaration = false,
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
        });

        document.Save(xmlWriter);
        xmlWriter.Flush();
        return stringWriter.ToString();
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }
}
