# Agent context — SPO Cold Storage

> Quick-start orientation for the next Copilot / agent session. Read this **first** if you've never worked in this repo before. Cross-references `CONTRIBUTING.md` for human-facing docs.

## 1. The 30-second summary

SharePoint Online files are migrated from SP into Azure Blob ("cold storage") and replaced with a `.url` placeholder. Site collection owners trigger this from an SPFx command set, the web API enqueues work to Service Bus, and a queue-triggered Azure Function (`Migration.Functions`) does the heavy lifting. Restore is the inverse.

The **single most important invariant** in this codebase:

> **The SharePoint source file must NEVER be deleted unless the copy to blob succeeded AND post-copy validation (length + MD5) succeeded.**

This is enforced in `ColdStorageMigratorPipeline.ProcessAsync` by the **strict ordering of try/catch blocks**. Anyone touching that file must keep that ordering. `MigrationLifecycleStatusExtensions.SourceDeleteAllowed()` codifies the same rule as a runtime guard, and `MigrationLifecycleStatusTests` locks it down.

## 1b. Product charter (PM) — re-review every change against this

**What it is:** SPO Cold Storage is a first-of-its-kind, **open-source**, *self-service* archival product. It agilises **end users** (SharePoint site owners), not admins, to offload and archive **individual files and folders** into Azure Blob cold storage — each source replaced with a `.url` placeholder — plus automated rules. Restore is the inverse.

**Three product pillars — every PR must protect all three:**

1. **Reliable.** Source data is *never* deleted unless its copy to cold storage is confirmed good (length + MD5). This is THE invariant (§1). Any change touching the migrate pipeline, validation, or delete path must preserve the lifecycle ordering and the `SourceDeleteAllowed()` guard and keep `MigrationLifecycleStatusTests` green.

2. **Scalable.** Work is queue-driven and horizontally scalable: the API only *enqueues*; a stateless, queue-triggered worker (Azure Function, always-ready + scale-out) does the heavy lifting. Don't add per-item work to the API request path, don't reintroduce a singleton / Always-On worker, and keep processing idempotent (DB status guards + per-host in-flight locks) so scale-out never double-processes or double-deletes.

3. **Accountable.** Every transfer is logged and the logs are **easy to find**. All status/audit writes go through `IJobStatusWriter` into `migration_job_logs`; user-initiated actions carry `Action` / `ActorUpn`. The **SPA MUST expose** (a) a **Transfers/Logs area** where any transfer and its full lifecycle timeline can be found and filtered, and (b) a **cold-storage file finder** to browse/download what's been archived. Never delete or bypass the audit trail.

**SPA scope (the accountability surface):** browse cold storage; find/filter *all* transfers across sites; drill into a transfer's per-item lifecycle + log timeline; savings. Keep it focused — this is an accountability + operations console. Triggering migrations lives in the SPFx command set, not here.

**Re-review checklist for any change:**
- [ ] Preserves the never-delete-unconfirmed invariant (reliable)?
- [ ] New heavy work is queue-driven, idempotent, and scale-out safe (scalable)?
- [ ] Every transfer / user action is still logged and findable in the SPA (accountable)?
- [ ] The SPA Transfers/Logs area and Cold Storage finder still cover the change?

## 2. Solution layout

