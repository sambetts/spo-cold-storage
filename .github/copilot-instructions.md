# Copilot instructions — SPO Cold Storage

SharePoint Online files are migrated into Azure Blob ("cold storage") and replaced
with a `.url` placeholder; restore is the inverse. Site-collection owners trigger
this from an SPFx command set → the ASP.NET Core web API enqueues work to Service
Bus → a queue-triggered Azure Function (`Migration.Functions`) does the heavy lifting.

Deeper references (keep in sync when you change behaviour):
- `AGENTS.md` — full agent orientation, solution tree, gotcha catalogue.
- `requirements.md` — source-of-truth spec for the whole cold-storage feature.
- `CONTRIBUTING.md` — human-facing contributor guide.
- `DATABASE_STRUCTURE.md` — SQL schema, tables, relationships.
- `deploy/README.md` — deployment parameter/phase reference.

> Note: the top-level `README.md` predates this work and still says ".NET 6" and
> describes only the original indexer/migrator. The current stack is **.NET 10**.

## The one invariant you must never break

> The SharePoint source file is **never** deleted unless the copy to blob
> succeeded **and** post-copy validation (length + MD5) succeeded.

This is enforced by the **ordering of the try/catch blocks** in
`Migration.Engine/Migration/ColdStorageMigratorPipeline.cs` `ProcessAsync`, and
codified as a runtime guard by `MigrationLifecycleStatusExtensions.SourceDeleteAllowed()`
(`Models/ColdStorage/MigrationLifecycleStatus.cs`). Do not reorder those blocks.
`Migration.Engine.Tests/Lifecycle/MigrationLifecycleStatusTests.cs` locks the rule down.

Lifecycle order (source delete is only legal once you reach `DeletePending`):
`Queued → Validating → MigrationInProgress → CopiedToColdStorage →
PostCopyValidation → DeletePending → PlaceholderCreating →
ColdStorageMigrationCompleted`. Any failure before `DeletePending` lands in a
terminal `*Failed` state and leaves the source intact.

## Build, test, lint

Backend (.NET 10 SDK is pinned in `src/global.json`). Run from `src/`:

```pwsh
dotnet build SPO.ColdStorage.slnx -v minimal      # use the .slnx, not a .sln
dotnet test  Migration.Engine.Tests/Migration.Engine.Tests.csproj   # fast unit tests, no external deps
```

- Filter to an area: `dotnet test Migration.Engine.Tests/Migration.Engine.Tests.csproj --filter "FullyQualifiedName~Lifecycle"`
- Run a **single** test: `... --filter "FullyQualifiedName~SourceDelete_NeverAllowed_FromAnyFailureState"`
- `Tests/Tests.csproj` are **integration** tests — they need live SQL Server / Azure. Add new tests to `Migration.Engine.Tests` unless you truly need a DB.
- There is no separate C# lint step: IDE code-style analyzers run in-build (`EnforceCodeStyleInBuild=true`) and `TreatWarningsAsErrors=true`, so any warning fails the build.

Web SPA — from `src/Web/web.client/`: `npm install`, `npm run dev`, `npm run build`, `npm run lint` (eslint, `--max-warnings 0`).

SPFx — from `src/SPFx/spfx-cold-storage/`: `npm install`, then `npm run build` (`gulp bundle`) or `npm run package` (`gulp bundle --ship && gulp package-solution --ship`); `npm run serve` for the workbench.

**Test suite is fully green** (144/0, no external deps). The legacy
`SnapshotBuilder.SiteModelBuilderTests` that used to fail were removed with the
legacy indexer/snapshot code, so a failing test now is genuinely yours.

## Architecture (the parts that span files)

```
SPFx command set (site-owner only)  ──AadHttpClient──►  Web/Web.Server (API)
    │                                       │  SiteOwnerAuthorizationService (CSOM AssociatedOwnerGroup)
    │                                       │  ContainerAccessService (per-container ACLs)
    │                                       ▼  IColdStorageBusPublisher
    │                       Service Bus queue 'filediscovery'  (ColdStorageBusEnvelope JSON)
    │                                       ▼
    │                       ColdStorageMessageProcessor  (in Migration.Functions)
    │                          ┌────────────┴────────────┐
    │                          ▼                         ▼
    │           ColdStorageMigratorPipeline      SharePointRestorePipeline
    │                          └──────────┬──────────────┘
    ▼                                     ▼
 status column   ◄──  IJobStatusWriter  ──►  SPOColdStorageDbContext
                       (single point for all status + audit writes)
              migration_jobs / migration_job_items / migration_job_logs
```

- **`IJobStatusWriter` (`Migration.Engine/Lifecycle/`) is the single point of truth** for status and audit writes. Route every status/log write through it — that's what keeps SharePoint, the API, and the DB in sync.
- **`ColdStorageMessageProcessor` parses one message format** — `ColdStorageBusEnvelope` (discriminated by `Operation = Migrate | Restore`) — and dead-letters anything unrecognised. The legacy indexer/snapshot stack + `BaseSharePointFileInfo` fallback were removed in the greenfield cleanup; there's no legacy path to preserve.
- **The worker is the queue-triggered Azure Function `Migration.Functions`** (Flex Consumption, always-ready). The API only enqueues; don't reintroduce a continuous WebJob / Always-On worker.
- **Enqueue is decoupled from the HTTP request and durable.** The API persists items as `Queued` then publishes batched with `CancellationToken.None`, so a client disconnect (HTTP 499) can't abort publishing and orphan un-sent items. A `MigrationDispatchReconciler` timer service in `Migration.Functions` re-drives `Queued` items whose message was never sent, fails stalled/stuck items, and the processor dead-letters poison messages after `ColdStorageMaxProcessAttempts`. Don't revert to per-item publishing on the request token and don't remove the reconciler — that's what stops a migration silently freezing.
- The `.url` placeholder is INI-style; build/parse it only via `Models/ColdStorage/PlaceholderFileMetadata.cs`.

