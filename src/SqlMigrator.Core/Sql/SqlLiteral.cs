namespace SqlMigrator.Core.Sql;

/// <summary>
/// Escapes values embedded inside a single-quoted T-SQL string literal. This is the only
/// place literal escaping happens in the solution; every value the generator embeds inside
/// a string literal must route through here.
/// </summary>
public static class SqlLiteral
{
    /// <summary>Escapes a value for embedding inside a single-quoted T-SQL string literal.</summary>
    public static string Escape(string value) => value.Replace("'", "''");
}
