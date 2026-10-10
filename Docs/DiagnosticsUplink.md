# Diagnostic consent and central upload

Quasar 1.2.0 introduces this diagnostics framework. Release packaging appends the build number to the base version.

Quasar is the consent authority and the only process that connects to the diagnostic backoffice. Every enrolled Host executor participates, including hosts shared by clusters and hosts added after setup. Existing authenticated reverse tunnels and host credentials carry policy and encrypted archives; cluster topology does not select or exclude hosts.

## Consent

Security exposes three independent choices:

- **Plugin usage statistics:** the existing YES/NO decision passed to Magnetar at its next start. Existing callers and stored decisions keep their meaning.
- **Diagnostic reports:** off by default. Covers error details, bounded recent logs, software versions and execution context. Logs may contain private player or server information.
- **Crash dumps:** off by default and requires diagnostic reports. Dumps may contain credentials, player data, world content and other process memory.

This update resets all previous consent decisions and reprompts administrators under policy version 3. Accepting YES enables statistics and diagnostic reporting and starts automatic enrollment; it does not enable dumps or public listings. Old pending evidence cannot enter the new consent window. Policy version 3 records a monotonic generation and UTC cutoff. Category changes advance the generation and reset the cutoff, preventing earlier incidents and queued archives from becoming eligible after a later opt-in. Changing only statistics preserves the diagnostic choices and cutoff.

Consent changes cancel active transfers, invalidate the local collector lease and purge outdated outbound archives immediately. Hosts receive renewable two-minute leases every 30 seconds through their existing authenticated connection. Renewal runs independently of transfers. An offline host cannot keep sharing indefinitely. Already delivered reports cannot be recalled. Memory dump environment settings for an existing process take effect on its next start; consent still gates packaging and upload of any dump already captured locally.

## Configuration

Configure Quasar with these keys, or their equivalent environment variables:

| Configuration key | Environment variable | Purpose |
| --- | --- | --- |
| `Quasar:Diagnostics:BackofficeUrl` | `Quasar__Diagnostics__BackofficeUrl` | Backoffice base URL, default `https://log.se-servers.net/`. HTTPS is required, except HTTP on loopback for local development. |
| `Quasar:Diagnostics:UploadToken` | `Quasar__Diagnostics__UploadToken` | Optional legacy credential; omit for automatic enrollment. |
| `Quasar:Diagnostics:StorageDirectory` | `Quasar__Diagnostics__StorageDirectory` or `QUASAR_DIAGNOSTICS_DIRECTORY` | Optional absolute local directory for captured logs, dumps and encrypted queues. The `QUASAR_` alias takes precedence. |

The default backoffice is `https://log.se-servers.net/`. It verifies delivery and pushes signed visibility updates to `https://se-servers.net`. Listing visibility is a separate Security opt-in; statistics-sharing consent is not required to list.

The uplink uses its default URL when none is supplied; an explicitly empty or invalid URL disables it. It negotiates a credential automatically after diagnostic consent. URLs containing credentials, query strings or fragments are rejected. Key and upload requests never follow redirects. Logs contain failure types and queue counts, not bearer credentials or archive plaintext.

Quasar persists its random installation identity at `Quasar/Diagnostics/installation-id` under its application data directory. Preserve that directory across upgrades. It contains no user account identity. After consent, Quasar creates `machine-key.pem` in that private state directory, signs enrollment with its 3072-bit RSA key, and decrypts the returned upload credential in memory. The backend binds the identity to that key, so restart re-enrollment preserves authentication and another key cannot take it over. Preserve both files across upgrades; the machine private key never leaves Quasar. The backoffice receives the identity in `X-Installation-Id`.

## Storage on a separate disk

Set `QUASAR_DIAGNOSTICS_DIRECTORY=/mnt/diagnostics/quasar` for both Bootstrap and a directly launched Quasar worker. Bootstrap also passes its resolved directory to the worker, so launch-time crash evidence and worker logging use the same location. `runs/` contains process logs, log batches and dumps; `incoming/` contains sealed evidence; `outbound/` contains Quasar's central-upload spool. The existing application-state directory retains installation identity and consent/lease state. An unset setting preserves the existing layout. Relative Quasar storage overrides and filesystem roots are rejected.

For each remote Host, set `diagnosticsDirectory` in its local executor configuration or use `QUASAR_HOST_DIAGNOSTICS_DIRECTORY` in its service environment:

```json
{
  "stateDirectory": "/var/lib/quasar-host",
  "diagnosticsDirectory": "/mnt/diagnostics/host-a"
}
```

