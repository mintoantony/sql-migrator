# SQLMigrator — Design

**Date:** 2026-09-09
**Status:** Approved design, ready for implementation planning
**Scope:** Proof of concept

## 1. Purpose

A local tool that copies data from one SQL Server database into another, where the
correspondence between the two — which table becomes which, which column becomes which —
is declared in an XML mapping file rather than in code.

The operator points it at two instances, hands it a mapping file, sees any problems with
that mapping listed before anything moves, and then watches the copy run table by table.

### In scope

- SQL Server on both ends
- Data movement only, into a target schema that already exists
- Mapping expressed as table → table and column → column, with renames and compatible
  type differences (`int` → `bigint`, `nvarchar(50)` → `nvarchar(200)`); see §5.4 for
  what "compatible" means and why it is narrower than it sounds
- Validation of the mapping against both live schemas before any write
- Live per-table progress during the run
- A per-table result report naming exactly what failed and why

### Not in scope

These are deliberate exclusions, not oversights. Each has a known cost and can be added
later without reworking what is built here.

| Excluded | Consequence |
|---|---|
| Key remapping / FK rewiring | The target must accept the source's primary keys unchanged. See §2.1. |
| Row filters (`WHERE`) | Whole tables are copied, or not at all. |
| Value transforms, constants, lookups | A target column is either mapped from a source column or left to its default. |
| Joins, multi-table sources | One source table produces rows in exactly one target table. |
| Schema creation (`CREATE TABLE`) | The engine never issues DDL against the target. |
| Engines other than SQL Server | No provider abstraction; `Microsoft.Data.SqlClient` is used directly. |
| Incremental or resumable runs, scheduling, CDC | Every run is a full copy into a cleared target. |

## 2. Constraints and decisions

### 2.1 Primary keys are preserved, not reassigned

Because there is no key remapping, migrated rows keep the primary key values they had in
the source. `SqlBulkCopy` runs with `SqlBulkCopyOptions.KeepIdentity`, and the engine
wraps each table in `SET IDENTITY_INSERT … ON/OFF` where the target has an identity
column among the mapped ones.

This is what keeps foreign keys valid for free: a child row's foreign key still points at
the same number it always did, so nothing needs rewriting. The price is that **the target
tables must be empty when a run starts**, or key collisions are certain. The engine
offers to clear them (§6.1).

### 2.2 A browser cannot reach SQL Server

Angular runs in a browser and cannot open a TCP socket to a database. The Angular
application is therefore a UI over a local backend process that holds both connections.
"Angular app" in this design means *Angular UI plus a .NET engine, served from localhost*.

### 2.3 Windows authentication only

The backend runs as the operator, and both connections use `Integrated Security=true`.
No credential is ever typed, stored, or transmitted. This removes the most dangerous
requirement a migration tool can have, and is available precisely because the tool is
local to one operator.

## 3. Architecture

```
D:\SQLMigrator
  src/
    SqlMigrator.Core/          engine library — no HTTP, no UI
    SqlMigrator.Api/           ASP.NET Core minimal API; serves the built Angular app
    SqlMigrator.Core.Tests/    unit and LocalDB integration tests
  web/                         Angular application
  fixtures/                    demo source and target databases (POC scaffolding)
  mappings/                    example mapping files and the XSD
  docs/                        this document and its successors
```

`SqlMigrator.Core` is a plain library: hand it a parsed mapping and two connection
strings and it validates or runs. It has no dependency on ASP.NET Core. This keeps the
engine unit-testable without a web host, and leaves the door open to packaging the tool
as a desktop application later without touching the engine.

`SqlMigrator.Api` binds to localhost only, holds **one run at a time in memory**, and
serves the Angular bundle from `wwwroot`. It has no database of its own, no job store and
no persistence — a single local operator running one migration needs none of them.

**Technology:** .NET 10 (SDK 10.0.301 present), `Microsoft.Data.SqlClient`, Angular 20
with Angular Material (Node 24.16 and npm 11.13 present).

### 3.1 Development loop

`ng serve` on port 4200, proxying `/api` to the API on its own port. For a packaged run,
`ng build` outputs into `src/SqlMigrator.Api/wwwroot` and `dotnet run` serves everything
from a single origin.

## 4. The mapping file

### 4.1 Format

```xml
<migration name="Demo migration">
  <table source="dbo.Customer" target="dbo.Client">
    <column source="CustomerId"  target="ClientId" />
    <column source="Name"        target="FullName" />
    <column source="CreatedUtc"  target="CreatedUtc" />
  </table>

  <table source="dbo.Order" target="dbo.Order">
    <column source="OrderId"     target="OrderId" />
    <column source="CustomerId"  target="ClientId" />
    <column source="Total"       target="Total" />
  </table>
</migration>
```

An XSD at `mappings/migration.xsd` defines this grammar. The file is validated against it
before any database is contacted, so a typo in the XML is reported as an XML problem
rather than as a confusing schema mismatch later on.

