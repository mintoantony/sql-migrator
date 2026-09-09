using System.Text.RegularExpressions;

namespace SqlMigrator.Core.Sql;

/// <summary>
/// Quotes T-SQL identifiers. This is the only place identifier quoting happens in the
/// solution; every identifier that reaches generated SQL text or a compiled expression
/// (directly, or via <see cref="Quote"/> / <see cref="QuoteSourceReference"/>) must route
/// through here. A name is structural, so it cannot be passed as a parameter — it is
/// interpolated into command text. That makes this escaping the only thing between a
/// caller-supplied name and arbitrary SQL, so it follows the T-SQL rule exactly: a ] inside
/// a bracket-quoted identifier is escaped by doubling it.
/// </summary>
public static partial class SqlIdentifier
{
    // One or two bracket-quoted parts, e.g. [SomeDb] or [SRCLINK].[SomeDb]. A source
    // reference is not free text — spec §2.5 fixes its shape — so anything that is not
    // exactly this is refused before any SQL is built, rather than sanitised.
    [GeneratedRegex(@"^\[[^\[\]]+\](\.\[[^\[\]]+\])?$", RegexOptions.None)]
    private static partial Regex ValidSourceReferenceRegex();

    /// <summary>
    /// Quotes a two-part (schema.table) or one-part (defaults to dbo) SQL name.
    /// </summary>
    public static string Quote(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            throw new ArgumentException("Table name must not be empty.", nameof(fullName));

        var parts = fullName.Split('.', 2);
        var schema = parts.Length == 2 ? parts[0] : "dbo";
        var table = parts.Length == 2 ? parts[1] : fullName;

        return $"{QuoteIdentifier(schema, nameof(fullName))}.{QuoteIdentifier(table, nameof(fullName))}";
    }

    /// <summary>
    /// Quotes a single T-SQL identifier. Strips at most one matched pair of surrounding
    /// brackets (so a caller-supplied "[Name]" is not double-bracketed), then escapes every
    /// remaining ']' by doubling it, per the T-SQL rule for bracket-quoted identifiers.
    /// Trimming the brackets off an already-quoted name is not enough on its own:
    /// "Customer]; DROP" would survive a trim and close the identifier early — the doubling
    /// below is what keeps it inside the identifier.
    /// </summary>
    public static string QuoteIdentifier(string identifier) => QuoteIdentifier(identifier, nameof(identifier));

    private static string QuoteIdentifier(string part, string paramName)
    {
        var bare = part.Trim();

        if (bare.Length >= 2 && bare[0] == '[' && bare[^1] == ']')
            bare = bare[1..^1];

        if (string.IsNullOrWhiteSpace(bare))
            throw new ArgumentException($"Table name part '{part}' is empty.", paramName);

        return $"[{bare.Replace("]", "]]")}]";
    }

    /// <summary>
    /// True if <paramref name="sourceReference"/> is a legitimate source reference: one or
    /// two bracket-quoted parts (a same-instance database, or [LinkedServer].[Database]).
    /// </summary>
    public static bool IsValidSourceReference(string? sourceReference) =>
        !string.IsNullOrWhiteSpace(sourceReference) && ValidSourceReferenceRegex().IsMatch(sourceReference);

    /// <summary>
    /// Validates a source reference structurally and re-quotes each of its parts. A source
    /// reference is legitimately a one- or two-part database/linked-server name — it cannot
    /// simply be bracket-quoted like an ordinary identifier, because the brackets it already
    /// carries are the delimiters, not payload. Anything that is not exactly that shape is
    /// rejected with <see cref="ArgumentException"/> rather than sanitised.
    /// </summary>
    public static string QuoteSourceReference(string sourceReference)
    {
        if (!IsValidSourceReference(sourceReference))
        {
            throw new ArgumentException(
                $"Source reference '{sourceReference}' must be one or two bracket-quoted parts, " +
                "e.g. [Database] or [LinkedServer].[Database].",
                nameof(sourceReference));
        }

        var parts = sourceReference.Split('.', 2);
        return parts.Length == 2
            ? $"{QuoteIdentifier(parts[0])}.{QuoteIdentifier(parts[1])}"
            : QuoteIdentifier(parts[0]);
    }
}