```
src/
├── SPO.ColdStorage.slnx           ← solution file (use this, not a .sln)
├── global.json                    ← pinned .NET 10 SDK
├── Directory.Build.props          ← TreatWarningsAsErrors=true, Nullable enable
├── Directory.Packages.props       ← central package management (NO Version= in csprojs)
│
├── Models/                        ← shared DTOs + the cold-storage models below
│   └── ColdStorage/
│       ├── MigrationLifecycleStatus.cs     ← 21-state enum + IsTerminal + SourceDeleteAllowed
│       ├── ColdStorageBusEnvelope.cs       ← bus message contract (Migrate | Restore)
│       ├── PlaceholderFileMetadata.cs      ← INI-style .url builder/parser
│       ├── OperationEnums.cs               ← ConflictBehavior, OperationKind, ItemKind
│       └── BlobMetadataKeys.cs
│
├── Entities/                      ← EF Core entities + DbContext
│   ├── SPOColdStorageDbContext.cs ← DbSets + index config
│   ├── DbInitializer.cs           ← idempotent raw SQL DDL (no EF migrations — see §3)
│   └── DBEntities/ColdStorage/    ← 5 new entities for the lifecycle backend
│
├── Migration.Engine/              ← the workhorse library
│   ├── ColdStorageMessageProcessor.cs ← transport-agnostic bus dispatcher (Migrate | Restore)
│   ├── Lifecycle/                 ← IJobStatusWriter (single point of writes for status + audit)
│   ├── Migration/                 ← ColdStorageMigratorPipeline + SharePointPlaceholderWriter
│   ├── Restore/                   ← SharePointRestorePipeline
│   ├── Providers/                 ← provider abstraction (ISourceStore/IColdStore + neutral Migrate/RestorePipeline + SharePoint/AzureBlob adaptors) — feature-flagged, see §5b
│   └── Reconciliation/            ← orphan-blob + dispatch (re-drive/stall) reconcilers
│
├── Migration.Functions/           ← queue-triggered Azure Function = the worker (hosts the processor + dispatch reconciler)
│
├── Web/Web.Server/                ← ASP.NET Core API host
│   ├── Controllers/               ← Migrations, Restores, Jobs, Containers, Placeholders
│   ├── Services/                  ← SiteOwnerAuth, ContainerAccess, BusPublisher
│   ├── Authorization/             ← CallerIdentity (ClaimsPrincipal extensions)
│   └── Models/Api/                ← REST DTOs (notice: namespace Web.Models.Api)
├── Web/web.client/                ← React + Vite SPA (separate npm project)
│
├── SPFx/spfx-cold-storage/        ← SharePoint Framework solution
│   ├── src/common/ColdStorageApiClient.ts
│   ├── src/extensions/coldStorageCommands/        ← ListView command set (Migrate / Restore)
│   ├── src/extensions/coldStorageStatusField/     ← Field customizer
│   └── config/package-solution.json               ← webApiPermissionRequests live here
│
├── Migration.Engine.Tests/        ← unit tests (xUnit v3, NSubstitute, AwesomeAssertions)
│   └── Lifecycle/                 ← cold-storage tests (46 of them)
└── Tests/                         ← integration tests — needs live SQL / Azure
```

The companion `requirements.md` at repo root is the source-of-truth spec for the cold-storage feature.

## 3. Conventions that are easy to get wrong

| Convention | Notes |
| ---------- | ----- |
| **Central package management** | Versions live in `Directory.Packages.props`. Do **NOT** add `Version=...` to `<PackageReference>` in csproj files. New package? Add to props first. |
| **No EF migrations** | The repo uses `EnsureCreated()` + idempotent raw SQL DDL in `DbInitializer`. When adding tables, follow that pattern: `IF OBJECT_ID('dbo.x','U') IS NULL CREATE TABLE ...` and `IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE ...) CREATE INDEX ...`. Match the existing style in `ApplyColdStorageSchemaUpgradesAsync`. |
| **`TreatWarningsAsErrors=true`** | A nullability warning or unused-using will fail the build. Don't disable globally — fix the warning. |
| **File-scoped namespaces** | All new C# code uses them. |
| **Snake-case columns** | All EF entities use `[Table("snake_case_name")]` + `[Column("snake_case_name")]`. |
| **`xUnit1051` suppression** | `Migration.Engine.Tests.csproj` suppresses this so `CancellationToken.None` is OK in tests. |
| **PnP.Framework gotcha** | `Microsoft.SharePoint.Client.File` does NOT expose a static `OpenBinaryDirect` in this build. Use the instance method `spFile.OpenBinaryStream()` (returns `ClientResult<Stream>`). |
| **`Models` collides in Web.Server** | The Web.Server project has its own `Web.Models.*` namespace, so an unqualified `Models.ColdStorage.X` resolves to `Web.Models.ColdStorage.X` and fails to compile. Use `global::Models.ColdStorage.X` inside Web.Server when the alias is ambiguous (see `JobsController.cs`). |
| **`File` and `LogLevel` ambiguity** | In `Migration.Engine/Migration/ColdStorageMigratorPipeline.cs` we alias `using IOFile = System.IO.File;` and `using LogLevel = Microsoft.Extensions.Logging.LogLevel;` to disambiguate. Same goes for restore. Don't remove these aliases. |
| **`StartsWith(char, StringComparison)` doesn't exist** | The `char` overload does NOT accept a `StringComparison`. Drop the second arg or use the `string` overload. |
| **Two test stacks** | `Migration.Engine.Tests` is pure unit tests, `Tests` needs live SQL. Add to the former unless you truly need DB. |