### 4.2 Rules

- **Tables execute in document order.** There is no topological sort; the author controls
  the sequence. The validator checks that the chosen order is foreign-key-safe and
  reports a blocking issue if a child is scheduled before its parent.
- **Names take the form `schema.table` and are matched case-insensitively**, consistent
  with a default SQL Server collation.
- **A target column that is not mapped receives nothing.** If it is `NOT NULL` with no
  default, that is a blocking issue. Silence is never an implicit value.
- **Every source column named must exist.** The mapping is not permitted to be
  aspirational.

## 5. Validation

Validation reads live metadata from both databases (`sys.tables`, `sys.columns`,
`sys.foreign_keys`, `sys.identity_columns`) and returns a list of issues. Severity decides
whether the run may start.

Guiding principle: **where a cheap query can turn "this might be a problem" into "this is
or is not a problem", run the query.** A validator that cries wolf is one that gets
ignored. Two rules below exploit this.

### 5.1 Blocking — the run cannot start

| Code | Condition |
|---|---|
| `XML001` | File is not valid against `migration.xsd` |
| `MAP001` | Source table does not exist |
| `MAP002` | Target table does not exist |
| `MAP003` | Source column does not exist |
| `MAP004` | Target column does not exist |
| `MAP005` | Two source columns mapped onto the same target column |
| `TYP001` | Types are not convertible by `SqlBulkCopy` (see §5.4) |
| `TYP003` | Narrowing conversion, and a `MAX(LEN(col))` or range probe shows existing data already exceeds the target |
| `NUL001` | Target column is `NOT NULL`, has no default, and nothing is mapped to it |
| `NUL002` | Source is nullable, target is `NOT NULL`, and a `COUNT` confirms NULLs are present |
| `ORD001` | A table is scheduled before a table it has a foreign key to |

### 5.2 Warning — the run may proceed

| Code | Condition |
|---|---|
| `TYP002` | Narrowing conversion (`nvarchar(200)` → `nvarchar(50)`, `decimal(18,4)` → `decimal(9,2)`) where the probe found nothing currently exceeding the target |
| `NUL003` | Source is nullable, target is `NOT NULL`, but no NULLs are present today |
| `TGT001` | Target table is not empty. A warning rather than a blocker because the "clear target tables first" option (§6.1) resolves it; with that option off it is very likely to become a key collision. |

### 5.3 Informational

| Code | Condition |
|---|---|
| `IDN001` | `IDENTITY_INSERT` will be used on this table |
| `CNT001` | Source row count — this also supplies the totals the progress bars need |

### 5.4 Type compatibility

`SqlBulkCopy` does not silently convert between unrelated types; it throws. The
validator's job is to catch what bulk copy would refuse, before the run starts.

Types are grouped into categories: string (`char`, `varchar`, `nchar`, `nvarchar`,
`text`), integer (`tinyint`, `smallint`, `int`, `bigint`), exact numeric (`decimal`,
`numeric`, `money`), approximate numeric (`float`, `real`), date and time, binary, `bit`,
`uniqueidentifier`, `xml`.

- Different categories → `TYP001`, blocking.
- Same category, target at least as wide or precise → clean.
- Same category, target narrower → probe the data. Data exceeds it → `TYP003`, blocking.
  Data fits today → `TYP002`, warning.

## 6. Execution

### 6.1 Before any table runs

If "clear target tables first" is set — the default, because §2.1 requires empty targets —
every mapped target table is emptied in one pass up front, in reverse dependency order so
that children go before parents. This happens once for the whole run, not per table:
clearing table by table as the run progressed would delete rows that a previously copied
table already depends on.

### 6.2 Per table

1. `SET IDENTITY_INSERT [target] ON`, if an identity column is among the mapped columns
2. A `SqlDataReader` over `SELECT <mapped columns> FROM <source table>`, fed to
   `SqlBulkCopy` with `KeepIdentity`, `BatchSize = 5000` and `NotifyAfter = 1000`
3. `SET IDENTITY_INSERT [target] OFF`

Nothing is ever fully materialised in memory. The reader streams, so memory stays flat
regardless of table size.

### 6.3 Failure handling

**Each table is its own transaction.** On failure it rolls back alone, is marked `Failed`
with the SQL error text, and **the run continues with the next table** — stopping at the
first error hides every other problem, and one run should produce the whole picture.

Tables holding a foreign key to a failed table are marked `Skipped` rather than
attempted, using the same foreign-key graph built for the `ORD001` check. This keeps one
genuine error from being buried under a cascade of consequent violations.

A "stop on first error" toggle provides the strict behaviour for anyone who wants it.

### 6.4 Cancellation

A `CancellationToken` is passed to `WriteToServerAsync`. The in-flight table's transaction
rolls back, and the remaining tables are marked `Cancelled`.

