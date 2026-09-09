using System.Text;
using SqlMigrator.Model.Schema;

namespace SqlMigrator.Ai;

/// <summary>
/// Renders structural metadata as compact text for a prompt. There is no overload that
/// accepts data — this type only ever sees a DbSchema.
///
/// Ordering is explicit everywhere (tables by full name, columns by ordinal position,
/// foreign keys by parent/referenced/name) so that the same schema always renders to the
/// same text, regardless of what order the caller's collections happen to enumerate in.
/// </summary>
public static class SchemaSummary
{
    public static string Render(DbSchema schema)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Database: {schema.DatabaseName}");
        sb.AppendLine();

        foreach (var table in schema.Tables.OrderBy(t => t.FullName, StringComparer.Ordinal))
            sb.AppendLine(RenderTable(table));

        if (schema.ForeignKeys.Count > 0)
        {
            sb.AppendLine("Foreign keys:");
            foreach (var fk in schema.ForeignKeys
                         .OrderBy(fk => fk.ParentFullName, StringComparer.Ordinal)
                         .ThenBy(fk => fk.ReferencedFullName, StringComparer.Ordinal)
                         .ThenBy(fk => fk.Name, StringComparer.Ordinal))
                sb.AppendLine($"  {fk.ParentFullName} -> {fk.ReferencedFullName}");
        }

        return sb.ToString();
    }

    public static string RenderTable(TableInfo table)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"TABLE {table.FullName}");
        foreach (var column in table.Columns.OrderBy(c => c.OrdinalPosition))
        {
            var pk = table.PrimaryKeyColumns.Contains(column.Name, StringComparer.OrdinalIgnoreCase) ? " PK" : "";
            var identity = column.IsIdentity ? " IDENTITY" : "";
            var nullability = column.IsNullable ? "NULL" : "NOT NULL";
            var @default = column.HasDefault ? " DEFAULT" : "";
            sb.AppendLine($"  {column.Name} {RenderType(column)} {nullability}{identity}{pk}{@default}");
        }
        return sb.ToString();
    }

    private static string RenderType(ColumnInfo column) => column.DataType.ToLowerInvariant() switch
    {
        "char" or "varchar" or "nchar" or "nvarchar" or "binary" or "varbinary" =>
            $"{column.DataType}({(column.MaxLength == -1 ? "MAX" : column.MaxLength.ToString())})",
        "decimal" or "numeric" => $"{column.DataType}({column.Precision},{column.Scale})",
        _ => column.DataType
    };
}
