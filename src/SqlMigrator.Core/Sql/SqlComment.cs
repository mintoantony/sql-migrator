namespace SqlMigrator.Core.Sql;

/// <summary>
/// Sanitizes values embedded inside a single-line "--" T-SQL comment. This is the only place
/// comment sanitization happens in the solution; every value the generator interpolates into a
/// "--" comment line must route through here. A "--" comment ends at the first CR or LF in the
/// batch, so a value that is allowed to carry either one can close the comment early and let
/// whatever follows run as a live statement — outside any comment, and in the generator's script
/// header, above <c>BEGIN TRANSACTION</c>. These values are purely informational (nothing
/// downstream parses them back out), so CR and LF are stripped rather than the value being
/// rejected outright: a "--" comment is one line by definition, so collapsing an embedded
/// newline away loses nothing a reviewer needs to see.
/// </summary>
public static class SqlComment
{
    /// <summary>Removes CR and LF so a value cannot terminate the "--" comment line it is embedded in.</summary>
    public static string Sanitize(string value) => value.Replace("\r", "").Replace("\n", "");
}