## Conventions that will bite you

- **Central package management.** All versions live in `src/Directory.Packages.props`. Never add `Version=` to a `<PackageReference>` in a csproj — add the package to the props file first, then reference it by name.
- **No EF migrations.** The DB is created via `EnsureCreated()` + idempotent raw SQL DDL in `Entities/DbInitializer.cs`. To add tables/indexes, match `ApplyColdStorageSchemaUpgradesAsync`: `IF OBJECT_ID('dbo.x','U') IS NULL CREATE TABLE ...`, `IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE ...) CREATE INDEX ...`.
- **EF entities use snake_case** everywhere: `[Table("snake_case")]` + `[Column("snake_case")]`. File-scoped namespaces for all new C#.
- **`Models` namespace collides inside `Web.Server`** — it has its own `Web.Models.*`, so unqualified `Models.ColdStorage.X` resolves to `Web.Models.ColdStorage.X` and won't compile. Use `global::Models.ColdStorage.X` in Web.Server (see `JobsController.cs`); don't blindly `using Models.ColdStorage;` there.
- **Ambiguity aliases in the pipelines** — `ColdStorageMigratorPipeline.cs` (and restore) alias `using IOFile = System.IO.File;` and `using LogLevel = Microsoft.Extensions.Logging.LogLevel;`. Keep them.
- **PnP.Framework gotcha:** `Microsoft.SharePoint.Client.File` has no static `OpenBinaryDirect` in this build — use the instance `spFile.OpenBinaryStream()` (returns `ClientResult<Stream>`).
- `string.StartsWith(char, StringComparison)` does not exist — the `char` overload takes no `StringComparison`.
- `Migration.Engine.Tests.csproj` suppresses `xUnit1051`, so `CancellationToken.None` is fine in tests. Test stack: xUnit v3, NSubstitute, AwesomeAssertions (still uses the `FluentAssertions` namespace).
- Log through `ILogger<T>` — no `Tracer`/`DebugTracer` fields.
- `Entities/Configuration/BaseConfig.cs` reflection is fragile: any new config property that isn't a `string` needs a default/converter or it throws on a missing value.

## Deployment

Two idempotent, phase-based PowerShell 7+ orchestrators in `deploy/`, both reading
`deploy/params.json` (gitignored; copy from `params.example.json`):

- `deploy.ps1` — Azure side (Bicep infra, Key Vault, SQL access, app publish/zip deploy, Function worker deploy). Phases: `All | Prereqs | Validate | Infra | Secrets | App | Sql | Function | Smoke`. In `All`, **App runs before Sql** — the private-SQL grant runs over the VNet via the Web App's Kudu (see below), so the VNet-integrated Web App must exist first.
- `deploy-spo.ps1` — SharePoint side (AAD app + cert, SPA config, SPFx build + App Catalog upload). Phases: `All | Prereqs | AadApp | Cert | SpaConfig | Spfx | SpfxDeploy`.

**Private-only governance (MCAPS-style subs) — the deploy handles it, but the mechanics are non-obvious:**
- Set `sql.publicNetworkAccess: "Disabled"` in `params.json` when a policy forces private-only SQL (e.g. `DenyPublicEndpointEnabled`, which also rejects SQL firewall rules). The Bicep firewall rules then become conditional, and the `Sql` phase grants each MSI `db_owner` **over the VNet via the Web App's Kudu command API** — using `CREATE USER … WITH SID` (appId bytes; the server lacks Directory Reader so `FROM EXTERNAL PROVIDER` fails), and **uploading the script through the Kudu VFS API** to run by `-File` (the `-EncodedCommand` one-liner exceeds cmd.exe's ~8 KB limit once the SQL-token JWT is embedded). It waits for the Kudu (scm) site to be ready first (503 right after a zip deploy).
- Key Vault is private-only too, so data-plane `az keyvault secret set` is Forbidden and re-enabling public access is reverted. Secrets are written via the **control plane** instead: Bicep writes all three (`aad-client-secret` passed as a `@secure()` param during `Infra`; storage/SB conn-strings via `listKeys`). The `Secrets` phase detects a private vault and just verifies existence via `az resource show`.
- A purge-protected KV is **recovered** (`az keyvault recover`) by the `Infra` phase before Bicep on a same-name redeploy.
- Storage shared-key + Service Bus local-auth are disabled, but the client factories (`BlobServiceClientFactory`/`ServiceBusClientFactory`) ignore the connection-string key/SAS and use the MSI when `config != null` (they only read the account name / namespace), so the key-based KV secrets still work.

Any SPFx code or `elements.xml` change requires bumping **both** `solution.version`
and `features[].version` in `config/package-solution.json`, or the upgrade skips
re-applying the element manifests and leaves stale CustomActions on existing sites.
