using System.Globalization;
using System.Text;
using SqlMigrator.Core.Sql;
using SqlMigrator.Model.Mapping;
using SqlMigrator.Model.Schema;

namespace SqlMigrator.Core.Generation;

public sealed record ScriptOptions(
    string SourceServer,
    string TargetServer,
    DateTimeOffset GeneratedUtc,
    string MappingFileName,
    string MappingSha256);

public sealed class ScriptGenerationException(string message) : Exception(message);

public static class ScriptGenerator
{
    public static string Generate(MigrationMapping mapping, DbSchema targetSchema, ScriptOptions options)
    {
        // Reject an invalid source reference as early as possible — before touching the
        // table order, the target schema, or any per-table work — rather than letting it
        // reach the emitted script unvalidated. It cannot simply be bracket-quoted like an
        // ordinary identifier; QuoteSourceReference both validates its shape and re-quotes it.
        string quotedSourceReference;
        try
        {
            quotedSourceReference = SqlIdentifier.QuoteSourceReference(mapping.SourceReference);
        }
        catch (ArgumentException ex)
        {
            throw new ScriptGenerationException(
                $"Mapping's source reference is invalid: {ex.Message}");
        }

        var targetTables = mapping.Tables.Select(t => t.TargetTable).ToList();

        if (!TableOrder.TrySort(targetTables, targetSchema.ForeignKeys, out var ordered, out var cycle))
        {
            throw new ScriptGenerationException(
                "Foreign keys among the mapped tables form a cycle, so no valid insert order exists: "
                + string.Join(", ", cycle));
        }

        var byTarget = mapping.Tables.ToDictionary(t => t.TargetTable, StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();

        sb.AppendLine($"-- Generated {options.GeneratedUtc.UtcDateTime:yyyy-MM-ddTHH:mmZ} from {options.MappingFileName} (sha256 {options.MappingSha256})");
        sb.AppendLine($"-- Source: {options.SourceServer} . {mapping.SourceDatabase}");
        sb.AppendLine($"-- Target: {options.TargetServer} . {mapping.TargetDatabase}");
        if (mapping.Model is not null) sb.AppendLine($"-- Mapping proposed by: {mapping.Model}");
        sb.AppendLine("-- Review before running. This script inserts data.");
        sb.AppendLine();
        sb.AppendLine("SET XACT_ABORT ON;");
        sb.AppendLine("BEGIN TRANSACTION;");
        sb.AppendLine();

        foreach (var targetName in ordered)
        {
            AppendTable(sb, byTarget[targetName], targetSchema, quotedSourceReference);
            sb.AppendLine();
        }

        sb.AppendLine("COMMIT;");
        return sb.ToString();
    }

    private static void AppendTable(
        StringBuilder sb, TableMapping table, DbSchema targetSchema, string quotedSourceReference)
    {
        var targetTable = targetSchema.Find(table.TargetTable)
            ?? throw new ScriptGenerationException($"Target table {table.TargetTable} is not in the target schema.");

        var quotedTarget = Quote(table.TargetTable);
        var escapedTargetName = SqlLiteral.Escape(table.TargetTable);
        var usesIdentity = targetTable.Columns.Any(c => c.IsIdentity && table.Columns.Any(m =>
            string.Equals(m.TargetColumn, c.Name, StringComparison.OrdinalIgnoreCase)));

        sb.AppendLine(CultureInfo.InvariantCulture,
            $"IF EXISTS (SELECT 1 FROM {quotedTarget})");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"    THROW 50001, 'Target table {escapedTargetName} is not empty. Keys are preserved, so a rerun would collide.', 1;");
        sb.AppendLine();

        var columnList = string.Join(", ", table.Columns.Select(c => SqlIdentifier.QuoteIdentifier(c.TargetColumn)));
        var insertLines = new[]
            {
                $"INSERT INTO {quotedTarget} ({columnList})",
                "SELECT " + string.Join(",\n       ", table.Columns.Select(c => c.Expression)),
                $"FROM {quotedSourceReference}.{Quote(table.SourceTable)};",
            };

        if (usesIdentity)
        {
            // IDENTITY_INSERT is session-level state, not transactional: SET XACT_ABORT rolls
            // the data back on failure but does not reset this flag. Guarantee it is turned OFF
            // on both paths, then rethrow the original error unchanged so the caller still sees it.
            sb.AppendLine("BEGIN TRY");
            sb.AppendLine($"    SET IDENTITY_INSERT {quotedTarget} ON;");
            foreach (var line in insertLines) sb.AppendLine("    " + line);
            sb.AppendLine($"    SET IDENTITY_INSERT {quotedTarget} OFF;");
            sb.AppendLine("END TRY");
            sb.AppendLine("BEGIN CATCH");
            sb.AppendLine($"    SET IDENTITY_INSERT {quotedTarget} OFF;");
            sb.AppendLine("    THROW;");
            sb.AppendLine("END CATCH;");
        }
        else
        {
            foreach (var line in insertLines) sb.AppendLine(line);
        }

        sb.AppendLine($"PRINT CONCAT('{escapedTargetName}: ', @@ROWCOUNT, ' rows');");
    }

    /// <summary>
    /// Quotes a two-part (schema.table) or one-part (defaults to dbo) SQL name. Delegates to
    /// <see cref="SqlIdentifier"/>, the one place in the solution that owns identifier-quoting
    /// logic — every identifier the generator emits routes through there, directly or via this
    /// method.
    /// </summary>
    private static string Quote(string fullName) => SqlIdentifier.Quote(fullName);
}
