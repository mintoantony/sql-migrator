using System.Text.RegularExpressions;

namespace SqlMigrator.Api;

/// <summary>
/// Builds the on-disk path for a saved mapping XML file. <see cref="BuildPath"/> is the only
/// place <c>TargetDatabase</c> is turned into a filename.
///
/// Rejects rather than sanitises, mirroring <see cref="ConnectionStrings"/>: a
/// <c>TargetDatabase</c> containing a path separator, "..", a drive letter or a rooted path is
/// refused outright rather than cleaned up, so a future edit that loosens the regex below can't
/// silently reopen the hole. The resulting full path is also checked against the target
/// directory as a second, independent guard — belt and braces — so even a value that slipped
/// past validation cannot land outside the mappings directory.
/// </summary>
public static class MappingFileNames
{
    private const int MaxLength = 128;

    // A plain SQL Server database identifier for filename purposes: letters, digits, underscore,
    // hyphen and space, starting with a letter, digit or underscore. No '\', '/', ':' or '..' can
    // match this, so no path separator, drive letter, UNC prefix or traversal segment gets through.
    private static readonly Regex ValidDatabaseName =
        new("^[A-Za-z0-9_][A-Za-z0-9_ -]{0,127}$", RegexOptions.Compiled);

    public static string BuildPath(string mappingsDirectory, string targetDatabase, DateTimeOffset timestampUtc)
    {
        if (string.IsNullOrWhiteSpace(targetDatabase))
            throw new ArgumentException("'TargetDatabase' must not be empty or whitespace.", nameof(targetDatabase));

        if (targetDatabase.Length > MaxLength)
            throw new ArgumentException(
                $"'TargetDatabase' exceeds the maximum allowed length of {MaxLength} characters.",
                nameof(targetDatabase));

        if (!ValidDatabaseName.IsMatch(targetDatabase))
            throw new ArgumentException(
                "'TargetDatabase' must be a plain database name (letters, digits, underscore, " +
                "hyphen or space only) — no path separators, no '..' and no drive letters.",
                nameof(targetDatabase));

        var fileName = $"{targetDatabase}-{timestampUtc:yyyyMMdd-HHmmss}.xml";
        var fullDirectory = Path.GetFullPath(mappingsDirectory);
        var fullPath = Path.GetFullPath(Path.Combine(fullDirectory, fileName));

        // Second, independent guard: even if the regex above were ever loosened, refuse to write
        // anywhere the resolved path does not land inside the mappings directory.
        if (!fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("'TargetDatabase' does not resolve to a valid file path.",
                nameof(targetDatabase));

        return fullPath;
    }
}
