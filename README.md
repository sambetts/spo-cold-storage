# SPO Cold Storage

**Self-service, open-source archival for SharePoint Online.** Site-collection
owners move individual files and folders out of SharePoint into low-cost Azure
Blob **cold storage** — each source is replaced with a `.url` placeholder that
links back to the archived copy. Restore is the inverse. It's the first
self-service cold-storage tool of its kind: it agilises **end users**, not
admins.

> **The one safety guarantee:** a SharePoint source file is **never** deleted
> unless the copy to Blob succeeded **and** post-copy validation (length + MD5)
> passed. This invariant is enforced by the lifecycle ordering in
> `ColdStorageMigratorPipeline` and the `SourceDeleteAllowed()` runtime guard,
> and locked down by tests.

---

## Product pillars

Every change is reviewed against three pillars (see `AGENTS.md` §1b):

- **Reliable** — never delete a source without a confirmed, validated copy.
- **Scalable** — the API only *enqueues*; a stateless, queue-triggered Azure
  Function (Flex Consumption, scale-out) does the heavy lifting. Processing is
  idempotent, so scale-out never double-processes or double-deletes.
- **Accountable** — every transfer is logged and easy to find. The web app has a
  **Transfers & Logs** area where any archive/restore and its full per-file
  lifecycle can be searched, and a **Cold Storage** finder to browse/download
  what's been archived.

---

## How it works

```
 SharePoint doc library
   │  SPFx command set (site owners): Migrate · Restore · Status
   ▼  AadHttpClient
 ASP.NET Core Web API  ──►  site-owner auth (CSOM) + per-container ACLs
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

The `.url` placeholder is an INI-style file that records where the content went;
opening it routes the user through the web app (auth + ACL check + short-lived
SAS) to download from cold storage.

## Features

- **Self-service migrate / restore** of files *and* folders from the SharePoint
  toolbar (site-owner only; enforced server-side).
- **Transfers & Logs** — an accountability console: every transfer across all
  sites, filterable, with a per-file lifecycle timeline, a worker online/offline
  banner, and one-click **recovery** of failed or stuck transfers.
- **Cold Storage finder** — browse and download archived blobs through the app.
- **Eligibility rules** — skip ineligible items (too small, excluded extensions
  — `.url` is always excluded, excluded scopes, retention/legal holds, recently
  read) before anything is copied or deleted.
- **Savings** dashboard — storage reclaimed and estimated net monthly saving.
- **Resilient by design** — geo-redundant storage with soft-delete + versioning,
  dead-letter after N attempts, resumable placeholder recovery, and Azure Monitor
  alerts (DLQ depth, backlog, worker errors).

## Tech stack

| Layer | Technology |
| ----- | ---------- |
| Backend | **.NET 10** — ASP.NET Core Web API + `Migration.Engine` library |
| Worker | **Azure Function** (isolated .NET 10, Flex Consumption, queue-triggered) |
| Data / messaging | Azure SQL, Azure Service Bus, Azure Blob Storage |
| Web app | **React 18 + Vite + Fluent UI v9** SPA, MSAL auth |
| SharePoint | **SPFx 1.22** ListView command set + status field customizer (React) |
| Platform | Key Vault, managed identity, VNet + private endpoints (no shared keys) |

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

## Deployment

Two idempotent, phase-based PowerShell orchestrators drive a full deployment from
a single `deploy/params.json` (copy from `params.example.json`):

- **`deploy/deploy.ps1`** — the Azure side: Bicep infra (VNet + private endpoints,
  SQL, Service Bus, Key Vault, storage, the API Web App **and the Function
  worker** + alerts), Key Vault secrets, SQL access, and app/worker code deploy.
  Phases: `Prereqs · Validate · Infra · Secrets · App · Sql · Function · Smoke`.
- **`deploy/deploy-spo.ps1`** — the SharePoint side: the Entra app + certificate,
  SPA MSAL config, and the SPFx build + App Catalog upload.

See **`deploy/README.md`** for the full parameter and phase reference.

## Documentation

| Doc | What |
| --- | --- |
| [`AGENTS.md`](AGENTS.md) | Orientation, product charter, architecture, conventions |
| [`requirements.md`](requirements.md) | Source-of-truth feature spec |
| [`DATABASE_STRUCTURE.md`](DATABASE_STRUCTURE.md) | SQL schema and relationships |
| [`deploy/README.md`](deploy/README.md) | Deployment parameters and phases |
| [`USER_ADMIN_GUIDE.md`](USER_ADMIN_GUIDE.md) | End-user + admin guide |
| [`CONTRIBUTING.md`](CONTRIBUTING.md) | Contributor guide |

## Contributing

Contributions are welcome. Before opening a PR, check it against the product
pillars (reliable / scalable / accountable) in `AGENTS.md`, keep the never-delete
invariant intact, and make sure `dotnet build` (warnings are errors) and the unit
tests are green. See `CONTRIBUTING.md`.