## 4. Build & test (verified)

```pwsh
cd src
dotnet build SPO.ColdStorage.slnx -v minimal           # 0 errors, 0 warnings expected
dotnet test  Migration.Engine.Tests/Migration.Engine.Tests.csproj --filter "FullyQualifiedName~Lifecycle"
```

**Known pre-existing test failures** in `Migration.Engine.Tests.SnapshotBuilder.SiteModelBuilderTests` — 7 tests fail with `System.ArgumentException: Object of type 'System.String' cannot be converted to type 'System.Boolean'` originating in `BaseConfig.cs:42`. These predate the cold-storage work (verified against the initial commit) and are caused by a reflection issue when test config has a string in a `bool` slot. **Treat these as baseline noise** until somebody fixes the test fixtures; don't waste a session chasing them unless that's the assignment.

`requirements.md` lives at the repo root and is the spec for everything under `Models/ColdStorage/`, `Entities/DBEntities/ColdStorage/`, the lifecycle pipelines, the API surface, and the SPFx component.

## 5. Cold-storage architecture cheat-sheet

```
SPFx command set
      │  AadHttpClient
      ▼
ASP.NET Core Web.Server  ──► SiteOwnerAuthorizationService (CSOM AssociatedOwnerGroup)
                          ──► ContainerAccessService (per-container ACLs)
                          ──► persist job.SubmissionRequestJson → 202 (fast)
                              │
                              ▼  in-proc channel (+ startup re-drive)
                   MigrationExpansionBackgroundService → MigrationExpander
                          (folder expand + per-file items + batched publish, off-request)
                              │
                              ▼  ColdStorageBusEnvelope (JSON)
                          Service Bus queue 'filediscovery'
                              │
                              ▼
                  ColdStorageMessageProcessor  (in Migration.Functions)
                              │
                  ┌───────────┴───────────┐
                  ▼                       ▼
       ColdStorageMigratorPipeline   SharePointRestorePipeline
                  │                       │
                  ▼                       ▼
       BlobStorageUploader            SharePointPlaceholderWriter (reads .url)
       SharePointPlaceholderWriter    BlobStorageDownloader
       IJobStatusWriter ◄────── all writes go through this for audit/rollup
       SPOColdStorageDbContext   (migration_jobs / migration_job_items / migration_job_logs)
```

**Durability (so a migration can never silently freeze):** `POST /api/migrations/start` only validates + authorizes + persists the selection to `job.SubmissionRequestJson`, then returns **202** immediately. A **`MigrationExpansionBackgroundService`** (hosted service in `Web.Server`) does the potentially-minutes-long folder expansion + per-file item creation + **batched publish decoupled from the request** (`CancellationToken.None`), so a large-folder submit is responsive and a client disconnect (HTTP 499) can't abort it; it re-drives any not-yet-expanded submission on startup. A **`MigrationDispatchReconciler`** (a timer `BackgroundService` in `Migration.Functions`, every `ColdStorageDispatchIntervalSeconds`) is the safety net: it re-drives `Queued` items whose message was never sent, **re-drives `RetryScheduled` items whose transient/throttle backoff has elapsed** (see below), fails items stuck past the max-queued / stall windows (phase-aware, so items past the delete point aren't mislabeled), **finalizes jobs whose items are all terminal but whose rollup was never written**, and closes empty jobs. The worker also **bounds per-item attempts** (`ColdStorageMaxProcessAttempts`) and dead-letters poison messages — firing the DLQ alert — instead of abandon-looping.

