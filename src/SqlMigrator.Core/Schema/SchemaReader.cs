using Microsoft.Data.SqlClient;

namespace SqlMigrator.Core.Schema;

/// <summary>Reads structural metadata only. Never reads a data row.</summary>
public static class SchemaReader
{
    private const string ColumnSql = """
        SELECT  s.name  AS SchemaName,
                t.name  AS TableName,
                c.name  AS ColumnName,
                ty.name AS DataType,
                CASE WHEN ty.name IN ('nchar','nvarchar') AND c.max_length > 0
                     THEN c.max_length / 2 ELSE c.max_length END AS MaxLength,
                c.precision,
                c.scale,
                c.is_nullable,
                c.is_identity,
                CASE WHEN c.default_object_id <> 0 THEN 1 ELSE 0 END AS HasDefault,
                c.column_id
        FROM sys.columns c
        JOIN sys.tables  t  ON t.object_id = c.object_id
        JOIN sys.schemas s  ON s.schema_id = t.schema_id
        JOIN sys.types   ty ON ty.user_type_id = c.user_type_id
        WHERE t.is_ms_shipped = 0
        ORDER BY s.name, t.name, c.column_id;
        """;

    private const string PrimaryKeySql = """
        SELECT  s.name AS SchemaName,
                t.name AS TableName,
                c.name AS ColumnName,
                ic.key_ordinal
        FROM sys.indexes i
        JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN sys.columns c        ON c.object_id  = ic.object_id AND c.column_id = ic.column_id
        JOIN sys.tables  t        ON t.object_id  = i.object_id
        JOIN sys.schemas s        ON s.schema_id  = t.schema_id
        WHERE i.is_primary_key = 1 AND t.is_ms_shipped = 0
        ORDER BY s.name, t.name, ic.key_ordinal;
        """;

    private const string ForeignKeySql = """
        SELECT  fk.name AS ForeignKeyName,
                ps.name AS ParentSchema,  pt.name AS ParentTable,
                rs.name AS ReferencedSchema, rt.name AS ReferencedTable
        FROM sys.foreign_keys fk
        JOIN sys.tables  pt ON pt.object_id  = fk.parent_object_id
        JOIN sys.schemas ps ON ps.schema_id  = pt.schema_id
        JOIN sys.tables  rt ON rt.object_id  = fk.referenced_object_id
        JOIN sys.schemas rs ON rs.schema_id  = rt.schema_id;
        """;

    public static async Task<DbSchema> ReadAsync(string connectionString, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var columns = new Dictionary<(string Schema, string Table), List<ColumnInfo>>();
        await using (var cmd = new SqlCommand(ColumnSql, conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var key = (reader.GetString(0), reader.GetString(1));
                if (!columns.TryGetValue(key, out var list))
                    columns[key] = list = [];

                list.Add(new ColumnInfo(
                    Name: reader.GetString(2),
                    DataType: reader.GetString(3),
                    MaxLength: reader.GetInt32(4),
                    Precision: reader.GetByte(5),
                    Scale: reader.GetByte(6),
                    IsNullable: reader.GetBoolean(7),
                    IsIdentity: reader.GetBoolean(8),
                    HasDefault: reader.GetInt32(9) == 1,
                    OrdinalPosition: reader.GetInt32(10)));
            }
        }

        var keys = new Dictionary<(string Schema, string Table), List<string>>();
        await using (var cmd = new SqlCommand(PrimaryKeySql, conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var key = (reader.GetString(0), reader.GetString(1));
                if (!keys.TryGetValue(key, out var list))
                    keys[key] = list = [];
                list.Add(reader.GetString(2));
            }
        }

        var foreignKeys = new List<ForeignKeyInfo>();
        await using (var cmd = new SqlCommand(ForeignKeySql, conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                foreignKeys.Add(new ForeignKeyInfo(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4)));
            }
        }

        var tables = columns
            .Select(kv => new TableInfo(
                kv.Key.Schema,
                kv.Key.Table,
                kv.Value,
                keys.TryGetValue(kv.Key, out var pk) ? pk : []))
            .OrderBy(t => t.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DbSchema(conn.Database, tables, foreignKeys);
    }
}
