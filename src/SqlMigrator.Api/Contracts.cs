namespace SqlMigrator.Api;

public sealed record ConnectionRequest(string Server, string Database);
public sealed record ConnectionTestResponse(bool Ok, string? Version, int TableCount, string? Error);

public sealed record AnalyseRequest(ConnectionRequest Source, ConnectionRequest Target, string SourceReference);
public sealed record AnalyseStarted(string SessionId);

public sealed record IssueDto(string Code, string Severity, string Message, string? Table, string? Column);

public sealed record ColumnDto(
    string TargetColumn, string Rule, string Expression, string Origin, double? Confidence, string? Reason);

public sealed record UnmappedDto(string TargetColumn, string Reason);

public sealed record TableDto(
    string SourceTable, string TargetTable, string Origin, double? Confidence, string? Reason,
    List<ColumnDto> Columns, List<UnmappedDto> Unmapped);

public sealed record MappingDto(
    string Name, string? Model, string SourceDatabase, string SourceReference, string TargetDatabase,
    List<TableDto> Tables);

/// <summary>State is one of: running, ready, failed.</summary>
public sealed record SessionStatus(
    string State,
    string? Step,
    MappingDto? Mapping,
    List<IssueDto> Issues,
    List<string> Failures,
    List<string> UnmatchedSourceTables,
    string? Error,
    // Configured server-side (Ai:ConfidenceThreshold) and served here so the review grid's
    // pre-accept logic uses the one value an operator can actually change, instead of a
    // hardcoded duplicate on the browser side.
    double ConfidenceThreshold);

public sealed record ValidateExpressionRequest(
    string SessionId, string SourceTable, string TargetTable, string TargetColumn, string Expression);

public sealed record ValidateExpressionResponse(bool Ok, string? ResultType, List<IssueDto> Issues);

public sealed record SaveMappingRequest(string SessionId, MappingDto Mapping);
public sealed record SaveMappingResponse(string Path, string Sha256);

public sealed record GenerateScriptRequest(string SessionId, MappingDto Mapping);
public sealed record GenerateScriptResponse(string Sql, List<IssueDto> Issues);