These are additions to the normal Host configuration, which still requires its identity, attachments and connection settings. A relative Host JSON path resolves against its configuration file directory. The Host environment override takes precedence. Host credentials and persisted lease/revocation state remain under `stateDirectory`; process logs, batches, native/CoreCLR dumps and sealed evidence use the selected directory. Quasar configures the plugin's per-run directory and policy location automatically. Use a distinct directory per Quasar/Host instance.

Quasar's ordinary application files already support `QUASAR_LOG_DIR` (or `Quasar:Logging:Directory`), independently of diagnostic capture. For example, set `QUASAR_LOG_DIR=/mnt/diagnostics/quasar/application` to place `quasar.log` and `discord.log` on the same disk. The diagnostic archive/run quotas do not govern these application files.

Paths require private local storage writable by the service account (0700 on Unix; restricted service-account ACLs on Windows). Mount the disk before starting services. The collectors check free space on the selected mount; existing retention, queue quotas, consent generations and dump grants still apply. See [Docker deployment](Docker.md) for the optional separate-volume Compose override and the backend README for `DIAGNOSTICS_LOG_DIRECTORY`.

Changing a directory does not move evidence automatically. Stop the launcher, worker/Host and their managed children, copy `runs/`, `incoming/` and, for Quasar, `outbound/` to the new private directory, preserving contents and permissions, then configure and restart. Keep `policy.json`, `installation-id`, consent and credentials in their original state locations. Leave the old evidence copy intact until verification succeeds; it is no longer polled after the switch. Moving storage must not reset consent or installation identity.

## Durable transfer

The collector seals each report and its bounded attachments into an encrypted ZIP before transfer. A public backoffice key wraps its random archive password. Quasar has no decryption key.

Quasar polls the report inventory from each enrolled host, checks the consent generation and dump permission, streams the archive into `Diagnostics/outbound`, and verifies its declared length and SHA-256. Only after both archive and envelope have been durably persisted does Quasar acknowledge the source. Local standalone reports use the same durable handover.

Uploads use `PUT /v1/reports/{reportId}` with the original report ID and `application/octet-stream` content type. Failed or interrupted deliveries remain queued; retries use the same ID and envelope. The archive is deleted locally only after successful backoffice acknowledgment. Report-specific backend rejections (400, 409, 413, 415, 422) and capacity rejection (507) retain that report for retry while allowing later queued reports to proceed. Authentication failures, backend outages and ingress backpressure stop the current upload pass; normal retention still bounds rejected evidence. The outbound spool is limited to 128 reports, 8 GiB and seven days. When full, source hosts retain unacknowledged archives for a later poll. Invalidated generations are removed on startup as well as on live consent changes. A malformed or corrupt source report is never acknowledged and does not block that source's later reports; inventory also omits envelopes whose filename, archive presence or length disagree.

Quasar retrieves the public key over its validated backoffice connection using `GET /v1/key`; keys must have a matching fingerprint, at least 3072 RSA bits and a valid expiry. A missing or invalid key suspends new sharing leases. The envelope travels in the bounded base64 `X-Report-Envelope` header; the request body is the unchanged encrypted ZIP.

## Verification

`DiagnosticsUplinkTests` covers legacy consent migration, external revocation and persistence, statistics-only updates, URL restrictions, multiple enrolled hosts, durable acknowledgment, failed backend delivery with the same report ID, cancellation on revocation, and corrupt download rejection. Run these tests through the normal Quasar test project with the current Magnetar PluginSdk available; no Quasar web service process is launched.

## Quasar process errors

Quasar registers an additional logging provider for Error and Critical messages, plus ordinary application logs while a diagnostic consent lease is active. It creates a collector run for its own process, preserving its executable hash, process identity and version so restart recovery can correlate an unexpected disappearance. Capture is local and independent of upload consent. Structured log state, scopes, configuration and environment variables are not serialized.

Each run retains at most 256 incident JSON files and accepts at most 32 ordinary error reports per minute. Fatal exceptions bypass the rate limit and evict an old incident if necessary, preserving run metadata. Reports truncate messages and exception text, use durable temporary-file publication, and isolate storage and formatting failures from the application. Recursive logging from a formatter is ignored.

The process unhandled-exception handler and top-level startup/runtime catch persist fatal evidence. Normal application stopping marks the run as expected; fatal evidence is still persisted if shutdown itself fails, including after logging providers have been disposed. Abrupt termination remains recoverable through the collector's persisted process identity. When Bootstrap supplies a valid self-run directory, Quasar adopts that run after checking its location and process identity, preserving dump configuration applied before startup. A direct launch creates its own local run; native/CoreCLR dump configuration still requires a launcher to set it before process startup.