**Transient/throttle retry (bus-scheduled):** a transient error (HTTP 429 throttle, timeout, transient 5xx — classified by `TransientErrorClassifier`) at any migrate step does **not** fail the item terminally. `ColdStorageMigratorPipeline.HandleStepFailureAsync` parks it in the non-terminal **`RetryScheduled`** status (visible in the SPFx + SPA UIs as "Waiting to retry", with a per-file "retry in X" countdown) and records a concrete **`NextRetryAt`** — taken from the SharePoint **`Retry-After`** header (`ThrottleInfo`) when present, else the exponential backoff (`ThrottleBackoff`, base→cap via `ColdStorageThrottleBackoff{Base,Max}Seconds`), capped at 1h. The retry is then **scheduled directly on the Service Bus** (`IColdStorageQueuePublisher.ScheduleAsync` → `ScheduledEnqueueTime = NextRetryAt`) so it resumes on its own **even when the Function idles between bursts** — it no longer depends on the reconciler being awake (that was the freeze bug: the in-process reconciler timer stops when the queue drains). The `MigrationDispatchReconciler` remains a *late* safety net: it only re-drives a `RetryScheduled` item whose `NextRetryAt` is well past due (grace = 2× dispatch interval), for the rare case the scheduled message never fired. Only after `ColdStorageMaxProcessAttempts` throttled attempts does it go terminal. A *permanent* error still fails terminally on the first hit. The placeholder-write and source-delete CSOM calls go through `ExecuteQueryAsyncWithThrottleRetries` (they previously failed on the first 429), which also stashes the last `Retry-After` onto the thrown exception.

**Batch ETA (`JobEtaCalculator`):** `GET /api/jobs/{id}` and `/api/jobs/recent` return `estimatedCompletionUtc`, `throttledCount` and `nextRetryUtc`; per item they return `nextRetryAt` + `lastRetryAfterSeconds`. The ETA is a throughput projection (completed ÷ elapsed) floored by the latest scheduled retry, so throttling honestly pushes it out. Both UIs surface it on the progress bar.

**Migrate conflict-by-date (`MigrateConflictResolver`):** when a file already has a blob in cold storage, the pipeline compares the live source's last-modified against the source-modified recorded on the existing blob (`BlobMetadataKeys.OriginalLastModifiedUtc`): **older** archive → overwrite; **same version** (±2s) → skip the copy but still write the placeholder; **newer** archive → refuse (an anomaly — never overwrite a newer archive, never delete the source). The delete-safety invariant + try/catch ordering are unchanged.

### Lifecycle status order (you cannot delete the source before reaching `DeletePending`)

```
Queued
  → Validating
  → MigrationInProgress
  → CopiedToColdStorage
  → PostCopyValidation          ← MD5 + length verified here
  → DeletePending               ← only NOW is source delete allowed
  → PlaceholderCreating
  → ColdStorageMigrationCompleted (terminal)
```

Any failure before `DeletePending` puts the item in `CopyToColdStorageFailed` / `ValidationFailed` — both terminal, both keep the source intact. See `MigrationLifecycleStatusTests.SourceDelete_NeverAllowed_FromAnyFailureState`.

**Source-delete-aware failure classification + already-archived reconcile (`ArchivedItemReconcile`):** the corollary of §1 — because the source is only ever deleted *after* a confirmed good copy, an item with `SourceDeletedAt != null` **must never** be reported as `CopyToColdStorageFailed` ("copy failed, source untouched"), which is both wrong and unsafe to act on. Two guards enforce this: (1) the `MigrationDispatchReconciler` give-up + stall sweeps route a source-deleted item to `PlaceholderFailed` (needs its placeholder recreated from the existing blob), never copy-failed; (2) a **pass-0** in every reconciler pass (`JobStatusWriter.CompleteAlreadyArchivedAsync`) *corrects* any row whose timestamps prove it is already fully archived (copied + source deleted + **placeholder created**) but which was left non-completed — flipping it to `ColdStorageMigrationCompleted` and recomputing the job rollup. The `RequeueAsync` ("Recover failed") path does the same short-circuit (`RequeueResultResponse.Recovered`), completing already-archived rows directly instead of re-driving them into the pipeline (whose source is legitimately gone). This is what killed the multi-job-same-folder incident: hundreds of fully-archived files sat in `CopyToColdStorageFailed` because their final `-> Completed` write was lost, then a requeue reset them to `Queued` while keeping the original `CreatedAt`, so the 24h give-up re-failed them instantly on every "Recover failed". **Don't** reintroduce an unconditional `CopyToColdStorageFailed` in the give-up/stall paths, and **don't** re-drive a source-deleted item through a full copy.

## 5b. Restore is blob-driven (cold storage is the source of truth)

