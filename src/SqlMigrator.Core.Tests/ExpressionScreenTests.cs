using SqlMigrator.Core.Validation;

namespace SqlMigrator.Core.Tests;

public class ExpressionScreenTests
{
    [Theory]
    [InlineData("CustomerId")]
    [InlineData("LEFT(LastName, 20)")]
    [InlineData("CONCAT(FirstName, ' ', LastName)")]
    [InlineData("CASE WHEN IsActive = 1 THEN 1 ELSE 0 END")]
    [InlineData("SYSUTCDATETIME()")]
    [InlineData("SUBSTRING(Email, CHARINDEX('@', Email) + 1, 100)")]
    [InlineData("'literal with ; semicolon inside a string'")]
    public void Accepts_legitimate_expressions(string expression) =>
        Assert.True(ExpressionScreen.Screen(expression).Ok);

    [Theory]
    [InlineData("1; DROP TABLE dbo.Customer", "terminator")]
    [InlineData("1 -- and a comment", "comment")]
    [InlineData("1 /* block */", "comment")]
    [InlineData("(SELECT 1) EXEC sp_who", "EXEC")]
    [InlineData("DELETE FROM dbo.Customer", "DELETE")]
    [InlineData("UPDATE dbo.Customer SET Qty = 1", "UPDATE")]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    public void Rejects_anything_that_is_not_an_expression(string expression, string expectedInReason)
    {
        var result = ExpressionScreen.Screen(expression);

        Assert.False(result.Ok);
        Assert.Contains(expectedInReason, result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_unbalanced_parentheses()
    {
        var result = ExpressionScreen.Screen("LEFT(LastName, 20");

        Assert.False(result.Ok);
        Assert.Contains("parenthes", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("LAST_UPDATE")]
    [InlineData("UPDATE_FLAG")]
    [InlineData("@EXEC_TIME")]
    [InlineData("[Update]")]
    [InlineData("[Delete]")]
    [InlineData("[weird;name]")]
    [InlineData("LEFT([Last Name], 20)")]
    public void Accepts_expressions_with_keywords_in_identifiers_or_bracketed_names(string expression) =>
        Assert.True(ExpressionScreen.Screen(expression).Ok);

    [Theory]
    [InlineData("UPDATE", "UPDATE")]
    [InlineData("EXEC sp_who", "EXEC")]
    public void Still_rejects_bare_forbidden_keywords(string expression, string expectedKeyword)
    {
        var result = ExpressionScreen.Screen(expression);

        Assert.False(result.Ok);
        Assert.Contains(expectedKeyword, result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_unterminated_bracketed_identifier()
    {
        var result = ExpressionScreen.Screen("[Name");

        Assert.False(result.Ok);
        Assert.Contains("bracketed identifier", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_semicolon_outside_brackets()
    {
        var result = ExpressionScreen.Screen("1; DROP TABLE dbo.Customer");

        Assert.False(result.Ok);
        Assert.Contains("terminator", result.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
