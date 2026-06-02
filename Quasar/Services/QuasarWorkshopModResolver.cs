using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Quasar.Models;
using Quasar.Services.Auth;

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

    private readonly QuasarAuthOptions _authOptions;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<QuasarWorkshopModResolver> _logger;

    public QuasarWorkshopModResolver(
        QuasarAuthOptions authOptions,
        IHttpClientFactory httpClientFactory,
        ILogger<QuasarWorkshopModResolver> logger)
    {
        _authOptions = authOptions;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Task<QuasarWorkshopSearchResultSet> GetPopularModsAsync(CancellationToken cancellationToken = default) =>
        QueryFilesAsync(
            searchText: string.Empty,
            queryType: GetQueryType(_authOptions.Workshop.PopularQueryType),
            limit: _authOptions.Workshop.PopularLimit,
            cancellationToken);

    public Task<QuasarWorkshopSearchResultSet> SearchModsAsync(string searchText, CancellationToken cancellationToken = default) =>
        QueryFilesAsync(
            searchText: searchText,
            queryType: GetQueryType(_authOptions.Workshop.SearchQueryType),
            limit: _authOptions.Workshop.SearchLimit,
            cancellationToken);

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

    private async Task<QuasarWorkshopSearchResultSet> QueryFilesAsync(
        string searchText,
        int queryType,
        int limit,
        CancellationToken cancellationToken)
    {
        var options = _authOptions.Workshop;
        if (!options.Enabled)
            throw new InvalidOperationException("Steam Workshop search is disabled.");

        if (string.IsNullOrWhiteSpace(options.WebApiKey))
            throw new InvalidOperationException("Steam Workshop Web API key required for search.");

        var request = new QueryFilesRequest
        {
            QueryType = queryType,
            Cursor = "*",
            NumPerPage = Math.Clamp(limit, 1, 50),
            CreatorAppId = options.AppId,
            AppId = options.AppId,
            RequiredTags = string.Join(",", options.RequiredTags),
            MatchAllTags = true,
            SearchText = searchText.Trim(),
            FileType = GetMatchingFileType(options.MatchingFileType),
            CacheMaxAgeSeconds = options.CacheMaxAgeSeconds,
            ReturnTags = true,
            ReturnPreviews = true,
            ReturnShortDescription = true,
        };

        var url = QueryHelpers.AddQueryString(
            "https://api.steampowered.com/IPublishedFileService/QueryFiles/v1/",
            new Dictionary<string, string?>
            {
                ["key"] = options.WebApiKey,
                ["input_json"] = JsonSerializer.Serialize(request, JsonOptions),
            });

        var payload = await GetAsync<QueryFilesEnvelope>(url, cancellationToken);
        var response = payload.Response;
        var results = (response?.PublishedFileDetails ?? [])
            .Where(detail => detail.Result == 1)
            .Where(detail => detail.ConsumerAppId == 0 || detail.ConsumerAppId == options.AppId)
            .Where(detail => !IsClearlyNonMod(detail))
            .Select(ToSearchResult)
            .Where(result => result.WorkshopId > 0)
            .DistinctBy(result => result.WorkshopId)
            .Take(request.NumPerPage)
            .ToList();

        return new QuasarWorkshopSearchResultSet(results, response?.Total ?? results.Count);
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

    private async Task<T> GetAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Quasar/1.0");

        using var response = await client.GetAsync(url, cancellationToken);
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

    private static string GetDescription(PublishedFileDetailsItem detail) =>
        string.IsNullOrWhiteSpace(detail.ShortDescription)
            ? string.Empty
            : detail.ShortDescription.Trim();

    private static QuasarWorkshopSearchResult ToSearchResult(PublishedFileDetailsItem detail)
    {
        return new QuasarWorkshopSearchResult(
            TryParseWorkshopId(detail.PublishedFileId, out var workshopId) ? workshopId : 0,
            GetDisplayName(detail),
            GetDescription(detail),
            string.IsNullOrWhiteSpace(detail.PreviewUrl) ? string.Empty : detail.PreviewUrl.Trim(),
            detail.Tags
                .Select(tag => tag.Tag?.Trim() ?? string.Empty)
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

    private static int GetQueryType(string queryType) =>
        queryType.Trim() switch
        {
            "RankedByVote" => 0,
            "RankedByPublicationDate" => 1,
            "AcceptedForGameRankedByAcceptanceDate" => 2,
            "RankedByTrend" => 3,
            "FavoritedByFriendsRankedByPublicationDate" => 4,
            "CreatedByFriendsRankedByPublicationDate" => 5,
            "RankedByNumTimesReported" => 6,
            "CreatedByFollowedUsersRankedByPublicationDate" => 7,
            "NotYetRated" => 8,
            "RankedByTotalUniqueSubscriptions" => 9,
            "RankedByTotalVotesAsc" => 10,
            "RankedByVotesUp" => 11,
            "RankedByTextSearch" => 12,
            "RankedByPlaytimeTrend" => 13,
            "RankedByTotalPlaytime" => 14,
            "RankedByAveragePlaytimeTrend" => 15,
            "RankedByLifetimeAveragePlaytime" => 16,
            "RankedByPlaytimeSessionsTrend" => 17,
            "RankedByLifetimePlaytimeSessions" => 18,
            "RankedByInappropriateContentRating" => 19,
            "RankedByBanContentCheck" => 20,
            "RankedByLastUpdatedDate" => 21,
            _ => 9,
        };

    private static int GetMatchingFileType(string fileType) =>
        fileType.Trim() switch
        {
            "Collections" => 1,
            "Art" => 2,
            "Videos" => 3,
            "Screenshots" => 4,
            "CollectionEligible" => 5,
            "UsableInGame" => 13,
            "ItemsMtx" => 17,
            "ItemsReadyToUse" => 18,
            "GameManagedItems" => 20,
            _ => 0,
        };

    private sealed class QueryFilesRequest
    {
        [JsonPropertyName("query_type")]
        public int QueryType { get; set; }

        [JsonPropertyName("cursor")]
        public string Cursor { get; set; } = "*";

        [JsonPropertyName("numperpage")]
        public int NumPerPage { get; set; }

        [JsonPropertyName("creator_appid")]
        public int CreatorAppId { get; set; }

        [JsonPropertyName("appid")]
        public int AppId { get; set; }

        [JsonPropertyName("requiredtags")]
        public string RequiredTags { get; set; } = string.Empty;

        [JsonPropertyName("match_all_tags")]
        public bool MatchAllTags { get; set; }

        [JsonPropertyName("search_text")]
        public string SearchText { get; set; } = string.Empty;

        [JsonPropertyName("filetype")]
        public int FileType { get; set; }

        [JsonPropertyName("cache_max_age_seconds")]
        public int CacheMaxAgeSeconds { get; set; }

        [JsonPropertyName("return_tags")]
        public bool ReturnTags { get; set; }

        [JsonPropertyName("return_previews")]
        public bool ReturnPreviews { get; set; }

        [JsonPropertyName("return_short_description")]
        public bool ReturnShortDescription { get; set; }
    }

    private sealed class QueryFilesEnvelope
    {
        [JsonPropertyName("response")]
        public QueryFilesResponse? Response { get; set; }
    }

    private sealed class QueryFilesResponse
    {
        [JsonPropertyName("total")]
        public int Total { get; set; }

        [JsonPropertyName("publishedfiledetails")]
        public List<PublishedFileDetailsItem> PublishedFileDetails { get; set; } = [];
    }

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

        [JsonPropertyName("consumer_appid")]
        public int ConsumerAppIdAlternate
        {
            set
            {
                if (value > 0)
                    ConsumerAppId = value;
            }
        }

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("short_description")]
        public string ShortDescription { get; set; } = string.Empty;

        [JsonPropertyName("preview_url")]
        public string PreviewUrl { get; set; } = string.Empty;

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

public sealed record QuasarWorkshopSearchResultSet(
    IReadOnlyList<QuasarWorkshopSearchResult> Mods,
    int Total);

public sealed record QuasarWorkshopSearchResult(
    long WorkshopId,
    string Title,
    string Description,
    string PreviewUrl,
    IReadOnlyList<string> Tags);
