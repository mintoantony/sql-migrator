using SqlMigrator.Model.Schema;

namespace SqlMigrator.Api;

/// <summary>
/// A minimal thread-safe append-only list. <see cref="Add"/>/<see cref="AddRange"/> are
/// serialized against each other, and <see cref="ToList"/> takes an atomic point-in-time copy —
/// so a background writer appending to a <see cref="Session"/>'s list while a status poll reads
/// it can neither tear the read nor throw "collection was modified".
/// </summary>
public sealed class SynchronizedList<T>
{
    private readonly Lock _gate = new();
    private readonly List<T> _items = [];

    public void Add(T item)
    {
        lock (_gate) _items.Add(item);
    }

    public void AddRange(IEnumerable<T> items)
    {
        lock (_gate) _items.AddRange(items);
    }

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    /// <summary>An atomic snapshot, safe to hand to a caller running on another thread.</summary>
    public List<T> ToList()
    {
        lock (_gate) return [.. _items];
    }
}

/// <summary>
/// One analysis at a time, in memory. A restart loses it, which is correct for a local tool.
///
/// A future background analysis mutates a session's <see cref="Issues"/>/<see cref="Failures"/>/
/// <see cref="UnmatchedSourceTables"/> lists while a status endpoint concurrently reads them via
/// <see cref="SessionStore.Get"/>, holding no lock of its own. Those three fields are therefore
/// <see cref="SynchronizedList{T}"/>, not a plain <see cref="List{T}"/>: appends and reads are
/// internally synchronized, so it is safe for <see cref="SessionStore.Get"/> to keep handing out
/// the live session rather than a deep copy. The scalar fields (<see cref="State"/>,
/// <see cref="Step"/>, <see cref="Error"/>, <see cref="Mapping"/>, schemas) are plain reference
/// types reassigned as a whole, never mutated in place, and .NET guarantees reference
/// assignment is atomic — so they carry no torn-read risk and need no extra locking.
/// </summary>
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
    public SynchronizedList<IssueDto> Issues { get; } = new();
    public SynchronizedList<string> Failures { get; } = new();
    public SynchronizedList<string> UnmatchedSourceTables { get; } = new();
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
