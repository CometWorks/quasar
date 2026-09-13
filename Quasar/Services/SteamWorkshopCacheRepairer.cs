using System.Globalization;
using System.Text;

namespace Quasar.Services;

internal static class SteamWorkshopCacheRepairer
{
    private const int SpaceEngineersAppId = 244850;
    private const string InstalledItemsSection = "WorkshopItemsInstalled";

    public static async Task<SteamWorkshopCacheRepairResult> RepairIfNeededAsync(
        string dedicatedServerAppDataPath,
        CancellationToken cancellationToken = default)
    {
        var manifestPath = Path.Combine(dedicatedServerAppDataPath, $"appworkshop_{SpaceEngineersAppId}.acf");
        if (!File.Exists(manifestPath))
            return SteamWorkshopCacheRepairResult.NotNeeded;

        var manifest = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        List<InstalledItemRecord> installedItems;
        try
        {
            installedItems = ParseInstalledItems(manifest);
        }
        catch (InvalidDataException exception)
        {
            var quarantinePath = QuarantineInvalidManifest(dedicatedServerAppDataPath, manifestPath);
            return new SteamWorkshopCacheRepairResult(
                true,
                true,
                quarantinePath,
                [],
                $"invalid manifest: {exception.Message}");
        }

        var contentRoot = Path.Combine(
            dedicatedServerAppDataPath,
            "content",
            SpaceEngineersAppId.ToString(CultureInfo.InvariantCulture));
        var missingItems = installedItems
            .Where(item => !HasContent(Path.Combine(contentRoot, item.ItemId.ToString(CultureInfo.InvariantCulture))))
            .ToList();
        if (missingItems.Count == 0)
            return SteamWorkshopCacheRepairResult.NotNeeded;

        var repairedManifest = new StringBuilder(manifest);
        foreach (var item in missingItems.OrderByDescending(item => item.Start))
            repairedManifest.Remove(item.Start, item.End - item.Start);

        await AtomicFileWriter.WriteTextAsync(manifestPath, repairedManifest.ToString(), cancellationToken);

        var missingItemIds = missingItems
            .Select(item => item.ItemId)
            .Distinct()
            .ToList();
        return new SteamWorkshopCacheRepairResult(
            true,
            false,
            string.Empty,
            missingItemIds,
            $"removed stale installed state for items missing content: {string.Join(", ", missingItemIds)}");
    }

    private static bool HasContent(string itemPath) =>
        Directory.Exists(itemPath) && Directory.EnumerateFileSystemEntries(itemPath).Any();

    private static string QuarantineInvalidManifest(string dedicatedServerAppDataPath, string manifestPath)
    {
        var quarantinePath = Path.Combine(
            dedicatedServerAppDataPath,
            "WorkshopCacheQuarantine",
            $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(quarantinePath);
        File.Move(manifestPath, Path.Combine(quarantinePath, Path.GetFileName(manifestPath)));

        var downloadsPath = Path.Combine(dedicatedServerAppDataPath, "downloads");
        if (Directory.Exists(downloadsPath))
            Directory.Move(downloadsPath, Path.Combine(quarantinePath, "downloads"));

        return quarantinePath;
    }

    private static List<InstalledItemRecord> ParseInstalledItems(string manifest)
    {
        var itemRecords = new List<InstalledItemRecord>();
        var awaitingSectionBrace = false;
        var inSection = false;
        var depth = 0;
        ulong? pendingItemId = null;
        var pendingItemStart = 0;

        foreach (var line in ReadLines(manifest))
        {
            var text = line.Text.Trim();
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
                if (depth == 1 && pendingItemId is null)
                    throw new InvalidDataException($"unexpected block in {InstalledItemsSection}");

                depth++;
                continue;
            }

            if (text == "}")
            {
                if (depth == 2 && pendingItemId is { } itemId)
                {
                    itemRecords.Add(new InstalledItemRecord(itemId, pendingItemStart, line.End));
                    pendingItemId = null;
                }

                depth--;
                if (depth == 0)
                {
                    if (pendingItemId is not null)
                        throw new InvalidDataException($"incomplete item in {InstalledItemsSection}");
                    return itemRecords;
                }
                if (depth < 0)
                    break;
                continue;
            }

            if (depth != 1)
                continue;
            if (pendingItemId is not null)
                throw new InvalidDataException($"expected '{{' after item {pendingItemId}");

            if (TryReadQuotedToken(text, out var itemIdText) &&
                ulong.TryParse(itemIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedItemId))
            {
                pendingItemId = parsedItemId;
                pendingItemStart = line.Start;
            }
        }

        throw new InvalidDataException($"missing or incomplete {InstalledItemsSection} section");
    }

    private static IEnumerable<ManifestLine> ReadLines(string value)
    {
        var start = 0;
        while (start < value.Length)
        {
            var newline = value.IndexOf('\n', start);
            var end = newline < 0 ? value.Length : newline + 1;
            var contentEnd = newline < 0 ? value.Length : newline;
            if (contentEnd > start && value[contentEnd - 1] == '\r')
                contentEnd--;

            yield return new ManifestLine(value[start..contentEnd], start, end);
            start = end;
        }
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

    private sealed record InstalledItemRecord(ulong ItemId, int Start, int End);

    private readonly record struct ManifestLine(string Text, int Start, int End);
}

internal sealed record SteamWorkshopCacheRepairResult(
    bool Repaired,
    bool ManifestQuarantined,
    string QuarantinePath,
    IReadOnlyList<ulong> MissingItemIds,
    string Issue)
{
    public static SteamWorkshopCacheRepairResult NotNeeded { get; } = new(false, false, string.Empty, [], string.Empty);
}
