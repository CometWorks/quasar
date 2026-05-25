using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Quasar.Models;

namespace Quasar.Services;

public sealed class QuasarWorkshopModResolver
{
    public const int SpaceEngineersAppId = 244850;

    private const int BatchSize = 100;
    private static readonly Regex WorkshopIdPattern = new(@"(?:(?:[?&]id=)|\b)(\d{6,20})\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<QuasarWorkshopModResolver> _logger;

    public QuasarWorkshopModResolver(
        IHttpClientFactory httpClientFactory,
        ILogger<QuasarWorkshopModResolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<QuasarWorkshopResolutionResult> ResolveAsync(string input, CancellationToken cancellationToken = default)
    {
        var candidateIds = ParseWorkshopIds(input);
        if (candidateIds.Count == 0)
            throw new InvalidOperationException("No Steam Workshop IDs found in input.");

        var warnings = new List<string>();
        var collectionChildren = await GetCollectionChildrenAsync(candidateIds, cancellationToken);
        var expandedIds = ExpandCandidateIds(candidateIds, collectionChildren);
        var detailsById = await GetPublishedFileDetailsAsync(expandedIds, cancellationToken);

        var mods = new List<QuasarModSelection>();
        foreach (var workshopId in expandedIds)
        {
            if (!detailsById.TryGetValue(workshopId, out var detail) || detail.Result != 1)
            {
                warnings.Add($"Workshop item {workshopId} not found.");
                continue;
            }

            if (detail.ConsumerAppId != SpaceEngineersAppId)
            {
                warnings.Add($"Skipped {GetDisplayName(detail)} ({workshopId}): not Space Engineers content.");
                continue;
            }

            if (IsClearlyNonMod(detail))
            {
                warnings.Add($"Skipped {GetDisplayName(detail)} ({workshopId}): workshop item is not a mod.");
                continue;
            }

            mods.Add(new QuasarModSelection
            {
                WorkshopId = workshopId,
                DisplayName = GetDisplayName(detail),
            });
        }

        _logger.LogInformation(
            "Resolved workshop input into {ModCount} mod entries with {WarningCount} warnings.",
            mods.Count,
            warnings.Count);

        return new QuasarWorkshopResolutionResult(mods, warnings);
    }

    private async Task<Dictionary<long, List<long>>> GetCollectionChildrenAsync(
        IReadOnlyList<long> workshopIds,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<long, List<long>>();
        foreach (var batch in Batch(workshopIds))
        {
            var content = BuildIndexedFormContent("collectioncount", batch, "publishedfileids");
            var payload = await PostAsync<CollectionDetailsEnvelope>(
                "https://api.steampowered.com/ISteamRemoteStorage/GetCollectionDetails/v1/",
                content,
                cancellationToken);

            foreach (var item in payload.Response?.CollectionDetails ?? [])
            {
                if (!TryParseWorkshopId(item.PublishedFileId, out var id))
                    continue;

                var children = (item.Children ?? [])
                    .OrderBy(child => child.SortOrder)
                    .Select(child => TryParseWorkshopId(child.PublishedFileId, out var childId) ? childId : 0L)
                    .Where(childId => childId > 0)
                    .ToList();

                if (item.Result == 1 && children.Count > 0)
                    results[id] = children;
            }
        }

        return results;
    }

    private async Task<Dictionary<long, PublishedFileDetailsItem>> GetPublishedFileDetailsAsync(
        IReadOnlyList<long> workshopIds,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<long, PublishedFileDetailsItem>();
        foreach (var batch in Batch(workshopIds))
        {
            var content = BuildIndexedFormContent("itemcount", batch, "publishedfileids");
            var payload = await PostAsync<PublishedFileDetailsEnvelope>(
                "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/",
                content,
                cancellationToken);

            foreach (var item in payload.Response?.PublishedFileDetails ?? [])
            {
                if (TryParseWorkshopId(item.PublishedFileId, out var id))
                    results[id] = item;
            }
        }

        return results;
    }

    private async Task<T> PostAsync<T>(
        string url,
        IReadOnlyList<KeyValuePair<string, string>> formData,
        CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Quasar/1.0");

        using var content = new FormUrlEncodedContent(formData);
        using var response = await client.PostAsync(url, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
        return payload ?? throw new InvalidOperationException($"Steam Workshop returned no payload for {url}.");
    }

    private static List<long> ParseWorkshopIds(string input)
    {
        var ids = new List<long>();
        var seen = new HashSet<long>();
        foreach (Match match in WorkshopIdPattern.Matches(input ?? string.Empty))
        {
            if (!TryParseWorkshopId(match.Groups[1].Value, out var workshopId))
                continue;

            if (seen.Add(workshopId))
                ids.Add(workshopId);
        }

        return ids;
    }

    private static List<long> ExpandCandidateIds(
        IReadOnlyList<long> candidateIds,
        IReadOnlyDictionary<long, List<long>> collectionChildren)
    {
        var expanded = new List<long>();
        var seen = new HashSet<long>();
        foreach (var candidateId in candidateIds)
        {
            if (collectionChildren.TryGetValue(candidateId, out var children))
            {
                foreach (var childId in children)
                {
                    if (seen.Add(childId))
                        expanded.Add(childId);
                }

                continue;
            }

            if (seen.Add(candidateId))
                expanded.Add(candidateId);
        }

        return expanded;
    }

    private static IReadOnlyList<List<long>> Batch(IReadOnlyList<long> source)
    {
        var batches = new List<List<long>>();
        for (var index = 0; index < source.Count; index += BatchSize)
            batches.Add(source.Skip(index).Take(BatchSize).ToList());

        return batches;
    }

    private static List<KeyValuePair<string, string>> BuildIndexedFormContent(
        string countKey,
        IReadOnlyList<long> values,
        string prefix)
    {
        var formData = new List<KeyValuePair<string, string>>
        {
            new(countKey, values.Count.ToString())
        };

        for (var index = 0; index < values.Count; index++)
            formData.Add(new KeyValuePair<string, string>($"{prefix}[{index}]", values[index].ToString()));

        return formData;
    }

    private static bool TryParseWorkshopId(string value, out long workshopId)
    {
        if (long.TryParse(value, out workshopId) && workshopId > 0)
            return true;

        workshopId = 0;
        return false;
    }

    private static bool IsClearlyNonMod(PublishedFileDetailsItem detail)
    {
        var tags = detail.Tags?
            .Select(tag => tag.Tag?.Trim() ?? string.Empty)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (tags is null || tags.Count == 0)
            return false;

        return tags.Contains("world") || tags.Contains("blueprint") || tags.Contains("ingameScript");
    }

    private static string GetDisplayName(PublishedFileDetailsItem detail) =>
        string.IsNullOrWhiteSpace(detail.Title)
            ? detail.PublishedFileId
            : detail.Title.Trim();

    private sealed class CollectionDetailsEnvelope
    {
        [JsonPropertyName("response")]
        public CollectionDetailsResponse? Response { get; set; }
    }

    private sealed class CollectionDetailsResponse
    {
        [JsonPropertyName("collectiondetails")]
        public List<CollectionDetailsItem> CollectionDetails { get; set; } = [];
    }

    private sealed class CollectionDetailsItem
    {
        [JsonPropertyName("publishedfileid")]
        public string PublishedFileId { get; set; } = string.Empty;

        [JsonPropertyName("result")]
        public int Result { get; set; }

        [JsonPropertyName("children")]
        public List<CollectionChildItem> Children { get; set; } = [];
    }

    private sealed class CollectionChildItem
    {
        [JsonPropertyName("publishedfileid")]
        public string PublishedFileId { get; set; } = string.Empty;

        [JsonPropertyName("sortorder")]
        public int SortOrder { get; set; }
    }

    private sealed class PublishedFileDetailsEnvelope
    {
        [JsonPropertyName("response")]
        public PublishedFileDetailsResponse? Response { get; set; }
    }

    private sealed class PublishedFileDetailsResponse
    {
        [JsonPropertyName("publishedfiledetails")]
        public List<PublishedFileDetailsItem> PublishedFileDetails { get; set; } = [];
    }

    private sealed class PublishedFileDetailsItem
    {
        [JsonPropertyName("publishedfileid")]
        public string PublishedFileId { get; set; } = string.Empty;

        [JsonPropertyName("result")]
        public int Result { get; set; }

        [JsonPropertyName("consumer_app_id")]
        public int ConsumerAppId { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("tags")]
        public List<PublishedFileTag> Tags { get; set; } = [];
    }

    private sealed class PublishedFileTag
    {
        [JsonPropertyName("tag")]
        public string Tag { get; set; } = string.Empty;
    }
}

public sealed record QuasarWorkshopResolutionResult(
    IReadOnlyList<QuasarModSelection> Mods,
    IReadOnlyList<string> Warnings);