## Matching release symbols

Linux and Windows packaging now produce `quasar-symbols-linux-x64.zip` and `quasar-symbols-win-x64.zip` alongside the runtime archives. The release workflow uploads both symbol archives and includes them in `SHA256SUMS`. Packaging requires Python 3.11 or newer; CI installs Python 3.12.

The package scripts request portable PDBs while keeping optimization enabled for Bootstrap, the worker and Host. `scripts/package-symbols.py` reads the modules and PDBs from those same builds before any later build can replace them. It verifies each portable PDB's identifier against the managed DLL's CodeView record and fails packaging when required symbols are missing or mismatched. Runtime archives exclude PDBs, and PDBs are not embedded into the single-file executables.

Each symbol archive contains the matching managed DLLs and PDBs grouped under `bootstrap/`, `worker/` and `host/`, plus `manifest.json`. The versioned manifest records the source commit and dirty-worktree flag, release version, runtime, build configuration, final published executable hashes, and each managed module/PDB hash and portable PDB identifier. The managed DLL hash identifies the module bundled into the single-file executable; the final executable hash separately identifies the distributed worker, Host or launcher. Use both mappings when selecting symbols for a crash. Keep the matching symbol archive with any release retained for production debugging.

Full Linux Release packaging and Windows Release cross-publishing from Linux both passed with the current diagnostics package and SDK. Both sets of archive checksums, symbol/module hashes and published executable hashes were verified; runtime archives exclude PDBs. The published self-contained Linux Host passed its self-test. Bash, PowerShell, XML and workflow YAML parsed successfully. Windows packaging success does not verify Windows runtime behavior; native crash capture still needs execution on a Windows runner.

## Host capture and native evidence

The existing Quasar.Host executor is the remote collector; no separate agent or public field endpoint is needed. Its diagnostic policy/list/download/ack routes use the existing bearer-authenticated Host connection and run outside the cluster execution gate. Native and managed capture records have a stable run ID, PID, executable hash, Host/node/cluster identity and launch attempt. Linux additionally records kernel boot ID and process start ticks, preserving ownership across clock changes and PID reuse. Administrative stops and normal worker drain are marked expected; readiness timeouts emit semantic incidents. Host fatal errors are captured locally too.

Local capture continues without sharing consent. Finished run evidence is retained for seven days with an 8 GiB cleanup target; active process evidence is retained until its process exits. The sealed Host queue allows 256 archives and 8 GiB; Quasar's outbound queue has its own 128-report/8 GiB bound. Collection pauses before packaging when disk free space cannot cover the estimated archive plus a 1 GiB reserve. Full runtime dumps can exceed these limits: such evidence stays local and the report explicitly records that its dump exceeds the supported archive size.

A current dump grant at launch configures CoreCLR's full single-file-compatible createdump into the private run directory. Only completed process-exit evidence includes memory attachments, and only when its launch dump generation still matches current consent. A later opt-in never enables forwarding old dumps. Old runs spanning a new consent cutoff omit their process log snapshots. Unknown-time process deaths recovered after a later grant are suppressed rather than retimestamped as new incidents.

Linux native processes use the installed `coredumpctl`/systemd collector. Host imports only a matching PID, boot ID, executable and launch/exit time window. Imports have a two-minute deadline and 2 GiB bound; missing OS support, permissions, identity, disk space and oversized cores produce an explicit unavailable status. Cluster's Python gateway supervisor records each actual Rust child separately, with bounded durable stdout/stderr. Configure systemd core storage quotas and service-account read access on deployment machines. The framework does not alter global `core_pattern`, install OS collectors or enable machine-wide Windows WER/ProcDump settings. Inherited dump settings remain fixed for a running process, and OS piped-core policy can ignore RLIMIT_CORE; consent withdrawal still prevents packaging and transfer immediately (or at lease expiry on disconnected Hosts).

On x64 Windows, the launcher attaches a background native debugger to each child launched with a current dump grant. First-chance application exceptions pass through normally; an unhandled second-chance exception produces a full DbgHelp minidump while the process is paused, then normal exception dispatch resumes. The report records the native exception code, fault address and crashing thread; the dump includes an exception stream with the faulting thread's AMD64 register context, all thread states, full memory, memory information and unloaded modules. WOW64 targets are rejected. Attachment follows process creation, so crashes before attachment, including first-instruction startup failures, have best-effort coverage. DbgHelp calls are serialized, capture has a two-minute cancellation deadline and 2 GiB size check, and insufficient disk reserve produces explicit unavailable status. The monitor detaches on consent expiry/withdrawal and configures debugger exit to leave surviving children running. Another debugger or insufficient process permissions can prevent attachment. Self-capture and attaching retrospectively to recovered children are not supported; Bootstrap can monitor the Quasar worker, while a directly launched Host needs an external launcher for its own native dump.

