# Agent context — SPO Cold Storage

> Quick-start orientation for the next Copilot / agent session. Read this **first** if you've never worked in this repo before. Cross-references `CONTRIBUTING.md` for human-facing docs.

## 1. The 30-second summary

SharePoint Online files are migrated from SP into Azure Blob ("cold storage") and replaced with a `.url` placeholder. Site collection owners trigger this from an SPFx command set, the web API enqueues work to Service Bus, a worker (`Migration.Migrator`) does the heavy lifting. Restore is the inverse.

The **single most important invariant** in this codebase:

> **The SharePoint source file must NEVER be deleted unless the copy to blob succeeded AND post-copy validation (length + MD5) succeeded.**

This is enforced in `ColdStorageMigratorPipeline.ProcessAsync` by the **strict ordering of try/catch blocks**. Anyone touching that file must keep that ordering. `MigrationLifecycleStatusExtensions.SourceDeleteAllowed()` codifies the same rule as a runtime guard, and `MigrationLifecycleStatusTests` locks it down.

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
│   ├── ColdStorageBusListener.cs  ← new envelope dispatcher (legacy fallback for indexer)
│   ├── Lifecycle/                 ← IJobStatusWriter (single point of writes for status + audit)
│   ├── Migration/                 ← ColdStorageMigratorPipeline + SharePointPlaceholderWriter
│   └── Restore/                   ← SharePointRestorePipeline
│
├── Migration.Migrator/            ← console worker = listens on the bus
├── Migration.Indexer/             ← legacy site-discovery worker (still works via fallback)
├── Migration.SiteSnapshotBuilder/ ← per-site snapshot builder
├── LoadGenerator/                 ← CLI to push synthetic load
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
                  ColdStorageBusListener  (in Migration.Migrator)
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

## 6. Backward compatibility

`ColdStorageBusListener` accepts **two** message formats:

1. `ColdStorageBusEnvelope` (new — discriminated by `Operation = Migrate | Restore`)
2. Legacy `BaseSharePointFileInfo` JSON (what `Migration.Indexer` still produces)

The new listener tries (1) first, falls back to (2), dead-letters if neither parses. This is what lets the existing indexer-driven flow keep working after `Migration.Migrator` switched to `ColdStorageBusListener`. **Do not remove the fallback unless the indexer has also been updated to emit envelopes.**

## 7. Deployment

`deploy/deploy.ps1` is a single PowerShell 7+ script reading `deploy.parameters.json`. It builds, provisions Azure, deploys app code, and uploads the SPFx package. Idempotent — re-run any time. See `deploy/README.md` for the parameter reference and the `-SkipXxx` switches.

Key resources it creates / configures:
- Resource group, App Insights, Storage account (with cold-storage blob containers), Service Bus namespace + `filediscovery` queue, SQL server + DB, App Service Plan (Linux), two Web Apps (`{prefix}-web` for the API, `{prefix}-worker` for `Migration.Migrator`).
- App settings on both apps: `ConnectionStrings__SQLConnectionString`, `ConnectionStrings__Storage`, `ConnectionStrings__ServiceBus`, `AzureAd__*`, `APPLICATIONINSIGHTS_CONNECTION_STRING`, `BlobContainerName`, `BaseServerAddress`.
- The Entra app registration for the API is **not** auto-created — you put its IDs in the JSON.

## 8. Branch / PR

- Working branch: `feat/cold-storage-lifecycle`
- All cold-storage work + deployment script lives here, not on `main`.
- PR URL: https://github.com/sambetts/spo-cold-storage/pull/new/feat/cold-storage-lifecycle

## 9. Things to be careful of in future sessions

- **Don't reorder the try/catches in `ColdStorageMigratorPipeline.ProcessAsync`** — the order is the safety guarantee.
- **Don't add EF migrations**. Idempotent SQL in `DbInitializer` is the convention.
- **Don't put package versions in csproj files.** Use `Directory.Packages.props`.
- **Don't remove the legacy `BaseSharePointFileInfo` fallback** in `ColdStorageBusListener` unless `Migration.Indexer` is updated.
- **Don't disable `TreatWarningsAsErrors`** — fix the warning.
- **Don't ignore the 7 SnapshotBuilder test failures** unless you've also verified they're still the pre-existing reflection issue (run them at HEAD before claiming "tests broken by my change").
- **Don't add `using Models.ColdStorage;` directly inside `Web.Server` controllers** without checking for the `Web.Models` collision. Either alias or use `global::`.
- **The `BaseConfig` reflection in `Entities/Configuration/BaseConfig.cs`** is fragile — any new config property that isn't a string will need a corresponding default/converter or it will throw on missing values.

## 10. Session-state on disk

Each agent session also writes a `plan.md` and checkpoints under `~/.copilot/session-state/<session-id>/`. Those are private to the session and not committed. If you're picking up where another session left off, look for the latest checkpoint file.
