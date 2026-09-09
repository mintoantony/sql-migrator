using SqlMigrator.Core.Validation;

namespace SqlMigrator.Core.Tests;

/// <summary>
/// The table name reaching SqlExpressionCompiler is structural, so it cannot be a
/// parameter — it is interpolated straight into the command text, and this class is
/// reachable from an API endpoint. These tests assert on the emitted identifier rather
/// than round-tripping through a server, so they are fast and need no database.
/// </summary>
public class SqlIdentifierQuotingTests
{
    [Fact]
    public void Plain_two_part_name_is_bracketed()
    {
        Assert.Equal("[dbo].[Order]", SqlExpressionCompiler.Quote("dbo.Order"));
    }

    [Fact]
    public void Name_without_a_schema_defaults_to_dbo()
    {
        Assert.Equal("[dbo].[Customer]", SqlExpressionCompiler.Quote("Customer"));
    }

    [Fact]
    public void Already_bracketed_name_is_not_double_wrapped()
    {
        Assert.Equal("[dbo].[Order]", SqlExpressionCompiler.Quote("[dbo].[Order]"));
    }

    /// <summary>
    /// The injection this class was found vulnerable to. A closing bracket inside the
    /// name used to end the identifier early, so everything after it became a second
    /// statement in the same batch. Doubling the bracket keeps it inside the identifier.
    /// </summary>
    [Fact]
    public void Closing_bracket_inside_the_name_cannot_end_the_identifier()
    {
        var quoted = SqlExpressionCompiler.Quote("dbo.Customer]; SELECT 1 AS Pwn --");

        Assert.Equal("[dbo].[Customer]]; SELECT 1 AS Pwn --]", quoted);

        // The proof: exactly one identifier is opened and closed. An unescaped ] would
        // leave an odd number of brackets and let the tail escape into executable SQL.
        Assert.Equal(2, quoted.Count(c => c == '['));
        Assert.Equal(4, quoted.Count(c => c == ']'));
    }

    [Fact]
    public void Drop_table_payload_stays_inside_the_identifier()
    {
        var quoted = SqlExpressionCompiler.Quote("dbo.Customer]; DROP TABLE Orders --");

        Assert.Equal("[dbo].[Customer]]; DROP TABLE Orders --]", quoted);
        Assert.DoesNotContain("]; DROP", quoted[1..^1].Replace("]]", string.Empty));
    }

    [Fact]
    public void Bracket_in_the_schema_part_is_escaped_too()
    {
        Assert.Equal("[ev]]il].[Customer]", SqlExpressionCompiler.Quote("ev]il.Customer"));
    }

    [Fact]
    public void Newline_in_the_name_is_kept_inside_the_identifier()
    {
        var quoted = SqlExpressionCompiler.Quote("dbo.Cus\ntomer");

        Assert.Equal("[dbo].[Cus\ntomer]", quoted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("dbo.")]
    [InlineData(".Customer")]
    [InlineData("[].[Customer]")]
    public void Empty_or_partial_names_are_refused_before_any_sql_is_built(string name)
    {
        Assert.Throws<ArgumentException>(() => SqlExpressionCompiler.Quote(name));
    }
}
