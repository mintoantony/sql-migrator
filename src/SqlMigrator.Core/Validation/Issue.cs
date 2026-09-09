namespace SqlMigrator.Core.Validation;

public enum Severity { Blocking, Warning, Info }

public sealed record Issue(
    string Code,
    Severity Severity,
    string Message,
    string? Table = null,
    string? Column = null);

public static class IssueExtensions
{
    public static bool HasBlocking(this IEnumerable<Issue> issues) =>
        issues.Any(i => i.Severity == Severity.Blocking);
}
