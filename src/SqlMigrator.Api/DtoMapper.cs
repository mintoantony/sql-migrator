using SqlMigrator.Core.Validation;
using SqlMigrator.Model.Mapping;

namespace SqlMigrator.Api;

public static class DtoMapper
{
    public static IssueDto ToDto(Issue issue) =>
        new(issue.Code, issue.Severity.ToString(), issue.Message, issue.Table, issue.Column);

    public static MappingDto ToDto(MigrationMapping mapping) => new(
        mapping.Name, mapping.Model, mapping.SourceDatabase, mapping.SourceReference, mapping.TargetDatabase,
        mapping.Tables.Select(t => new TableDto(
            t.SourceTable, t.TargetTable, t.Origin.ToString().ToLowerInvariant(), t.Confidence, t.Reason,
            t.Columns.Select(c => new ColumnDto(
                c.TargetColumn, c.Rule.ToString().ToLowerInvariant(), c.Expression,
                c.Origin.ToString().ToLowerInvariant(), c.Confidence, c.Reason)).ToList(),
            t.Unmapped.Select(u => new UnmappedDto(u.TargetColumn, u.Reason)).ToList())).ToList());

    public static MigrationMapping ToDomain(MappingDto dto, DateTimeOffset generatedUtc) => new(
        dto.Name, generatedUtc, dto.Model, dto.SourceDatabase, dto.SourceReference, dto.TargetDatabase,
        dto.Tables.Select(t => new TableMapping(
            t.SourceTable, t.TargetTable,
            t.Columns.Select(c => new ColumnMapping(
                c.TargetColumn,
                Enum.TryParse<RuleKind>(c.Rule, true, out var rule) ? rule : RuleKind.Copy,
                c.Expression,
                Enum.TryParse<Origin>(c.Origin, true, out var origin) ? origin : Origin.Human,
                c.Confidence, c.Reason)).ToList(),
            t.Unmapped.Select(u => new UnmappedColumn(u.TargetColumn, u.Reason)).ToList(),
            Enum.TryParse<Origin>(t.Origin, true, out var tableOrigin) ? tableOrigin : Origin.Human,
            t.Confidence, t.Reason)).ToList());
}
