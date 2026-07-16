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
│   └── Restore/                   ← SharePointRestorePipeline
│
├── Migration.Functions/           ← queue-triggered Azure Function = the worker (hosts the processor)
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
                          ──► IColdStorageBusPublisher
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
- **Keep `always-ready ≥ 1` on the Function** — on Flex Consumption, scale-from-zero does **not** reliably wake the identity-based Service Bus trigger listener; always-ready keeps it warm (verified: probe drained in <10 s at always-ready=2).
- **Don't disable `TreatWarningsAsErrors`** — fix the warning.
- **Migration.Engine.Tests should be fully green** (125/0). The legacy SnapshotBuilder tests that used to fail were removed with the legacy code; a new failure is genuinely yours.
- **Don't add `using Models.ColdStorage;` directly inside `Web.Server` controllers** without checking for the `Web.Models` collision. Either alias or use `global::`.
- **The `BaseConfig` reflection in `Entities/Configuration/BaseConfig.cs`** is fragile — any new config property that isn't a string will need a corresponding default/converter or it will throw on missing values.
- **On a private-only sub, don't try to write KV secrets or reach SQL from the deploy machine** — both are Forbidden. Use the control-plane (KV) / Kudu-over-VNet (SQL) paths the deploy already implements.

## 10. Session-state on disk

Each agent session also writes a `plan.md` and checkpoints under `~/.copilot/session-state/<session-id>/`. Those are private to the session and not committed. If you're picking up where another session left off, look for the latest checkpoint file.