### 6.5 Run state

Per table: `Pending`, `Running`, `Done`, `Failed`, `Skipped` or `Cancelled`, plus rows
read, rows copied, duration and error text. Alongside those, the overall elapsed time and
a final summary of tables succeeded, failed and skipped.

## 7. HTTP API

| Method | Route | Purpose |
|---|---|---|
| `POST` | `/api/connections/test` | Confirm a server and database are reachable; returns SQL Server version and table count |
| `POST` | `/api/mapping/validate` | Mapping XML plus both connections → the issue list (§5) |
| `POST` | `/api/run` | Start the migration; returns a `runId` |
| `GET` | `/api/run/{id}` | Current run state (§6.5) |
| `POST` | `/api/run/{id}/cancel` | Request cancellation |

Progress reaches the UI by polling `GET /api/run/{id}` every 500 ms. Polling rather than
SignalR is deliberate: one user, one run, over localhost. A websocket would buy nothing
but a dependency and a reconnection story.

## 8. User interface

One page, four steps, because the flow is strictly linear. Angular Material supplies the
stepper, table, progress bar and chips.

1. **Connections** — source and target panels (server, database, Windows auth). Each has
   a Test button returning the SQL Server version and table count, so the operator can
   confirm they reached the right instance.
2. **Mapping** — choose the XML file. Shows what was parsed: the table list with column
   counts. Malformed XML surfaces here.
3. **Validate** — the issue list grouped by severity, with a verdict at the top
   ("3 blocking issues — cannot run", or "Ready · 2 warnings"). Migrate stays disabled
   while anything blocks.
4. **Run** — a live table, one row per mapped table: name, progress bar, rows copied of
   total, status chip, duration. Overall progress and Cancel sit above it. On completion
   the summary remains, with a Copy report button.

Standalone components, Angular signals for run state, and a single `MigrationService`
owning the polling loop.

## 9. Demo fixtures

The engine never creates schema (§1). In production both databases already exist. For the
POC they do not, so the repository carries scripts that build a pair of demo databases.
These are **demo scaffolding, explicitly outside the engine**.

`fixtures/source.sql` builds `SqlMigratorDemo_Source`:

- `dbo.Customer` — `CustomerId int IDENTITY` primary key, `Name nvarchar(200)`,
  `Email nvarchar(200) NULL`, `CreatedUtc datetime2`, `IsActive bit`
- `dbo.Order` — `OrderId int IDENTITY` primary key, `CustomerId int` → Customer,
  `OrderDate datetime2`, `Total decimal(18,2)`
- `dbo.OrderLine` — `OrderLineId int IDENTITY` primary key, `OrderId int` → Order,
  `Product nvarchar(100)`, `Qty int`, `UnitPrice decimal(18,2)`

`fixtures/target.sql` builds `SqlMigratorDemo_Target` — the same shape, deliberately
renamed so the mapping file has real work to do: `dbo.Client` (`ClientId`, `FullName`,
`EmailAddress`, `CreatedUtc`, `Active`), `dbo.Order` (carrying `ClientId` rather than
`CustomerId`), and `dbo.OrderLine`.

Seed volumes are chosen so progress is actually visible while it runs: roughly 5,000
customers, 20,000 orders and 60,000 order lines. All data is synthetic.

Two mapping files ship alongside:

- `mappings/demo.xml` — the clean mapping; validates green and migrates successfully
- `mappings/demo-broken.xml` — deliberately wrong in several ways (a misspelled column, a
  narrowing conversion that real data exceeds, a child table ordered before its parent) so
  the validation screen can be demonstrated with something to show

## 10. Testing

- **Unit, no database** — XSD validation, mapping parsing, the type-compatibility matrix
  and the foreign-key ordering check. These are pure functions over metadata models, and
  they carry most of the logic worth testing.
- **Integration against LocalDB** — build the fixtures, run the engine, then assert that
  row counts and per-column checksums match. Failure paths get explicit tests: a table
  that violates a constraint must end `Failed` while its children end `Skipped` and
  unrelated tables still end `Done`.
- **API** — `WebApplicationFactory` over the endpoints in §7.
- **UI** — component tests for the issue list and the run table. No end-to-end browser
  suite for a proof of concept.

## 11. Security

- Windows authentication only; no credential is stored, typed or transmitted (§2.3)
- Connection strings are never written to logs or returned in API responses
- The API binds to localhost only
- The POC runs against the synthetic demo databases in §9, never a production instance

## 12. Later, if wanted

Recorded so that the boundaries above read as decisions rather than gaps. None of these is
in scope now, and none would require reworking this design:

- Key remapping with an old-ID to new-ID crosswalk, for targets that assign their own keys
- Row filters and value transforms in the mapping grammar
- A "generate a `.sql` script instead of running it" mode, sharing the same mapping model
- Packaging as a desktop application — which is why the engine is a separate library