`Tests/NativeCapture/NativeCapture.csproj` provides a real Windows noncontinuable native exception, verifies the minidump signature, exception stream/code/thread/register context and encrypted archive, and checks that revocation detaches without killing a surviving child. A separate fixture abruptly kills the debugger process and verifies that its child survives. The Windows CI workflow compiles `Tests/NativeCapture/native_crash.c` with the installed x64 Visual C++ toolchain and runs the fixture against that native executable and the reviewed diagnostics source pin. Raising the fault outside the CLR preserves its original native code rather than wrapping it as a managed SEH exception. Cross-compilation passes locally; real Windows execution requires a Windows runner and is not claimed by a Linux build.

Quasar Bootstrap configures its worker run before spawn and the worker adopts that identity. Direct worker launches capture errors locally but need external launch-time dump settings for memory capture. Capture and evidence-storage failures are isolated from child supervision.

## Build and rollout

The new archive library is a local prerelease package until it is published. Run `scripts/bootstrap-diagnostics.sh /path/to/error-reporting/Diagnostics/CometWorks.Diagnostics.csproj` (or the `.ps1` equivalent) before restoring Quasar. It builds `CometWorks.Diagnostics` 0.1.0 into the sibling `diagnostics-artifacts/packages` feed already mapped in `NuGet.Config`. Fresh checkout names can differ; pass the source project explicitly. Use the matching Magnetar SDK for source plugin builds.

Merge and release the SDK/plugin first, activate the reviewed `ErrorReporting.xml` hub pin, then release the Magnetar launcher that requires it. The draft hub PR pins the real merged error-reporting implementation; merge the registration before exposing the mandatory-plugin launcher. Both release jobs and the Windows native fixture default to reviewed error-reporting commit `4629caff434e35312e8c99c776e364e392142d5d` (PR #2). Set the `DIAGNOSTICS_SOURCE_REF` GitHub repository variable to override it with another exact 40-character reviewed archive-package source commit. Both jobs refuse invalid or mutable dependency pins. The coordinated cluster changes extend the current main branch's Rust gateway and multi-host lifecycle; release them against the matching diagnostics SDK.

Deploy the always-running backoffice with Docker behind a TLS reverse proxy using logging-backend's Compose and proxy examples. Use `log.se-servers.net` consistently in the proxy and AllowedHosts, `https://log.se-servers.net/signin-github` for GitHub OAuth, and `https://log.se-servers.net/` for Quasar's backoffice URL. Quasar enrolls automatically after diagnostic consent; no installation ID/token configuration is required. Enroll field Hosts through their normal authenticated management flow. Backend human sessions require active CometWorks GitHub organization membership; model analysis and issue publication are separate opt-in deployment settings. No live endpoint, OAuth app, production tokens, model calls or repository issues are created by these changes.

The cross-repository proof is in error-reporting's `Tests/EndToEnd/run.sh`. It starts only disposable loopback Host/backend fixtures, executes real plugin capture and Quasar uplink, validates backend encryption/correlation/acknowledgment, and tests a small native SIGSEGV where systemd core access is available. Collector tests cover lease expiry, persistent revocation, stale generations, restart acknowledgment, historical evidence exclusion, kernel identity and capture I/O failure.


## Continuous application logs

The diagnostics choice also covers bounded batches of ordinary Quasar logs, Magnetar SDK logs and supervised process stdout. Each line is accepted only under a current consent lease. Batches retain the capture generation; the collector checks it again before sealing, so enabling diagnostics never uploads earlier console files or a previous consent window's pending lines.

The shared `LogBatchCapture` helper persists a pending journal and completes `log_batch` incidents after 30 seconds, 64 lines or 7,680 message characters. Lines carry UTC timestamp, source, severity and a message truncated to 512 characters. Collector polls respect the batching interval; process shutdown can force an eligible partial flush. Completed batches use stable IDs for retry and a separate 32-batch local quota. Ordinary logs cannot evict error/fatal capture files, and archive queues reserve capacity for incidents. At high volume, contention, truncation and quota eviction can discard ordinary lines; this is bounded diagnostic collection, not an audit log.

Log batches use the same encrypted archive and central uploader as incidents. They carry no dump or old `process.log` attachment. The backend stores these batches for log inspection and excludes them from automated incident analysis and issue publication. Diagnostic consent remains off by default.
