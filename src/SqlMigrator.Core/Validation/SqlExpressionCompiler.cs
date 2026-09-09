using System.Data;
using Microsoft.Data.SqlClient;

namespace SqlMigrator.Core.Validation;

/// <summary>
/// Asks SQL Server itself whether an expression is real, using SELECT TOP 0 with
/// SchemaOnly behaviour. Reads no rows; returns the type the expression yields.
/// </summary>
public sealed class SqlExpressionCompiler(string sourceConnectionString) : IExpressionCompiler
{
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

    /// <summary>Turns dbo.Order into [dbo].[Order] so reserved words survive.</summary>
    internal static string Quote(string fullName)
    {
        var parts = fullName.Split('.', 2);
        return parts.Length == 2
            ? $"[{parts[0].Trim('[', ']')}].[{parts[1].Trim('[', ']')}]"
            : $"[dbo].[{fullName.Trim('[', ']')}]";
    }
}
