using SqlMigrator.Model.Mapping;
using SqlMigrator.Model.Schema;

namespace SqlMigrator.Core.Validation;

public sealed class MappingValidator(DbSchema source, DbSchema target, IExpressionCompiler compiler)
{
    public async Task<IReadOnlyList<Issue>> ValidateAsync(
        MigrationMapping mapping, CancellationToken ct = default)
    {
        var issues = new List<Issue>();

        foreach (var table in mapping.Tables)
        {
            var sourceTable = source.Find(table.SourceTable);
            var targetTable = target.Find(table.TargetTable);

            if (sourceTable is null)
            {
                issues.Add(new Issue("MAP002", Severity.Blocking,
                    $"Source table {table.SourceTable} does not exist.", table.SourceTable));
                continue;
            }
            if (targetTable is null)
            {
                issues.Add(new Issue("MAP002", Severity.Blocking,
                    $"Target table {table.TargetTable} does not exist.", table.TargetTable));
                continue;
            }

            issues.AddRange(DuplicateTargets(table));
            issues.AddRange(await ColumnIssues(table, sourceTable, targetTable, ct));
            issues.AddRange(RequiredButUnmapped(table, targetTable));

            if (targetTable.Columns.Any(c => c.IsIdentity && table.Columns.Any(m =>
                    string.Equals(m.TargetColumn, c.Name, StringComparison.OrdinalIgnoreCase))))
            {
                issues.Add(new Issue("IDN001", Severity.Info,
                    $"IDENTITY_INSERT will be used on {table.TargetTable}.", table.TargetTable));
            }
        }

        issues.AddRange(UnmatchedSourceTables(mapping));
        return issues;
    }

    private static IEnumerable<Issue> DuplicateTargets(TableMapping table) =>
        table.Columns
            .GroupBy(c => c.TargetColumn, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => new Issue("MAP001", Severity.Blocking,
                $"Target column {g.Key} is mapped {g.Count()} times.", table.TargetTable, g.Key));

    private async Task<List<Issue>> ColumnIssues(
        TableMapping table, TableInfo sourceTable, TableInfo targetTable, CancellationToken ct)
    {
        var issues = new List<Issue>();

        foreach (var column in table.Columns)
        {
            var targetColumn = targetTable.Column(column.TargetColumn);
            if (targetColumn is null)
            {
                issues.Add(new Issue("MAP003", Severity.Blocking,
                    $"Target column {column.TargetColumn} does not exist on {table.TargetTable}.",
                    table.TargetTable, column.TargetColumn));
                continue;
            }

            var screen = ExpressionScreen.Screen(column.Expression);
            if (!screen.Ok)
            {
                issues.Add(new Issue("EXP001", Severity.Blocking,
                    $"{screen.Reason} Expression: {column.Expression}",
                    table.TargetTable, column.TargetColumn));
                continue;
            }

            var compiled = await compiler.CompileAsync(sourceTable.FullName, column.Expression, ct);
            if (!compiled.Ok)
            {
                issues.Add(new Issue("EXP002", Severity.Blocking,
                    $"Expression did not compile: {compiled.Error}",
                    table.TargetTable, column.TargetColumn));
                continue;
            }

            var check = TypeCompatibility.Check(compiled.ResultType!, targetColumn);
            if (!check.Compatible)
            {
                issues.Add(new Issue("TYP001", Severity.Blocking, check.Message!,
                    table.TargetTable, column.TargetColumn));
            }
            else if (check.Narrowing)
            {
                issues.Add(new Issue("TYP002", Severity.Warning, check.Message!,
                    table.TargetTable, column.TargetColumn));
            }
        }

        return issues;
    }

    private static IEnumerable<Issue> RequiredButUnmapped(TableMapping table, TableInfo targetTable)
    {
        var mapped = table.Columns
            .Select(c => c.TargetColumn)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return targetTable.Columns
            .Where(c => !c.IsNullable && !c.HasDefault && !c.IsIdentity && !mapped.Contains(c.Name))
            .Select(c => new Issue("NUL001", Severity.Blocking,
                $"{table.TargetTable}.{c.Name} is NOT NULL with no default and nothing is mapped to it.",
                table.TargetTable, c.Name));
    }

    private IEnumerable<Issue> UnmatchedSourceTables(MigrationMapping mapping)
    {
        var mapped = mapping.Tables
            .Select(t => t.SourceTable)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return source.Tables
            .Where(t => !mapped.Contains(t.FullName))
            .Select(t => new Issue("SRC001", Severity.Warning,
                $"Source table {t.FullName} is not matched to any target table.", t.FullName));
    }
}
