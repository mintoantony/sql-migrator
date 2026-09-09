using SqlMigrator.Core.Sql;

namespace SqlMigrator.Core.Tests;

/// <summary>
/// The source-reference regex is private, but its behaviour is fully observable through
/// <see cref="SqlIdentifier.IsValidSourceReference"/> and <see cref="SqlIdentifier.QuoteSourceReference"/>.
/// These tests exercise the regex directly through those two entry points, without going
/// through <c>ScriptGenerator</c>, so a trailing newline is judged purely on the regex's own
/// anchoring rather than being masked by <c>QuoteIdentifier</c>'s incidental <c>Trim()</c>.
/// </summary>
public class SqlIdentifierTests
{
    [Theory]
    [InlineData("[SqlMigratorDemo_Source]")]
    [InlineData("[LINKEDSRV].[SqlMigratorDemo_Source]")]
    public void Well_formed_source_references_are_valid(string reference)
    {
        Assert.True(SqlIdentifier.IsValidSourceReference(reference));
        Assert.Equal(reference, SqlIdentifier.QuoteSourceReference(reference));
    }

    /// <summary>
    /// Pins the anchor fix. `^...$` in .NET matches `$` immediately before a trailing '\n', so
    /// "[db]\n" used to satisfy a regex that claims to allow only "[db]" — safe in practice only
    /// because a different method, <c>QuoteIdentifier</c>, happens to <c>Trim()</c> its input.
    /// `\A...\z` anchors to the true start and end of the string, so this now agrees with what
    /// the regex is documented to accept, without depending on that incidental trimming.
    /// </summary>
    [Theory]
    [InlineData("[SqlMigratorDemo_Source]\n")]
    [InlineData("[SqlMigratorDemo_Source]\r\n")]
    [InlineData("[SqlMigratorDemo_Source]\n\n")]
    [InlineData("\n[SqlMigratorDemo_Source]")]
    public void A_source_reference_with_a_trailing_or_leading_newline_is_not_valid(string reference)
    {
        Assert.False(SqlIdentifier.IsValidSourceReference(reference));
        Assert.Throws<ArgumentException>(() => SqlIdentifier.QuoteSourceReference(reference));
    }
}
