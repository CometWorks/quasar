# Quasar SDK transport adapter contract

SDK 3 adds binary reads, staged uploads, subscriptions and server-owned browser
session bindings to the version 1 JSON SDK. HTTP is implemented. A future peer
adapter must enforce this same boundary; a DataChannel is not an authenticated
caller by itself.

## Browser session binding

`QuasarBrowserSessions` is a singleton core service in `Quasar.Services.Plugins`.
Call `await BindHttpSessionAsync(HttpContext)` only from the existing authenticated
Quasar HTTP path after that path's antiforgery validation for state-changing
setup. Quasar's normal middleware must already have established `HttpContext.User`.
Do not construct a binding from a browser/sidecar-supplied principal or claims.

The returned `QuasarBrowserSession` is a server-owned object with `User`, `Id`,
`Owner`, `PathBase`, `ExpiresAt` and `Revoked`. The HTTP-only, protected session
cookie is user- and path-bound; neither it nor the principal is sent to a peer
process. `ExpiresAt` is the earlier of the Quasar authentication ticket expiry
and the SDK cookie's eight-hour lifetime. Explicit local trusted access without
an authentication ticket uses the SDK bound; the SDK creates no trust bypass.
Machine service principals cannot bind. Session state is bounded to 512 entries
and 32 per user, and idle entries are removed after 15 minutes when another
binding is established.

Store this exact binding under the worker-assigned peer/attempt ID. Call
`Verify(binding, token)` for each command and while a peer is idle; it checks
expiry, revocation and the registered object identity and refreshes idle activity.
Observe `binding.Revoked` continuously. `RevokeAsync(HttpContext)` cancels the
binding and its transfer tokens; Quasar calls it during its existing validated
logout flow before cookie sign-out. Application disposal also cancels bindings.
Closing one view/peer should cancel that peer's resources, not revoke the shared
browser binding used by other views.

This SDK binding does not grant Nebula hosting entitlement. A hosted peer adapter
must additionally bind and renew its hosting lease and cancel on lease expiry,
credential replacement or worker shutdown. Keep that authorization separate from
the Quasar principal and operation rules.

## Operation invocation

Registrations are `AddQuasarOperation<TRequest,TResponse>`,
`AddQuasarRead<TRequest>`, `AddQuasarUpload<TRequest,TResponse>` and
`AddQuasarSubscription<TRequest,TItem>` in
`Quasar.Plugin.Abstractions.Operations`. `QuasarOperationKind` identifies Json,
Read, Upload or Subscription. `QuasarOperationService.Find` exposes the registered
kind and maximum upload bytes. Unknown kinds/operations are not proxy targets.

Enforce `QuasarSdkLimits.MaximumJsonBytes` (65,536 UTF-8 bytes) before parsing
control frames and `MaximumJsonDepth` (32) while parsing. Select the stored
Quasar binding from the worker-owned peer ID, never from a frame's role/SteamID.
Use a valid request service scope for the full invocation/resource lifetime.

Call `CheckAccessAsync(binding.User, pluginId, operationId, kind)` before reserving
a resource. A null return means allowed; an error result carries status/detail.
Then call `DispatchAsync(new QuasarOperationContext(binding.User, services)
{ Kind = kind, Upload = stagedStreamIfApplicable }, pluginId, operationId,
request, token)`. Dispatch refreshes runtime roles and checks current effective
policies; it supplies `context.EnsureAuthorizedAsync(token)` to handlers for a
fresh check before side effects. Errors use the result's status/detail; do not
serialize stream definitions as JSON.

JSON handlers receive a cooperative 30-second deadline. They must honor
cancellation. Disconnect/deadline does not prove a write failed; never replay a
mutating operation unless its feature has a separate idempotency contract.

## Resource lifecycle

`QuasarTransferRegistry.Create(binding, plugin, operation, kind, request)` reserves
a single-use lease after authorization. `Get(binding, id, claim: true)` claims it.
Limits are four resources per browser session, 16 per user and 64 per worker.
Unused leases expire in one minute; claimed leases in ten minutes. A transfer's
`Token` is also linked to browser expiry/logout/revocation. A transfer's ID is not
a bearer credential: the same binding is required to access it.

