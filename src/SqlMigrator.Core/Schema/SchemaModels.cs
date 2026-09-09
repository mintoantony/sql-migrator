namespace SqlMigrator.Core.Schema;

/// <summary>A column as SQL Server describes it. MaxLength is in characters for string types, -1 for MAX.</summary>
public sealed record ColumnInfo(
    string Name,
    string DataType,
    int MaxLength,
    byte Precision,
    byte Scale,
    bool IsNullable,
    bool IsIdentity,
    bool HasDefault,
    int OrdinalPosition);

/// <summary>Parent is the referencing (child) table; Referenced is the table it points at.</summary>
public sealed record ForeignKeyInfo(
    string Name,
    string ParentSchema,
    string ParentTable,
    string ReferencedSchema,
    string ReferencedTable)
{
    public string ParentFullName => $"{ParentSchema}.{ParentTable}";
    public string ReferencedFullName => $"{ReferencedSchema}.{ReferencedTable}";
}

public sealed record TableInfo(
    string Schema,
    string Name,
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<string> PrimaryKeyColumns)
{
    public string FullName => $"{Schema}.{Name}";

    public ColumnInfo? Column(string name) =>
        Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

public sealed record DbSchema(
    string DatabaseName,
    IReadOnlyList<TableInfo> Tables,
    IReadOnlyList<ForeignKeyInfo> ForeignKeys)
{
    public TableInfo? Find(string fullName) =>
        Tables.FirstOrDefault(t => string.Equals(t.FullName, fullName, StringComparison.OrdinalIgnoreCase));
}
