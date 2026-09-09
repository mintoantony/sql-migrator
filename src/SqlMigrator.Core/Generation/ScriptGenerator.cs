using System.Globalization;
using System.Text;
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
        var escapedTargetName = EscapeLiteral(table.TargetTable);
        var usesIdentity = targetTable.Columns.Any(c => c.IsIdentity && table.Columns.Any(m =>
            string.Equals(m.TargetColumn, c.Name, StringComparison.OrdinalIgnoreCase)));

        sb.AppendLine(CultureInfo.InvariantCulture,
            $"IF EXISTS (SELECT 1 FROM {quotedTarget})");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"    THROW 50001, 'Target table {escapedTargetName} is not empty. Keys are preserved, so a rerun would collide.', 1;");
        sb.AppendLine();

        var columnList = string.Join(", ", table.Columns.Select(c => QuoteIdentifier(c.TargetColumn)));
        var insertLines = new[]
            {
                $"INSERT INTO {quotedTarget} ({columnList})",
                "SELECT " + string.Join(",\n       ", table.Columns.Select(c => c.Expression)),
                $"FROM {sourceReference}.{Quote(table.SourceTable)};",
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

    private static string Quote(string fullName)
    {
        var parts = fullName.Split('.', 2);
        return parts.Length == 2
            ? $"{QuoteIdentifier(parts[0])}.{QuoteIdentifier(parts[1])}"
            : $"{QuoteIdentifier("dbo")}.{QuoteIdentifier(fullName)}";
    }

    /// <summary>
    /// Quotes a single T-SQL identifier. Strips at most one matched pair of surrounding
    /// brackets (so a caller-supplied "[Name]" is not double-bracketed), then escapes every
    /// remaining ']' by doubling it, per the T-SQL rule for bracket-quoted identifiers. This is
    /// the only place identifier quoting happens; every identifier the generator emits must
    /// route through here (directly, or via <see cref="Quote"/>).
    /// </summary>
    private static string QuoteIdentifier(string identifier)
    {
        var name = identifier;
        if (name.Length >= 2 && name[0] == '[' && name[^1] == ']')
        {
            name = name[1..^1];
        }

        return $"[{name.Replace("]", "]]")}]";
    }

    /// <summary>Escapes a value for embedding inside a single-quoted T-SQL string literal.</summary>
    private static string EscapeLiteral(string value) => value.Replace("'", "''");
}
