# SQLMigrator

Reads two SQL Server schemas, has a language model propose how one maps onto the other —
including transformation rules — proves every proposed expression against the real
database, takes your corrections, and generates a T-SQL migration script.

**The tool never writes data to a database.** Its outputs are a mapping XML file and a
`.sql` script that you review and run yourself.

## Requirements

- .NET 10 SDK
- Node 22+ and the Angular CLI
- SQL Server reachable with Windows authentication
- An NVIDIA NIM API key, or any OpenAI-compatible endpoint

## Setup

```bash
# Demo databases
sqlcmd -S localhost -E -i fixtures/source.sql
sqlcmd -S localhost -E -i fixtures/target.sql

# Model access — environment variable is preferred over the settings file
setx NVIDIA_API_KEY "nvapi-…"
cp src/SqlMigrator.Api/appsettings.Local.json.example src/SqlMigrator.Api/appsettings.Local.json
# then set Ai:Model to a model id from the NIM catalogue

# Build and run
cd web && ng build && cd ..
dotnet run --project src/SqlMigrator.Api
```

Open <http://127.0.0.1:5199>.

## Testing

```bash
dotnet test                      # engine, AI contracts, API — no API key needed
cd web && ng test --watch=false  # Angular components
```

The live model test is skipped unless `SQLMIGRATOR_AI_LIVE=1`, `NVIDIA_API_KEY` and
`SQLMIGRATOR_AI_MODEL` are all set.

## Security

- Windows authentication only; no database credential is stored, typed, or generated
- The NVIDIA key stays server-side. `appsettings.Local.json` is gitignored
- Only schema metadata is sent to the model — never a data row. `SqlMigrator.Ai` has no
  reference to `Microsoft.Data.SqlClient`, so this is enforced by the project graph
- The API binds to `127.0.0.1` only

## Documents

- Design: `docs/superpowers/specs/2026-09-09-ai-sql-migrator-design.md`
- Plans: `docs/superpowers/plans/`
