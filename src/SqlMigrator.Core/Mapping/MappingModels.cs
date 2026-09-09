namespace SqlMigrator.Core.Mapping;

/// <summary>The model's stated intent. Documentation and UI grouping only — the generator uses Expression.</summary>
public enum RuleKind { Copy, Truncate, Concat, Split, Case, Constant }

/// <summary>Who authored this line: the model, or a person who edited it.</summary>
public enum Origin { Ai, Human }

public sealed record ColumnMapping(
    string TargetColumn,
    RuleKind Rule,
    string Expression,
    Origin Origin,
    double? Confidence = null,
    string? Reason = null);

public sealed record UnmappedColumn(string TargetColumn, string Reason);

public sealed record TableMapping(
    string SourceTable,
    string TargetTable,
    IReadOnlyList<ColumnMapping> Columns,
    IReadOnlyList<UnmappedColumn> Unmapped,
    Origin Origin,
    double? Confidence = null,
    string? Reason = null)
{
    public bool Equals(TableMapping? other) =>
        other is not null
        && SourceTable == other.SourceTable
        && TargetTable == other.TargetTable
        && Origin == other.Origin
        && Confidence == other.Confidence
        && Reason == other.Reason
        && Columns.SequenceEqual(other.Columns)
        && Unmapped.SequenceEqual(other.Unmapped);

    public override int GetHashCode() => HashCode.Combine(SourceTable, TargetTable, Origin);
}

public sealed record MigrationMapping(
    string Name,
    DateTimeOffset GeneratedUtc,
    string? Model,
    string SourceDatabase,
    string SourceReference,
    string TargetDatabase,
    IReadOnlyList<TableMapping> Tables)
{
    public bool Equals(MigrationMapping? other) =>
        other is not null
        && Name == other.Name
        && GeneratedUtc == other.GeneratedUtc
        && Model == other.Model
        && SourceDatabase == other.SourceDatabase
        && SourceReference == other.SourceReference
        && TargetDatabase == other.TargetDatabase
        && Tables.SequenceEqual(other.Tables);

    public override int GetHashCode() => HashCode.Combine(Name, GeneratedUtc, SourceDatabase, TargetDatabase);
}
