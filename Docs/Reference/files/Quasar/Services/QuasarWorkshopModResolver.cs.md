# Quasar/Services/QuasarWorkshopModResolver.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary
Resolves Steam Workshop mod references (URLs, raw IDs, or text containing IDs) into typed `QuasarModSelection` records via the Steam Web API. Supports resolving individual mods and expanding collections into their child items, browsing popular mods, and full-text mod search. Non-mod items (worlds, blueprints, in-game scripts) are silently filtered out.

## Structure
**Namespace:** `Quasar.Services`

**Type:** `QuasarWorkshopModResolver` (sealed class)

Constants:
- `SpaceEngineersAppId = 244850`
- `BatchSize = 100` (Steam API limit per call)
- `WorkshopIdPattern` — compiled regex that extracts 6–20 digit IDs from URLs or plain text

| Member | Description |
|---|---|
| `GetPopularModsAsync(ct)` | Calls `IPublishedFileService/QueryFiles` with the configured popular-query type and limit. |
| `SearchModsAsync(searchText, ct)` | Same endpoint with free-text search. |
| `ResolveAsync(input, ct)` | Parses workshop IDs from `input`, expands collections via `GetCollectionDetails`, fetches `GetPublishedFileDetails` in batches, filters non-SE and non-mod items, returns `QuasarWorkshopResolutionResult`. |

Private helpers:
- `QueryFilesAsync` — builds `QueryFilesRequest` JSON, calls `GetAsync`
- `GetCollectionChildrenAsync` — batched POST to `GetCollectionDetails/v1`
- `GetPublishedFileDetailsAsync` — batched POST to `GetPublishedFileDetails/v1`
- `ParseWorkshopIds` / `ExpandCandidateIds` / `Batch` / `BuildIndexedFormContent` — input parsing and batching utilities
- `IsClearlyNonMod` — rejects items tagged `world`, `blueprint`, or `ingameScript`
- `GetQueryType` / `GetMatchingFileType` — string-to-int maps for Steam enum values
- Private nested DTOs: `QueryFilesRequest`, `QueryFilesEnvelope`, `CollectionDetailsEnvelope`, `PublishedFileDetailsEnvelope`, and related items/responses

Result types (sealed records in same file):
- `QuasarWorkshopResolutionResult(Mods, Warnings)` — resolution output with warning list
- `QuasarWorkshopSearchResultSet(Mods, Total)` — search page result
- `QuasarWorkshopSearchResult(WorkshopId, Title, Description, PreviewUrl, Tags)` — single search hit

## Dependencies
- [`Quasar/Services/Auth/QuasarAuthOptions.cs`](Auth/QuasarAuthOptions.cs.md) (Workshop sub-options: enabled flag, query types, limits, required tags, appId)
- [`Quasar/Services/SteamWorkshopCredentialsCatalog.cs`](SteamWorkshopCredentialsCatalog.cs.md) (Web API key retrieval)
- `Quasar/Models/QuasarModSelection.cs`
- `Microsoft.AspNetCore.WebUtilities.QueryHelpers` (URL query string building)
- `IHttpClientFactory`

## Notes
- The Steam Web API key is required for search/popular queries but not for `GetCollectionDetails` or `GetPublishedFileDetails` (those accept anonymous POST).
- Collection expansion is one level deep; nested collections are not recursed.
- HTTP timeout is 30 seconds per request.
- `consumer_app_id` and `consumer_appid` are both handled (via a setter alias) due to Steam API inconsistency between endpoints.
