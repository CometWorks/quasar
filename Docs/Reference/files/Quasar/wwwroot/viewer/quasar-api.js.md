# Quasar/wwwroot/viewer/quasar-api.js

**Module:** Quasar.Host  **Kind:** JS  **Tier:** 3

## Summary
HTTP helper for the standalone grid viewer. It reads the `agentId` and `entityId` query parameters and fetches the metadata-only scene JSON from Quasar's viewer API.

## Structure

| Export | Purpose |
|---|---|
| `getViewerParams()` | Parses and validates `agentId`/`entityId` from `window.location.search`. |
| `fetchEntityScene()` | Performs a same-origin JSON fetch to `/api/viewer/entities/{agentId}/{entityId}/scene` and unwraps problem details on failure. |

## Dependencies
- Browser `URLSearchParams` and `fetch` APIs.
- The Quasar viewer scene endpoint implemented by the web host.