Combine that token with peer-disconnect, application-stop and hosting-lease
cancellation. Always `Remove(transfer)` in a finally block; `Cancel(transfer)`
stops it explicitly. Do not free its concurrency slot while its task still runs.

`QuasarResourceLifetime.WatchAuthorizationAsync(check, stopSource)` runs a supplied
check every `QuasarSdkLimits.AuthorizationRecheckInterval` (one second). The
check must call session verification, operation authorization and any feature
validation. Denial cancels `stopSource`; unexpected failure also cancels and is
reported by the returned task. Stop and await that task during cleanup. Check
again before work starts and before protected side effects. A stalled network
writer must be cancelled/aborted too; checking only on the next incoming message
is insufficient.

The HTTP implementation is the executable example in `QuasarHttpOperations`.
Its details are an adapter, not a requirement to proxy localhost HTTP from a peer.

### Reads

Dispatch returns `QuasarRead`: deferred `OpenAsync`, content type, optional name,
length, modification time and ETag, `EnableRanges`, and optional `ValidateAsync`.
Call feature validation before opening and throughout transfer, alongside core
authorization. Open/dispose the stream with the combined cancellation token.
Preserve byte/content metadata, implement supported ranges or explicitly reject
unsupported ones, and bound queued bytes. HTTP uses native ASP.NET file streaming
and a 64 KiB copy buffer; it rejects ranges on non-range resources.

### Uploads

Call `await QuasarUploadStaging.StageAsync(incomingStream,
registeredOperation.MaximumUploadBytes, token)` and retain/dispose the resulting
FileStream. This uses a private SDK staging file, a 64 KiB copy buffer and the
registered maximum even when the incoming length is unknown. Cancellation,
oversize or failed staging deletes the partial file. Completed files are
rewound. Delete-on-close removes normal staging; old crash debris is removed when
a later upload starts after it is older than one hour.

Only after complete staging, dispatch with `context.Upload = staged`. The
registered handler prepares/validates a `QuasarUpload<TResponse>` commit callback;
the SDK rechecks authorization immediately before calling it. The feature owns
atomic application and its own resource/consent checks. Always dispose staging
after success or failure; no raw destination path or credential comes from the
transport. An adapter must not substitute a live unbounded incoming stream for
the completed staged stream.

### Subscriptions

Dispatch returns `QuasarSubscription.Items`, an `IAsyncEnumerable<object?>`.
Enumerate with cancellation and pull-driven backpressure. Dispose enumeration on
view close, disconnect, permission loss or lifetime expiry. HTTP writes NDJSON
without a producer queue and caps each serialized item at 64 KiB/depth 32. A peer
adapter may use its own framing while preserving these limits and lifecycle.

## HTTP and browser surface

`POST /_quasar/sdk/v1/resources/<plugin>/<operation>` reserves a resource using
bounded JSON and CSRF protection. It returns an ID, kind and same-panel URL.
GET claims reads/subscriptions; PUT claims uploads with CSRF protection; DELETE
cancels an owned resource with CSRF protection. All responses are private no-store.
Legacy adopted routes use `IQuasarHttpOperations` and `MapQuasarOperation` so they
share policies/handlers rather than introducing another authorization path.

The browser client provides `request`, `openRead`, `openWrite`, `subscribe`,
`download` and `dispose`. Read bodies are Web ReadableStreams; upload accepts
native Blob/File bodies or supported request streams. Consumers dispose their
handles; client/page teardown cancels its owned work. Subscriptions are async
iterables. Native downloads are started through the SDK-owned URL without
buffering the whole file into JavaScript. No automatic mutation retry occurs.

Focused checks cover ranges, incremental read bounds, control responsiveness,
session separation and limits, interrupted/oversized upload cleanup, authorization
revocation, native cookie expiry binding and logout token cancellation. Viewer migration is implemented in SDK 4; core consumers follow in SDK 5.
