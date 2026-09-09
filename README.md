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

## The demo databases

`fixtures/source.sql` and `fixtures/target.sql` create two databases whose names are fixed
and are what the tests and the walkthrough below assume:

| | Database | Tables |
|---|---|---|
| Source | `SqlMigratorDemo_Source` | `dbo.Customer`, `dbo.Order`, `dbo.OrderLine` |
| Target | `SqlMigratorDemo_Target` | `dbo.Client`, `dbo.Order`, `dbo.OrderLine` |

They are deliberately not identical — that is the point. `Customer` becomes `Client`,
columns are renamed (`Email` → `EmailAddress`, `Product` → `ProductName`, `Qty` →
`Quantity`), and some target columns have no direct source at all, so the model has to
propose transformations rather than a column-for-column copy.

Both databases are **disposable**. The test suite drops and recreates them on every run, so
never point `SQLMIGRATOR_TEST_SQL` at a server holding anything you care about.
`SqlMigrator.Api.Tests` owns a separate pair, `SqlMigratorApiDemo_Source` /
`SqlMigratorApiDemo_Target`, so the two projects cannot interfere.

## Walking through the demo

1. **Connections** — Server `localhost` for both. Source database
   `SqlMigratorDemo_Source`, target `SqlMigratorDemo_Target`. Press **Test** on each; a
   healthy connection reports the SQL Server version and a table count of 3.
2. **Source reference** — this is the prefix the generated script uses to reach the source,
   and it is emitted into the script verbatim, so it must be a bracket-quoted name:
   - same instance: `[SqlMigratorDemo_Source]`
   - different instance: `[LinkedServerName].[SqlMigratorDemo_Source]`
3. **Analyse** — reads both schemas, asks the model to match tables, then asks it for an
   expression per target column, then proves every expression against the real database.
   Expect roughly a minute; progress shows which table pair is being mapped.
4. **Review** — the grid shows each target column with its rule, the proposed expression,
   a confidence, and any issue. Proposals at or above the confidence threshold
   (`Ai:ConfidenceThreshold`, default `0.75`) arrive ticked. **Unticking a row drops it**:
   the column is submitted as unmapped, so a non-nullable target with no default then
   raises a blocking issue rather than silently shipping a proposal you rejected.
5. **Script** — generation is refused while any blocking issue stands. The result can be
   copied, downloaded as `migration.sql`, or saved as mapping XML to
   `src/SqlMigrator.Api/bin/Debug/net10.0/mappings/`.

### What a real run looks like

On the fixtures above, a typical run matches `dbo.Customer → dbo.Client` at high confidence
and proposes `CONCAT(FirstName, ' ', LastName)` for `FullName`. Both source columns are
`nvarchar(100)`, so that expression yields `nvarchar(201)` against an `nvarchar(200)`
target, and the validator raises a **TYP002 narrowing warning**. That is the tool working,
not failing: the model proposed something plausible, SQL Server itself revealed the
problem, and you correct it in the grid — `LEFT(CONCAT(FirstName, ' ', LastName), 200)` —
before any script exists.

Two other things you are likely to see:

- **`SRC001` warnings** for source tables the model matched to nothing. They are warnings,
  not errors — a partial migration is a legitimate outcome.
- **A `429 Too Many Requests` failure** on one or two tables if your NIM tier is rate
  limited. The run continues and records the failure against that table only; a provider
  problem on one table costs that table, not the migration. Re-run to pick them up.

## Testing

```bash
dotnet test                      # engine, AI contracts, API — no API key needed
cd web && ng test --watch=false  # Angular components
```

The live model test is skipped unless `SQLMIGRATOR_AI_LIVE=1`, `NVIDIA_API_KEY` and
`SQLMIGRATOR_AI_MODEL` are all set.

The database-backed tests use `localhost` unless `SQLMIGRATOR_TEST_SQL` names another
server. They create and drop the four `SqlMigratorDemo_*` / `SqlMigratorApiDemo_*`
databases and nothing else.

## Configuration reference

`src/SqlMigrator.Api/appsettings.Local.json` — gitignored; copy from the `.example`:

| Setting | Default | Notes |
|---|---|---|
| `Ai:BaseUrl` | `https://integrate.api.nvidia.com/v1` | Any OpenAI-compatible endpoint. Point it at a self-hosted NIM container to keep everything local |
| `Ai:Model` | *(none)* | A model id from the NIM catalogue. Must support JSON-mode output |
| `Ai:ApiKey` | *(empty)* | `NVIDIA_API_KEY` is preferred; never commit a key |
| `Ai:ConfidenceThreshold` | `0.75` | At or above this, a proposal arrives pre-ticked in the review grid |
| `Ai:TimeoutSeconds` | `120` | Per request, applied as a linked cancellation |
| `Ai:ColumnBatchSize` | `5` | Table pairs mapped per column-mapping request. `1` reproduces one request per table pair |

### How many requests a run makes, and the trade

Analysis costs `1 + ceil(N / ColumnBatchSize)` requests for `N` matched table pairs: one to
match tables, then one per batch of column mappings. At the default that is 2 requests for
the 3-table demo and 11 for a 50-table schema, against 4 and 51 before batching.

Batching buys far fewer requests — and so far fewer rate-limit refusals — at one real cost:
**a provider failure now costs the whole batch rather than a single table.** The failure
message names every table in the failed batch, so nothing disappears silently, and the other
batches still complete. Set `ColumnBatchSize` to `1` to get per-table isolation back at the
old request volume.

### When the model returns nothing

A run that reaches Review with no table pairs and a failure like *"Gave up on table matching"*
means the provider refused or never answered — not that the tool failed. The two common causes:

- **`429 Too Many Requests`** — the account is rate limited. Re-run; batching makes this much
  less likely than it was.
- **"the model provider timed out"** — the model took longer than `Ai:TimeoutSeconds` on a
  single call. Raise it, or pick a faster model. Note this bites on the *first* call, before
  batching applies, so batching alone will not fix a model that is simply slow — and a larger
  `ColumnBatchSize` makes each request bigger, so lower it if a slow model starts timing out
  on the column pass too.

The API always binds `http://127.0.0.1:5199` — that is set in code, not configuration, and
deliberately not overridable by `ASPNETCORE_URLS`. This process can reach two databases and
must not be reachable from the network.

The Angular build writes into `src/SqlMigrator.Api/wwwroot`, which the API serves as static
files, so `ng build` then `dotnet run` gives you the whole app on one port with no dev
proxy. `ng serve` on `:4200` also works for front-end work; `web/proxy.conf.json` forwards
`/api` to `:5199`.

## Security

- Windows authentication only; no database credential is stored, typed, or generated
- The NVIDIA key stays server-side. `appsettings.Local.json` is gitignored
- Only schema metadata is sent to the model — never a data row. `SqlMigrator.Ai` has no
  reference to `Microsoft.Data.SqlClient`, so this is enforced by the project graph
- The API binds to `127.0.0.1` only

## Documents

- Design: `docs/superpowers/specs/2026-09-09-ai-sql-migrator-design.md`
- Plans: `docs/superpowers/plans/`
