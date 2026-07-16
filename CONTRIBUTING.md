# Contributing to SPO ColdStorage

## Prerequisites

- .NET SDK 10.0.300+ (pinned in `src/global.json`)
- Node.js 20+ (for the SPA in `src/Web/web.client`)
- SQL Server (local or Azure) — only required for the integration tests under `src/Tests/`

## Layout

The solution sits under `src/` and is opened via `SPO.ColdStorage.slnx`.

| Folder                          | Purpose                                                       |
| ------------------------------- | ------------------------------------------------------------- |
| `Entities`                      | EF Core entities and `SPOColdStorageDbContext`                |
| `Models`                        | DTOs shared across the engine and web                         |
| `Migration.Engine`              | Core SharePoint→blob migration/restore logic + bus processor  |
| `Migration.Functions`           | Queue-triggered Azure Function — the worker                    |
| `Web/Web.Server`                | ASP.NET Core Web API host                                     |
| `Web/web.client`                | React + Vite SPA                                              |
| `Migration.Engine.Tests`        | **Unit tests** — in-memory, no external dependencies          |
| `Tests`                         | **Integration tests** — require live SQL Server / Azure infra |

## Build & test

From `SPO/ColdStorage/src/`:

```pwsh
dotnet build SPO.ColdStorage.slnx
dotnet test  SPO.ColdStorage.slnx
```

`TreatWarningsAsErrors=true` is set in `Directory.Build.props`, so any new warning will fail the build.

To work on the SPA:

```pwsh
cd Web/web.client
npm install
npm run dev
```

## Coding conventions

- **C# 13 / .NET 10**, nullable reference types enabled.
- File-scoped namespaces.
- Central package management via `Directory.Packages.props` — add new versions there, then reference by name (no `Version=` in csprojs).
- Logging through `ILogger<T>` (no `DebugTracer`, no `Tracer` field names).
- Tests: xUnit v3, NSubstitute, AwesomeAssertions (FluentAssertions namespace).
- New unit tests go in `Migration.Engine.Tests`; only put a test in `Tests` if it genuinely needs a live database or Azure resource.

## CI

Pushes and PRs against `main` that touch `SPO/ColdStorage/**` run `.github/workflows/spo-coldstorage-build.yml`, which restores, builds and tests the slnx on Ubuntu with .NET 10 and Node 20.
