using System.Globalization;
using System.Text;
using SqlMigrator.Core.Mapping;
using SqlMigrator.Core.Schema;

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
            AppendTable(sb, byTarget[targetName], targetSchema, mapping.SourceReference);
            sb.AppendLine();
        }

        sb.AppendLine("COMMIT;");
        return sb.ToString();
    }

    private static void AppendTable(
        StringBuilder sb, TableMapping table, DbSchema targetSchema, string sourceReference)
    {
        var targetTable = targetSchema.Find(table.TargetTable)
            ?? throw new ScriptGenerationException($"Target table {table.TargetTable} is not in the target schema.");

        var quotedTarget = Quote(table.TargetTable);
        var usesIdentity = targetTable.Columns.Any(c => c.IsIdentity && table.Columns.Any(m =>
            string.Equals(m.TargetColumn, c.Name, StringComparison.OrdinalIgnoreCase)));

        sb.AppendLine(CultureInfo.InvariantCulture,
            $"IF EXISTS (SELECT 1 FROM {quotedTarget})");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"    THROW 50001, 'Target table {table.TargetTable} is not empty. Keys are preserved, so a rerun would collide.', 1;");
        sb.AppendLine();

        if (usesIdentity) sb.AppendLine($"SET IDENTITY_INSERT {quotedTarget} ON;");

        var columnList = string.Join(", ", table.Columns.Select(c => $"[{c.TargetColumn}]"));
        sb.AppendLine($"INSERT INTO {quotedTarget} ({columnList})");
        sb.AppendLine("SELECT " + string.Join(",\n       ", table.Columns.Select(c => c.Expression)));
        sb.AppendLine($"FROM {sourceReference}.{Quote(table.SourceTable)};");

        if (usesIdentity) sb.AppendLine($"SET IDENTITY_INSERT {quotedTarget} OFF;");

        sb.AppendLine($"PRINT CONCAT('{table.TargetTable}: ', @@ROWCOUNT, ' rows');");
    }

    private static string Quote(string fullName)
    {
        var parts = fullName.Split('.', 2);
        return parts.Length == 2
            ? $"[{parts[0].Trim('[', ']')}].[{parts[1].Trim('[', ']')}]"
            : $"[dbo].[{fullName.Trim('[', ']')}]";
    }
}
