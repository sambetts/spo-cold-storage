# SPO Cold Storage — Technical Overview

> The engineering companion to the [homepage](../README.md) and the
> [User & Admin Guide](../USER_ADMIN_GUIDE.md). This page covers architecture, the
> tech stack, the repository layout, how to build and test, and how to deploy.
>
> For the full agent orientation and product charter see [`AGENTS.md`](../AGENTS.md);
> for the source-of-truth feature spec see [`requirements.md`](../requirements.md).

---

## The one invariant

> A SharePoint source file is **never** deleted unless the copy to Blob succeeded
> **and** post-copy validation (length + MD5) passed.

This is enforced by the strict lifecycle ordering in
`Migration.Engine/Migration/ColdStorageMigratorPipeline.cs` and codified as a runtime
guard by `MigrationLifecycleStatusExtensions.SourceDeleteAllowed()`
(`Models/ColdStorage/MigrationLifecycleStatus.cs`), and locked down by unit tests.
Source deletion only becomes legal once an item reaches `DeletePending`:

```
Queued → Validating → MigrationInProgress → CopiedToColdStorage →
PostCopyValidation → DeletePending → PlaceholderCreating →
ColdStorageMigrationCompleted
```

Any failure before `DeletePending` lands in a terminal `*Failed` state and leaves the
source intact.

---

## Product pillars

Every change is reviewed against three pillars (see [`AGENTS.md`](../AGENTS.md) §1b):

- **Reliable** — never delete a source without a confirmed, validated copy. The
  invariant above is the hard rule.
- **Scalable** — the API only *enqueues*; a stateless, queue-triggered Azure Function
  (Flex Consumption, scale-out) does the heavy lifting. Processing is idempotent (DB
  status guards + per-host in-flight locks), so scale-out never double-processes or
  double-deletes.
- **Accountable** — every transfer is logged and easy to find. All status/audit writes
  flow through one writer into `migration_job_logs`; the web app's **Transfers & Logs**
  area exposes every archive/restore and its full per-file lifecycle, and the **Cold
  Storage** finder browses/downloads what's been archived.

---

## How it works

```
 SharePoint doc library
   │  SPFx command set (site contributors/owners): Migrate · Restore · Status
   ▼  AadHttpClient
 ASP.NET Core Web API  ──►  contributor auth (CSOM effective perms) + per-container ACLs
   │                        eligibility rules (size / type / exclusions / holds)
   ▼  enqueue only
 Azure Service Bus  ('filediscovery' queue, ColdStorageBusEnvelope)
   ▼
 Azure Function (Flex Consumption, always-ready)  ── the worker
   ├─ Migrate:  download → validate → copy to Blob → verify (len+MD5)
   │            → delete source → write .url placeholder
   └─ Restore:  read Blob → upload to SharePoint → verify → remove placeholder
   │
   ▼  all status + audit writes go through one writer
 Azure SQL  (migration_jobs / migration_job_items / migration_job_logs)
```

**Submit is async and durable.** `POST /api/migrations/start` validates, authorizes, and
persists the selection, then returns **202** immediately. A background service expands
folders, creates the per-file items and publishes them off the request thread, so a
large-folder submit stays responsive and a client disconnect can't orphan the job. A
dispatch reconciler re-drives anything that slips through and finalizes stuck rollups.

**Downloads stream through the API.** The `.url` placeholder is an INI-style file that
records where the content went; opening it routes the user through the web app (auth +
ACL check) which streams the file back from cold storage over its private connection —
so downloads work even when the storage account blocks all public network access.

---

## Tech stack

| Layer | Technology |
| ----- | ---------- |
| Backend | **.NET 10** — ASP.NET Core Web API + `Migration.Engine` library |
| Worker | **Azure Function** (isolated .NET 10, Flex Consumption, queue-triggered) |
| Data / messaging | Azure SQL, Azure Service Bus, Azure Blob Storage |
| Web app | **React 18 + Vite + Fluent UI v9** SPA, MSAL auth |
| SharePoint | **SPFx 1.22** ListView command set + status field customizer (React) |
| Platform | Key Vault, managed identity, VNet + private endpoints (no shared keys) |

---

## Repository layout

```
src/
├── SPO.ColdStorage.slnx            solution (use the .slnx)
├── Models/            shared DTOs + cold-storage models (lifecycle, envelope, placeholder)
├── Entities/          EF Core entities + DbContext (idempotent SQL DDL, no EF migrations)
├── Migration.Engine/  the workhorse: bus processor, migrate/restore pipelines, lifecycle writer
├── Migration.Functions/  the queue-triggered Function worker
├── Web/Web.Server/    ASP.NET Core API host
├── Web/web.client/    React + Vite SPA
├── SPFx/spfx-cold-storage/  SharePoint Framework solution
└── Migration.Engine.Tests/  unit tests (xUnit v3)
deploy/                Bicep + PowerShell deployment orchestrators
```

---

## Getting started

Prerequisites: .NET 10 SDK (pinned in `src/global.json`), Node.js 22, PowerShell 7+,
Azure CLI, and an Entra tenant with SharePoint Online.

Build & test (from `src/`):

```pwsh
dotnet build SPO.ColdStorage.slnx -v minimal
dotnet test  Migration.Engine.Tests/Migration.Engine.Tests.csproj
```

Web app (from `src/Web/web.client/`): `npm install` · `npm run dev` · `npm run build` · `npm run lint`
SPFx (from `src/SPFx/spfx-cold-storage/`): `npm install` · `npm run build` · `npm run package`

---

## Deployment

Two idempotent, phase-based PowerShell orchestrators drive a full deployment from
a single `deploy/params.json` (copy from `params.example.json`):

- **`deploy/deploy.ps1`** — the Azure side: Bicep infra (VNet + private endpoints,
  SQL, Service Bus, Key Vault, storage, the API Web App **and the Function
  worker** + alerts), Key Vault secrets, SQL access, and app/worker code deploy.
  Phases: `Prereqs · Validate · Infra · Secrets · App · Sql · Function · Smoke`.
- **`deploy/deploy-spo.ps1`** — the SharePoint side: the Entra app + certificate,
  SPA MSAL config, and the SPFx build + App Catalog upload.

See **[`deploy/README.md`](../deploy/README.md)** for the full parameter and phase reference.

---

## Documentation index

| Doc | What |
| --- | --- |
| [`README.md`](../README.md) | Homepage / product overview |
| [`USER_ADMIN_GUIDE.md`](../USER_ADMIN_GUIDE.md) | End-user + admin guide (with screenshots) |
| [`AGENTS.md`](../AGENTS.md) | Orientation, product charter, architecture, conventions |
| [`requirements.md`](../requirements.md) | Source-of-truth feature spec |
| [`DATABASE_STRUCTURE.md`](../DATABASE_STRUCTURE.md) | SQL schema and relationships |
| [`deploy/README.md`](../deploy/README.md) | Deployment parameters and phases |
| [`CONTRIBUTING.md`](../CONTRIBUTING.md) | Contributor guide |

---

## Contributing

Contributions are welcome. Before opening a PR, check it against the product
pillars (reliable / scalable / accountable) in [`AGENTS.md`](../AGENTS.md), keep the
never-delete invariant intact, and make sure `dotnet build` (warnings are errors) and
the unit tests are green. See [`CONTRIBUTING.md`](../CONTRIBUTING.md).