Restore does **not** trust the SQL DB for "what should be restored" — the DB is an audit log. `MigrationExpander.ExpandRestoreAsync` enumerates the archived **blobs** under the selected folder's blob prefix (`IColdStorageBlobEnumerator`, `ColdStorageBlobEnumerator` reads each blob's `spOriginal*` metadata for the authoritative destination, falling back to `ColdStorageBlobKey.ReverseServerRelativeUrl`) and resolves explicit placeholders to their blob. `SharePointRestorePipeline.ProcessBlobDrivenAsync` (taken when `RestoreTarget.BlobPath` is set → `IsBlobDriven`) restores straight from the blob: download → conflict-resolve → upload to the original path → verify → (optional) blob delete → remove the `.url` placeholder **only if it still exists**. This is what makes an **orphaned archive** (blob present, placeholder and/or `migration_job_items` row missing) restorable — the exact failure the DB-driven flow silently skipped. It is idempotent: a destination already present at the archived size is marked `Skipped` (no re-copy, no conflict-fail), so re-restoring a folder is safe. The legacy placeholder-driven path still handles in-flight envelopes with no `BlobPath`.

**Two easy-to-reintroduce bugs (both caught in review, PR #54):**
- Blob enumeration MUST skip version-history sidecars via `VersionBlobLayout.IsVersionArtifact` (`{key}.versions/<id>` content + `{key}.versions.json` manifest live under the same prefix as real files) — otherwise they get pushed back into SharePoint as junk `.json` files / spurious failures.
- Any restore envelope rebuilt from a DB row (`ColdStorageBusMessageFactory.BuildEnvelopeFromItem`) MUST set `BlobPath = item.BlobPath`, or a reconciler re-drive of an orphaned archive falls back to the placeholder path and terminal-fails as `ValidationFailed`.

The `#1` delete-safety invariant is preserved: a placeholder/blob is only ever removed **after** the restored file is verified present, and blob deletion stays gated by `ColdStorageDeleteBlobAfterRestore` (default off).

## 5c. Provider abstraction (feature-flagged foundation)

`Migration.Engine/Providers/` is a provider-neutral rewrite of the migrate + restore logic so the engine can be **unit-tested with in-memory adaptors** and so new source/cold-store backends (beyond SharePoint + Azure Blob) can be added later. It is **off by default** — the config flag `ColdStorageUseProviderPipelines` (int, `0` = legacy inline pipelines, `>0` = new path) gates it in `ColdStorageMessageProcessor`. Merging is safe; **do not flip the flag in prod until the real adaptors are integration-tested in a non-prod env** (the neutral pipelines are unit-proven, the SharePoint/Azure adaptors are not yet).

Two roles, both in `Migration.Engine.Providers`:
- **`ISourceStore`** (the live store, e.g. SharePoint): get item metadata + hold status, read/write content (restore upload is response-lost-but-landed safe), delete (idempotent — not-found = success), and the placeholder read/write/remove. Impl: `Providers/SharePoint/SharePointSourceStore.cs` (reuses the existing downloader / placeholder-writer / hold-detector).
- **`IColdStore`** (the archive, e.g. Azure Blob): get info (length/md5/metadata incl. `OriginalLastModifiedUtc` for conflict-by-date), idempotent overwrite write (+ md5 header + metadata), verify (length + md5), open-read/download, delete-if-exists. Impl: `Providers/AzureBlob/AzureBlobColdStore.cs` (translates 429/5xx → transient `TransferProviderException`).
- **`ITransferContent`** is the hand-off between them (Length + `ContentMd5Base64` computed once + `OpenReadAsync`); `TempFileTransferContent` (SP, streams + hashes + truncation-checks) and `InMemoryTransferContent` (tests).
- **`TransferProviderException(bool IsTransient, int? RetryAfterSeconds)`** — adaptors translate provider throttle/transient signals (429/timeout/5xx/"I/O error") into this; `TransientErrorClassifier` + `ThrottleInfo` recognise it; the in-memory adaptor throws it to simulate throttling.

The neutral `MigratePipeline` / `RestorePipeline` (over these interfaces, config via `TransferPipelineOptions.FromConfig`) preserve **every** guard the legacy pipelines have — the §1 delete-safety invariant + strict step order, verify-before-delete, idempotent retries, throttle-parking (`StepFailureHandler` → `RetryScheduled` + `Retry-After`), and the resume paths. Proven by **29 in-memory tests** in `Migration.Engine.Tests/Providers/` (fault injection covers throttling, transient vs permanent, idempotency, resume, conflict). Best-effort features **not yet** ported: version-history capture + permissions restore (follow-up).

## 6. Message format (greenfield — no legacy)

The bus carries a single format: `ColdStorageBusEnvelope` (discriminated by
`Operation = Migrate | Restore`). `ColdStorageMessageProcessor` parses it and
dead-letters anything unrecognised. The legacy indexer/snapshot stack and the
old `BaseSharePointFileInfo` fallback were removed in the greenfield cleanup —
there is no legacy message path to preserve.

## 7. Deployment

Two idempotent PowerShell 7+ orchestrators in `deploy/`, both reading `deploy/params.json`
(gitignored; copy from `params.example.json`). Re-run any phase any time. See
`deploy/README.md` for the full parameter/phase reference.

- `deploy.ps1` (Azure): `All | Prereqs | Validate | Infra | Secrets | App | Sql | Function | Smoke`.
  In `All`, **App runs before Sql** (the private-SQL grant runs over the VNet via the Web App's Kudu).
- `deploy-spo.ps1` (SharePoint): AAD app + cert, SPA `.env.production`, SPFx build + App Catalog upload.

Key resources it creates / configures:
- Resource group, App Insights, Storage account (with cold-storage blob containers + soft-delete/versioning),
  Service Bus namespace + `filediscovery` queue, SQL server + DB, App Service Plan, a Web App (`app-*`) for
  the API, a queue-triggered Azure Function (`func-*`, Flex Consumption, always-ready=2) for the worker, a
  VNet + private endpoints (blob/queue/table/SQL/KV) + DNS, and 3 Azure Monitor alerts (DLQ depth, queue
  backlog, function exceptions) → an action group.
- App settings use runtime **Key Vault references** (`@Microsoft.KeyVault(...)`) resolved by each app's MSI.
- The Entra app registration for the API is **not** auto-created by `deploy.ps1` — `deploy-spo.ps1 -Phase AadApp` creates it (or you put its IDs in the JSON).

**Private-only governance (MCAPS-style subs).** Some subscriptions force data-plane services private and
revert public re-enablement. `deploy.ps1` handles this end-to-end — set `sql.publicNetworkAccess: "Disabled"`
in params. Non-obvious mechanics (also in `deploy/README.md` + copilot-instructions):
- **KV secrets go via the control plane** (Bicep/ARM), which bypasses the private data-plane block:
  `aad-client-secret` is passed to Bicep as a `@secure()` param during `Infra`; the `Secrets` phase only
  *verifies* existence. Data-plane `az keyvault secret set` is Forbidden here.
- **The private-SQL grant runs over the VNet via the Web App's Kudu**: `CREATE USER … WITH SID` (appId bytes;
  no Directory Reader for `FROM EXTERNAL PROVIDER`), script uploaded via the Kudu **VFS** API and run by
  `-File` (an `-EncodedCommand` one-liner exceeds cmd.exe's ~8 KB limit once the SQL-token JWT is embedded).
- A purge-protected KV is **recovered** before Bicep on a same-name redeploy.
- Storage shared-key + SB local-auth are disabled; the client factories use the MSI anyway (they read only
  the account name / namespace from the connection strings), so the key-based KV secrets still work.

The whole stack has been **torn down and redeployed from scratch** to prove reproducibility (Function drains
the queue in <10 s with always-ready=2; API returns 401 = SQL grant + DbInitializer healthy).

## 8. Branch / PR

- Cold-storage lifecycle, greenfield cleanup, product-charter features, and the deployment scripts are all
  merged to **`main`** and deployed. Do net-new work on a feature branch off `main`.

## 9. Things to be careful of in future sessions

- **Don't reorder the try/catches in `ColdStorageMigratorPipeline.ProcessAsync`** — the order is the safety guarantee.
- **Don't add EF migrations**. Idempotent SQL in `DbInitializer` is the convention.
- **Don't put package versions in csproj files.** Use `Directory.Packages.props`.
- **The worker is the queue-triggered Azure Function** (`Migration.Functions`) — don't reintroduce a continuous WebJob / Always-On worker (governance disables Always On, so the item would idle-stop). The API only enqueues.
- **Submit is async — keep it that way.** `MigrationsController.StartAsync` only persists the selection to `job.SubmissionRequestJson` and returns **202**; a `MigrationExpansionBackgroundService` (in `Web.Server`) expands folders, creates the per-file items and publishes them (batched, `CancellationToken.None`) off the request thread, re-driving un-expanded jobs on startup. Do **not** revert to expanding + publishing inline on the request token: a large folder then blocks the request for minutes (HTTP 500 timeouts) and a client disconnect (HTTP 499) orphans `Queued` items with no message, silently freezing the job (exactly what happened to a 4,000-file job). The `MigrationDispatchReconciler` re-drives orphans, fails stalled items and finalizes stuck job rollups — **don't remove it**.
- **Keep the SQL DB at ≥ S1.** The worker does concurrent EF writes; on **Basic** (5 DTU, ~30 concurrent-request cap) a busy submit hits `The request limit for the database is 30 and has been reached` → EF `RetryLimitExceededException` mid-pipeline, leaving items in odd states. Worker concurrency is tunable **without a code redeploy** via `params.worker.maxConcurrentCalls` → the `AzureFunctionsJobHost__extensions__serviceBus__maxConcurrentCalls` app setting (overrides `host.json`); default 5.
- **The SPA MSAL config is baked at build time.** `configReader.ts` reads `import.meta.env.VITE_*`; `deploy-spo.ps1 -Phase SpaConfig` writes `src/Web/web.client/.env.production`. If you rebuild/redeploy the SPA (e.g. the App phase) **without** a current `.env.production`, `VITE_MSAL_SCOPES` becomes the literal `"undefined"` and the SPA dies with MSAL `ClientConfigurationError: url_parse_error` on any token request. Run `SpaConfig` before an App deploy.
- **Keep `always-ready ≥ 1` on the Function** — on Flex Consumption, scale-from-zero does **not** reliably wake the identity-based Service Bus trigger listener; always-ready keeps it warm (verified: probe drained in <10 s at always-ready=2).
- **Don't disable `TreatWarningsAsErrors`** — fix the warning.
- **Migration.Engine.Tests should be fully green** (144/0). The legacy SnapshotBuilder tests that used to fail were removed with the legacy code; a new failure is genuinely yours.
- **Don't add `using Models.ColdStorage;` directly inside `Web.Server` controllers** without checking for the `Web.Models` collision. Either alias or use `global::`.
- **The `BaseConfig` reflection in `Entities/Configuration/BaseConfig.cs`** is fragile — any new config property that isn't a string will need a corresponding default/converter or it will throw on missing values.
- **On a private-only sub, don't try to write KV secrets or reach SQL from the deploy machine** — both are Forbidden. Use the control-plane (KV) / Kudu-over-VNet (SQL) paths the deploy already implements.
- **SPFx build/deploy gotchas** (these cost a full incident — the ListView command-set buttons silently vanished):
  - After any `git reset` / branch switch, run `npm ci` in **both** `src/Web/web.client` **and** `src/SPFx/spfx-cold-storage` before building. The deploy scripts skip `npm install` when `node_modules` exists, so a stale tree silently produces broken builds (the SPA got react-router **5** vs required **6**; SPFx got react **17.0.2** vs the pinned **17.0.1**).
  - A stale SPFx react (17.0.2 vs SPO's SPFx 1.22 runtime, which provides **17.0.1**) makes the extension fail to load with `Could not load … Cannot destructure property 'id' of 'a' as it is undefined` in the browser console, and the buttons silently disappear. Fix: `npm ci` → react 17.0.1 → rebuild, and verify the built `Extension_<id>.xml` manifest declares react `17.0.1`.
  - **Bump `solution.version` AND `features[].version`** in `config/package-solution.json` on **every** SPFx deploy. Re-deploying the same version is a no-op on an installed site (`Update-PnPApp` won't swap the bundle; SharePoint says "same version number … delete from the site + recycle bin").
  - `deploy-spo.ps1 -Phase Spfx` now runs `gulp clean` first; without it `dist/` accumulates duplicate hashed bundles that ship in the `.sppkg` and corrupt the tenant's component-manifest registration.
  - No app-only cert on the machine + private Key Vault → upload with `deploy-spo.ps1 -Phase SpfxDeploy -SpfxAuthMode DeviceLogin` (two device codes: App Catalog, then the site). To go headless, drop a passwordless PFX in `deploy/.local/` and register it on the AAD app.

## 10. Session-state on disk

Each agent session also writes a `plan.md` and checkpoints under `~/.copilot/session-state/<session-id>/`. Those are private to the session and not committed. If you're picking up where another session left off, look for the latest checkpoint file.
