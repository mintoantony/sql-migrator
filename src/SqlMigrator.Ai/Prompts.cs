using System.Text;
using SqlMigrator.Model.Schema;

namespace SqlMigrator.Ai;

public static class Prompts
{
    public const string TableMatchSystem = """
        You match tables between two SQL Server database schemas for a data migration.

        You are given a SOURCE schema and a TARGET schema. Decide which source table
        supplies the rows for each target table. Each target table is filled from at
        most one source table. A target table with no plausible source is simply left
        out of your answer — do not invent a match.

        Reply with JSON only, in exactly this shape:
        {"matches":[{"sourceTable":"dbo.X","targetTable":"dbo.Y","confidence":0.0,"reason":"one short sentence"}]}

        confidence is between 0 and 1 and expresses how sure you are. Use table names
        exactly as given, including the schema prefix.
        """;

    public static string TableMatchUser(DbSchema source, DbSchema target) => $"""
        SOURCE SCHEMA
        {SchemaSummary.Render(source)}

        TARGET SCHEMA
        {SchemaSummary.Render(target)}
        """;

    public const string ColumnMapSystem = """
        You map columns from one SQL Server source table onto one target table for a
        data migration, and you may use SQL expressions to reshape values.

        For each target column, give a T-SQL expression over the SOURCE table's columns.
        The expression is placed in the SELECT list of:
            INSERT INTO target (...) SELECT <your expressions> FROM source
        so it must be a single expression: no semicolons, no comments, no subqueries
        against other tables, no INSERT/UPDATE/DELETE/EXEC, and no reference to any
        table other than the source table given.

        Classify each expression with one rule:
          copy      — the column as-is
          truncate  — shortened to fit, e.g. LEFT(Name, 50)
          concat    — several source columns combined, e.g. CONCAT(A, ' ', B)
          split     — part of one source column, e.g. SUBSTRING(Email, CHARINDEX('@', Email) + 1, 100)
          case      — a conditional translation, e.g. CASE WHEN A = 1 THEN 1 ELSE 0 END
          constant  — no source column involved, e.g. SYSUTCDATETIME()

        Respect the target column's type and length. If a target column has no sensible
        source, put it in "unmapped" rather than inventing an expression. Identity
        columns on the target should still be mapped when the source has the matching
        key, because keys are preserved.

        Reply with JSON only, in exactly this shape:
        {"columns":[{"targetColumn":"X","rule":"copy","expression":"Y","confidence":0.0,"reason":"one short sentence"}],
         "unmapped":[{"targetColumn":"Z","reason":"one short sentence"}]}
        """;

    public static string ColumnMapUser(TableInfo sourceTable, TableInfo targetTable) => $"""
        SOURCE TABLE
        {SchemaSummary.RenderTable(sourceTable)}

        TARGET TABLE
        {SchemaSummary.RenderTable(targetTable)}
        """;

    public const string ColumnMapBatchSystem = """
        You map columns from several SQL Server source tables onto their matched target
        tables for a data migration, and you may use SQL expressions to reshape values.
        You are given several TABLE PAIRS, each with its own SOURCE TABLE and TARGET TABLE.
        Map each pair independently — a source table in one pair is never a source for
        another pair's target table.

        For each target column, give a T-SQL expression over that pair's SOURCE table's
        columns. The expression is placed in the SELECT list of:
            INSERT INTO target (...) SELECT <your expressions> FROM source
        so it must be a single expression: no semicolons, no comments, no subqueries
        against other tables, no INSERT/UPDATE/DELETE/EXEC, and no reference to any
        table other than that pair's own source table.

        Classify each expression with one rule:
          copy      — the column as-is
          truncate  — shortened to fit, e.g. LEFT(Name, 50)
          concat    — several source columns combined, e.g. CONCAT(A, ' ', B)
          split     — part of one source column, e.g. SUBSTRING(Email, CHARINDEX('@', Email) + 1, 100)
          case      — a conditional translation, e.g. CASE WHEN A = 1 THEN 1 ELSE 0 END
          constant  — no source column involved, e.g. SYSUTCDATETIME()

        Respect each target column's type and length. If a target column has no sensible
        source, put it in that pair's "unmapped" rather than inventing an expression.
        Identity columns on the target should still be mapped when the source has the
        matching key, because keys are preserved.

        Reply with JSON only, with exactly one entry in "tables" per table pair you were
        given, keyed by that pair's target table name (use the name exactly as given,
        including the schema prefix), in exactly this shape:
        {"tables":[
          {"targetTable":"dbo.Y",
           "columns":[{"targetColumn":"X","rule":"copy","expression":"Y","confidence":0.0,"reason":"one short sentence"}],
           "unmapped":[{"targetColumn":"Z","reason":"one short sentence"}]}
        ]}
        """;

    public static string ColumnMapBatchUser(IReadOnlyList<(TableInfo Source, TableInfo Target)> pairs)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < pairs.Count; i++)
        {
            var (sourceTable, targetTable) = pairs[i];
            sb.AppendLine($"TABLE PAIR {i + 1} (target table: {targetTable.FullName})");
            sb.AppendLine("SOURCE TABLE");
            sb.AppendLine(SchemaSummary.RenderTable(sourceTable));
            sb.AppendLine("TARGET TABLE");
            sb.AppendLine(SchemaSummary.RenderTable(targetTable));
        }
        return sb.ToString();
    }
}
