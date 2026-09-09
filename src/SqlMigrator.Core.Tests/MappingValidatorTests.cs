using SqlMigrator.Core.Schema;
using SqlMigrator.Core.Validation;
using SqlMigrator.Model.Mapping;
using SqlMigrator.Model.Schema;

namespace SqlMigrator.Core.Tests;

[Collection("sql")]
public class MappingValidatorTests
{
    private static async Task<(DbSchema Source, DbSchema Target, IExpressionCompiler Compiler)> Context()
    {
        TestDatabases.EnsureCreated();
        return (
            await SchemaReader.ReadAsync(TestDatabases.SourceConnectionString),
            await SchemaReader.ReadAsync(TestDatabases.TargetConnectionString),
            new SqlExpressionCompiler(TestDatabases.SourceConnectionString));
    }

    private static MigrationMapping MappingWith(params ColumnMapping[] columns) => new(
        "T", DateTimeOffset.UnixEpoch, null,
        TestDatabases.SourceDb, $"[{TestDatabases.SourceDb}]", TestDatabases.TargetDb,
        [new TableMapping("dbo.Customer", "dbo.Client", columns, [], Origin.Ai, 1.0, null)]);

    [Fact]
    public async Task Reports_a_blocking_issue_for_an_unmapped_required_column()
    {
        var (source, target, compiler) = await Context();
        var mapping = MappingWith(new ColumnMapping("FullName", RuleKind.Copy, "FirstName", Origin.Ai));

        var issues = await new MappingValidator(source, target, compiler).ValidateAsync(mapping);

        var issue = issues.Single(i => i.Code == "NUL001" && i.Column == "ShortName");
        Assert.Equal(Severity.Blocking, issue.Severity);
        Assert.True(issues.HasBlocking());
    }

    [Fact]
    public async Task Reports_a_blocking_issue_for_an_expression_the_screen_rejects()
    {
        var (source, target, compiler) = await Context();
        var mapping = MappingWith(new ColumnMapping("FullName", RuleKind.Copy, "FirstName; DROP TABLE dbo.Customer", Origin.Ai));

        var issues = await new MappingValidator(source, target, compiler).ValidateAsync(mapping);

        Assert.Contains(issues, i => i.Code == "EXP001" && i.Severity == Severity.Blocking);
    }

    [Fact]
    public async Task Reports_a_blocking_issue_for_an_expression_that_does_not_compile()
    {
        var (source, target, compiler) = await Context();
        var mapping = MappingWith(new ColumnMapping("FullName", RuleKind.Copy, "NoSuchColumn", Origin.Ai));

        var issues = await new MappingValidator(source, target, compiler).ValidateAsync(mapping);

        var issue = issues.Single(i => i.Code == "EXP002");
        Assert.Equal(Severity.Blocking, issue.Severity);
        Assert.Contains("NoSuchColumn", issue.Message);
    }

    [Fact]
    public async Task Reports_a_warning_for_a_narrowing_conversion()
    {
        var (source, target, compiler) = await Context();
        var mapping = MappingWith(new ColumnMapping("Summary", RuleKind.Copy, "Description", Origin.Ai));

        var issues = await new MappingValidator(source, target, compiler).ValidateAsync(mapping);

        Assert.Contains(issues, i => i.Code == "TYP002" && i.Severity == Severity.Warning && i.Column == "Summary");
    }

    [Fact]
    public async Task Reports_a_blocking_issue_for_an_incompatible_type()
    {
        var (source, target, compiler) = await Context();
        var mapping = MappingWith(new ColumnMapping("CreatedUtc", RuleKind.Copy, "FirstName", Origin.Ai));

        var issues = await new MappingValidator(source, target, compiler).ValidateAsync(mapping);

        Assert.Contains(issues, i => i.Code == "TYP001" && i.Severity == Severity.Blocking);
    }

    [Fact]
    public async Task Reports_a_blocking_issue_for_a_duplicated_target_column()
    {
        var (source, target, compiler) = await Context();
        var mapping = MappingWith(
            new ColumnMapping("FullName", RuleKind.Copy, "FirstName", Origin.Ai),
            new ColumnMapping("FullName", RuleKind.Copy, "LastName", Origin.Ai));

        var issues = await new MappingValidator(source, target, compiler).ValidateAsync(mapping);

        Assert.Contains(issues, i => i.Code == "MAP001" && i.Severity == Severity.Blocking);
    }

    [Fact]
    public async Task Reports_informational_identity_insert_and_a_warning_for_an_unmatched_source_table()
    {
        var (source, target, compiler) = await Context();
        var mapping = MappingWith(new ColumnMapping("ClientId", RuleKind.Copy, "CustomerId", Origin.Ai));

        var issues = await new MappingValidator(source, target, compiler).ValidateAsync(mapping);

        Assert.Contains(issues, i => i.Code == "IDN001" && i.Severity == Severity.Info);
        Assert.Contains(issues, i => i.Code == "SRC001" && i.Severity == Severity.Warning && i.Table == "dbo.Order");
    }

    [Fact]
    public async Task A_fully_correct_mapping_produces_no_blocking_issues()
    {
        var (source, target, compiler) = await Context();
        var mapping = new MigrationMapping(
            "T", DateTimeOffset.UnixEpoch, null,
            TestDatabases.SourceDb, $"[{TestDatabases.SourceDb}]", TestDatabases.TargetDb,
            [DemoMapping.CustomerToClient(), DemoMapping.OrderToOrder(), DemoMapping.OrderLineToOrderLine()]);

        var issues = await new MappingValidator(source, target, compiler).ValidateAsync(mapping);

        Assert.False(issues.HasBlocking());
    }
}
