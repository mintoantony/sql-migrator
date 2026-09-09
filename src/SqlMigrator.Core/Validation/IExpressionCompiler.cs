namespace SqlMigrator.Core.Validation;

public sealed record CompileResult(bool Ok, string? Error, SqlTypeInfo? ResultType)
{
    public static CompileResult Success(SqlTypeInfo type) => new(true, null, type);
    public static CompileResult Failure(string error) => new(false, error, null);
}

/// <summary>Gate 2: proves an expression compiles against the real source table.</summary>
public interface IExpressionCompiler
{
    Task<CompileResult> CompileAsync(string sourceTableFullName, string expression, CancellationToken ct = default);
}
