using System.Globalization;

namespace Quasar.Services;

internal static class SteamWorkshopCacheRepairer
{
    private const int SpaceEngineersAppId = 244850;
    private const string InstalledItemsSection = "WorkshopItemsInstalled";

    public static SteamWorkshopCacheRepairResult RepairIfNeeded(string dedicatedServerAppDataPath)
    {
        var manifestPath = Path.Combine(dedicatedServerAppDataPath, $"appworkshop_{SpaceEngineersAppId}.acf");
        if (!File.Exists(manifestPath))
            return SteamWorkshopCacheRepairResult.NotNeeded;

        List<ulong> missingItemIds = [];
        string issue;
        try
        {
            var installedItemIds = ParseInstalledItemIds(File.ReadAllText(manifestPath));
            var contentRoot = Path.Combine(dedicatedServerAppDataPath, "content", SpaceEngineersAppId.ToString(CultureInfo.InvariantCulture));
            missingItemIds = installedItemIds
                .Where(itemId => !File.Exists(Path.Combine(contentRoot, itemId.ToString(CultureInfo.InvariantCulture), "metadata.mod")))
                .ToList();
            if (missingItemIds.Count == 0)
                return SteamWorkshopCacheRepairResult.NotNeeded;

            issue = $"installed items missing content: {string.Join(", ", missingItemIds)}";
        }
        catch (InvalidDataException exception)
        {
            issue = $"invalid manifest: {exception.Message}";
        }

        var quarantinePath = Path.Combine(
            dedicatedServerAppDataPath,
            "WorkshopCacheQuarantine",
            $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(quarantinePath);
        File.Move(manifestPath, Path.Combine(quarantinePath, Path.GetFileName(manifestPath)));

        var downloadsPath = Path.Combine(dedicatedServerAppDataPath, "downloads");
        if (Directory.Exists(downloadsPath))
            Directory.Move(downloadsPath, Path.Combine(quarantinePath, "downloads"));

        return new SteamWorkshopCacheRepairResult(true, quarantinePath, missingItemIds, issue);
    }

    private static List<ulong> ParseInstalledItemIds(string manifest)
    {
        using var reader = new StringReader(manifest);
        var itemIds = new List<ulong>();
        var awaitingSectionBrace = false;
        var inSection = false;
        var depth = 0;

        while (reader.ReadLine() is { } line)
        {
            var text = line.Trim();
            if (!inSection)
            {
                if (awaitingSectionBrace)
                {
                    if (text.Length == 0)
                        continue;
                    if (text != "{")
                        throw new InvalidDataException($"expected '{{' after {InstalledItemsSection}");

                    inSection = true;
                    depth = 1;
                    continue;
                }

                if (TryReadQuotedToken(text, out var token) && token == InstalledItemsSection)
                    awaitingSectionBrace = true;
                continue;
            }

            if (text == "{")
            {
                depth++;
                continue;
            }

            if (text == "}")
            {
                depth--;
                if (depth == 0)
                    return itemIds;
                if (depth < 0)
                    break;
                continue;
            }

            if (depth == 1 && TryReadQuotedToken(text, out var itemIdText) &&
                ulong.TryParse(itemIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var itemId))
            {
                itemIds.Add(itemId);
            }
        }

        throw new InvalidDataException($"missing or incomplete {InstalledItemsSection} section");
    }

    private static bool TryReadQuotedToken(string text, out string token)
    {
        token = string.Empty;
        if (text.Length < 2 || text[0] != '"')
            return false;

        var closingQuote = text.IndexOf('"', 1);
        if (closingQuote < 0)
            return false;

        token = text[1..closingQuote];
        return true;
    }
}

internal sealed record SteamWorkshopCacheRepairResult(
    bool Repaired,
    string QuarantinePath,
    IReadOnlyList<ulong> MissingItemIds,
    string Issue)
{
    public static SteamWorkshopCacheRepairResult NotNeeded { get; } = new(false, string.Empty, [], string.Empty);
}
