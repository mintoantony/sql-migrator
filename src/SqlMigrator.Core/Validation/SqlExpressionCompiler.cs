using System.Data;
using Microsoft.Data.SqlClient;

namespace SqlMigrator.Core.Validation;

/// <summary>
/// Asks SQL Server itself whether an expression is real, using SELECT TOP 0 with
/// SchemaOnly behaviour. Reads no rows; returns the type the expression yields.
/// </summary>
public sealed class SqlExpressionCompiler(string sourceConnectionString) : IExpressionCompiler
{
    /// <summary>
    /// Compiles one expression against the source table.
    /// </summary>
    /// <remarks>
    /// The failure contract is deliberate. A <see cref="CompileResult"/> failure means
    /// "SQL Server rejected this expression" and nothing else, so a caller can show it
    /// to a human as a problem with the mapping. Everything else propagates:
    /// <see cref="OperationCanceledException"/> so a caller can tell its own cancellation
    /// apart from a bad expression, and a connection or configuration failure so an
    /// unreachable server is never reported as an invalid expression.
    /// <see cref="ArgumentException"/> is thrown for a malformed table name before any
    /// SQL is built — this type is reachable from an API endpoint and does not assume
    /// the caller validated first.
    /// </remarks>
    public async Task<CompileResult> CompileAsync(
        string sourceTableFullName, string expression, CancellationToken ct = default)
    {
        var sql = $"SELECT TOP 0 ({expression}) AS [Value] FROM {Quote(sourceTableFullName)}";

        try
        {
            await using var conn = new SqlConnection(sourceConnectionString);
            await conn.OpenAsync(ct);

            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 15 };
            await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SchemaOnly, ct);

            var schema = await reader.GetColumnSchemaAsync(ct);
            var column = schema[0];

            return CompileResult.Success(new SqlTypeInfo(
                DataType: column.DataTypeName ?? "unknown",
                MaxLength: column.ColumnSize ?? 0,
                Precision: (byte)(column.NumericPrecision ?? 0),
                Scale: (byte)(column.NumericScale ?? 0),
                IsNullable: column.AllowDBNull ?? true));
        }
        catch (SqlException ex)
        {
            return CompileResult.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Turns dbo.Order into [dbo].[Order] so reserved words survive.
    /// The table name is structural, so it cannot be passed as a parameter — it is
    /// interpolated into the command text. That makes this escaping the only thing
    /// between a caller-supplied name and arbitrary SQL, so it follows the T-SQL rule
    /// exactly: a ] inside a bracket-quoted identifier is escaped by doubling it.
    /// </summary>
    internal static string Quote(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            throw new ArgumentException("Table name must not be empty.", nameof(fullName));

        var parts = fullName.Split('.', 2);
        var schema = parts.Length == 2 ? parts[0] : "dbo";
        var table = parts.Length == 2 ? parts[1] : fullName;

        return $"{QuoteIdentifier(schema, nameof(fullName))}.{QuoteIdentifier(table, nameof(fullName))}";
    }

    /// <summary>
    /// Wraps one identifier in brackets, doubling any ] it contains. Trimming the
    /// brackets off an already-quoted name is not enough on its own: Customer]; DROP
    /// would survive a trim and close the identifier early.
    /// </summary>
    private static string QuoteIdentifier(string part, string paramName)
    {
        var bare = part.Trim();

        if (bare.Length >= 2 && bare[0] == '[' && bare[^1] == ']')
            bare = bare[1..^1];

        if (string.IsNullOrWhiteSpace(bare))
            throw new ArgumentException($"Table name part '{part}' is empty.", paramName);

        return $"[{bare.Replace("]", "]]")}]";
    }
}
