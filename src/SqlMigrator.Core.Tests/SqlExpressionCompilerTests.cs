using SqlMigrator.Core.Validation;

namespace SqlMigrator.Core.Tests;

[Collection("sql")]
public class SqlExpressionCompilerTests
{
    private static SqlExpressionCompiler Compiler()
    {
        TestDatabases.EnsureCreated();
        return new SqlExpressionCompiler(TestDatabases.SourceConnectionString);
    }

    [Fact]
    public async Task Plain_column_yields_its_own_type()
    {
        var result = await Compiler().CompileAsync("dbo.Customer", "Email");

        Assert.True(result.Ok);
        Assert.Equal("nvarchar", result.ResultType!.DataType);
        Assert.Equal(200, result.ResultType.MaxLength);
    }

    [Fact]
    public async Task Concat_yields_a_string()
    {
        var result = await Compiler().CompileAsync("dbo.Customer", "CONCAT(FirstName, ' ', LastName)");

        Assert.True(result.Ok);
        Assert.Equal(TypeCategory.String, TypeCompatibility.CategoryOf(result.ResultType!.DataType));
    }

    [Fact]
    public async Task Left_reports_the_truncated_length()
    {
        var result = await Compiler().CompileAsync("dbo.Customer", "LEFT(LastName, 20)");

        Assert.True(result.Ok);
        Assert.Equal(20, result.ResultType!.MaxLength);
    }

    [Fact]
    public async Task Case_expression_yields_an_integer()
    {
        var result = await Compiler().CompileAsync("dbo.Customer", "CASE WHEN IsActive = 1 THEN 1 ELSE 0 END");

        Assert.True(result.Ok);
        Assert.Equal(TypeCategory.Integer, TypeCompatibility.CategoryOf(result.ResultType!.DataType));
    }

    [Fact]
    public async Task Unknown_column_fails_with_the_server_message()
    {
        var result = await Compiler().CompileAsync("dbo.Customer", "CustomerName");

        Assert.False(result.Ok);
        Assert.Contains("CustomerName", result.Error);
        Assert.Null(result.ResultType);
    }

    [Fact]
    public async Task Syntax_error_fails_rather_than_throwing()
    {
        var result = await Compiler().CompileAsync("dbo.Customer", "LEFT(LastName");

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Unknown_table_fails_cleanly()
    {
        var result = await Compiler().CompileAsync("dbo.NoSuchTable", "1");

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }

    /// <summary>
    /// A CompileResult failure must mean 'SQL Server rejected this expression' and nothing
    /// else. An unreachable server raises SqlException too, so if the connection open were
    /// inside the same catch, a database outage would be reported as a bad expression and
    /// send a human to rewrite perfectly good SQL. This pins the distinction.
    /// </summary>
    [Fact]
    public async Task An_unreachable_server_throws_rather_than_reporting_a_bad_expression()
    {
        var unreachable = new SqlExpressionCompiler(
            "Server=localhost,14330;Database=NoSuchDb;Integrated Security=True;" +
            "TrustServerCertificate=True;Connect Timeout=2");

        await Assert.ThrowsAnyAsync<Exception>(
            () => unreachable.CompileAsync("dbo.Customer", "Email"));
    }
}
