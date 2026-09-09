using System.Data;
using Microsoft.Data.SqlClient;
using SqlMigrator.Core.Sql;

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

        // Opening the connection sits OUTSIDE the try on purpose. A login failure or an
        // unreachable server raises SqlException just as a bad expression does, so catching
        // around the open would report "your expression did not compile" when the real
        // problem is that the database cannot be reached — sending a human to rewrite
        // perfectly good SQL. Only the compile itself is allowed to become a Failure.
        await using var conn = new SqlConnection(sourceConnectionString);
        await conn.OpenAsync(ct);

        try
        {
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
    /// Turns dbo.Order into [dbo].[Order] so reserved words survive. Delegates to
    /// <see cref="SqlIdentifier.Quote"/>, the one place in the solution that owns
    /// identifier-quoting logic.
    /// </summary>
    internal static string Quote(string fullName) => SqlIdentifier.Quote(fullName);
}
