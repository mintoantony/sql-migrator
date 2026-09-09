using System.Text;

namespace SqlMigrator.Core.Validation;

public sealed record ScreenResult(bool Ok, string? Reason)
{
    public static readonly ScreenResult Pass = new(true, null);
    public static ScreenResult Fail(string reason) => new(false, reason);
}

/// <summary>
/// Gate 1: an expression must stay an expression. Rejects statement terminators,
/// comments and DDL/DML keywords — while ignoring the contents of string literals,
/// where a semicolon or a double dash is perfectly legitimate.
/// </summary>
public static class ExpressionScreen
{
    private static readonly string[] ForbiddenKeywords =
    [
        "INSERT", "UPDATE", "DELETE", "MERGE", "DROP", "ALTER", "CREATE", "TRUNCATE",
        "EXEC", "EXECUTE", "GRANT", "REVOKE", "BACKUP", "RESTORE", "SHUTDOWN", "WAITFOR"
    ];

    public static ScreenResult Screen(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return ScreenResult.Fail("Expression is empty.");

        var outsideLiterals = StripLiteralsAndIdentifiers(expression, out var unternatedStringLiteral, out var unterminatedIdentifier);
        if (unternatedStringLiteral)
            return ScreenResult.Fail("Expression has an unterminated string literal.");
        if (unterminatedIdentifier)
            return ScreenResult.Fail("Expression has an unterminated bracketed identifier.");

        if (outsideLiterals.Contains(';'))
            return ScreenResult.Fail("Expression contains a statement terminator (';').");

        if (outsideLiterals.Contains("--") || outsideLiterals.Contains("/*") || outsideLiterals.Contains("*/"))
            return ScreenResult.Fail("Expression contains a comment.");

        foreach (var keyword in ForbiddenKeywords)
        {
            if (ContainsWord(outsideLiterals, keyword))
                return ScreenResult.Fail($"Expression contains the forbidden keyword '{keyword}'.");
        }

        var depth = 0;
        foreach (var c in outsideLiterals)
        {
            if (c == '(') depth++;
            else if (c == ')' && --depth < 0) break;
        }
        if (depth != 0)
            return ScreenResult.Fail("Expression has unbalanced parentheses.");

        return ScreenResult.Pass;
    }

    /// <summary>Replaces the body of every '...' literal and [...] identifier with spaces, preserving offsets.</summary>
    private static string StripLiteralsAndIdentifiers(string expression, out bool unterminatedStringLiteral, out bool unterminatedIdentifier)
    {
        var builder = new StringBuilder(expression.Length);
        var inStringLiteral = false;
        var inBracketedIdentifier = false;

        for (var i = 0; i < expression.Length; i++)
        {
            var c = expression[i];

            if (c == '\'' && !inBracketedIdentifier)
            {
                // '' inside a string literal is an escaped quote, not a close.
                if (inStringLiteral && i + 1 < expression.Length && expression[i + 1] == '\'')
                {
                    builder.Append("  ");
                    i++;
                    continue;
                }
                inStringLiteral = !inStringLiteral;
                builder.Append(' ');
                continue;
            }

            if (c == '[' && !inStringLiteral)
            {
                inBracketedIdentifier = true;
                builder.Append(' ');
                continue;
            }

            if (c == ']' && inBracketedIdentifier && !inStringLiteral)
            {
                // ]] inside a bracketed identifier is an escaped bracket, not a close.
                if (i + 1 < expression.Length && expression[i + 1] == ']')
                {
                    builder.Append("  ");
                    i++;
                    continue;
                }
                inBracketedIdentifier = false;
                builder.Append(' ');
                continue;
            }

            builder.Append(inStringLiteral || inBracketedIdentifier ? ' ' : c);
        }

        unterminatedStringLiteral = inStringLiteral;
        unterminatedIdentifier = inBracketedIdentifier;
        return builder.ToString();
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$';

    private static bool ContainsWord(string haystack, string word)
    {
        var index = haystack.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var before = index == 0 || !IsIdentifierChar(haystack[index - 1]);
            var afterIndex = index + word.Length;
            var after = afterIndex >= haystack.Length || !IsIdentifierChar(haystack[afterIndex]);
            if (before && after) return true;
            index = haystack.IndexOf(word, index + 1, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }
}
