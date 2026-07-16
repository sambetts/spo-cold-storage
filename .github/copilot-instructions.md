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

**Known baseline noise:** ~7 tests in `Migration.Engine.Tests.SnapshotBuilder.SiteModelBuilderTests`
fail with `ArgumentException` from `Entities/Configuration/BaseConfig.cs` (reflection
coercing a string into a `bool` slot). These predate the cold-storage work — verify
they still fail at HEAD before blaming your change; don't chase them otherwise.

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

- `deploy.ps1` — Azure side (Bicep infra, Key Vault, SQL access, app publish/zip deploy). Phases: `All | Prereqs | Validate | Infra | Secrets | Sql | App | Smoke`.
- `deploy-spo.ps1` — SharePoint side (AAD app + cert, SPA config, SPFx build + App Catalog upload). Phases: `All | Prereqs | AadApp | Cert | SpaConfig | Spfx | SpfxDeploy`.

Any SPFx code or `elements.xml` change requires bumping **both** `solution.version`
and `features[].version` in `config/package-solution.json`, or the upgrade skips
re-applying the element manifests and leaves stale CustomActions on existing sites.
