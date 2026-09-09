using SqlMigrator.Model.Schema;

namespace SqlMigrator.Api;

/// <summary>One analysis at a time, in memory. A restart loses it, which is correct for a local tool.</summary>
public sealed class Session
{
    public required string Id { get; init; }
    public required string SourceConnectionString { get; init; }
    public required string TargetConnectionString { get; init; }
    public required string SourceServer { get; init; }
    public required string TargetServer { get; init; }
    public required string SourceReference { get; init; }

    public string State { get; set; } = "running";
    public string? Step { get; set; }
    public string? Error { get; set; }

    public DbSchema? SourceSchema { get; set; }
    public DbSchema? TargetSchema { get; set; }
    public MappingDto? Mapping { get; set; }
    public List<IssueDto> Issues { get; } = [];
    public List<string> Failures { get; } = [];
    public List<string> UnmatchedSourceTables { get; } = [];
}

public sealed class SessionStore
{
    private readonly Lock _gate = new();
    private Session? _current;

    public Session Start(Session session)
    {
        lock (_gate) { _current = session; return session; }
    }

    public Session? Get(string id)
    {
        lock (_gate) return _current?.Id == id ? _current : null;
    }
}
