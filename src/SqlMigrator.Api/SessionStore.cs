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
/// The State/Step/Mapping/Error quartet, published as one immutable value so a reader can
/// never observe it torn — see the note on <see cref="Session.Result"/>.
/// </summary>
public sealed record SessionResult(string State, string? Step, MappingDto? Mapping, string? Error)
{
    public static readonly SessionResult Running = new("running", null, null, null);
}

/// <summary>
/// One analysis at a time, in memory. A restart loses it, which is correct for a local tool.
///
/// A background analysis mutates a session's <see cref="Issues"/>/<see cref="Failures"/>/
/// <see cref="UnmatchedSourceTables"/> lists while a status endpoint concurrently reads them via
/// <see cref="SessionStore.Get"/>, holding no lock of its own. Those three fields are therefore
/// <see cref="SynchronizedList{T}"/>, not a plain <see cref="List{T}"/>: appends and reads are
/// internally synchronized, so it is safe for <see cref="SessionStore.Get"/> to keep handing out
/// the live session rather than a deep copy.
///
/// <see cref="State"/>, <see cref="Step"/>, <see cref="Mapping"/> and <see cref="Error"/> are a
/// different story: atomic per field does NOT mean consistent across fields. If they were plain
/// properties assigned independently, a reader could observe the PAIR torn — State already
/// "ready" while Mapping is still null — the instant a background writer exists. So they are not
/// separate properties. They live together in one <see cref="SessionResult"/>, replaced as a
/// single reference under <see cref="_gate"/> by <see cref="Complete"/>/<see cref="Fail"/>/
/// <see cref="SetStep"/>, and read back as a single atomic snapshot via <see cref="Result"/>.
/// A reader always sees a value that some writer actually published, never a mix of two.
///
/// <see cref="SourceSchema"/> and <see cref="TargetSchema"/> remain plain reference-typed
/// properties: they are written only internally by the single background run for this session
/// (never read back through the status endpoint), so plain atomic reference assignment is
/// sufficient for them.
/// </summary>
public sealed class Session
{
    public required string Id { get; init; }
    public required string SourceConnectionString { get; init; }
    public required string TargetConnectionString { get; init; }
    public required string SourceServer { get; init; }
    public required string TargetServer { get; init; }
    public required string SourceReference { get; init; }

    public DbSchema? SourceSchema { get; set; }
    public DbSchema? TargetSchema { get; set; }

    public SynchronizedList<IssueDto> Issues { get; } = new();
    public SynchronizedList<string> Failures { get; } = new();
    public SynchronizedList<string> UnmatchedSourceTables { get; } = new();

    private readonly Lock _gate = new();
    private SessionResult _result = SessionResult.Running;

    /// <summary>An atomic snapshot of (State, Step, Mapping, Error) — never a torn read.</summary>
    public SessionResult Result
    {
        get { lock (_gate) return _result; }
    }

    /// <summary>Updates the progress message shown while the run is still "running".</summary>
    public void SetStep(string? step)
    {
        lock (_gate) _result = _result with { Step = step };
    }

    /// <summary>Publishes State="ready" and the mapping together, in one step.</summary>
    public void Complete(MappingDto mapping)
    {
        lock (_gate) _result = new SessionResult("ready", null, mapping, null);
    }

    /// <summary>Publishes State="failed" and the error message together, in one step.</summary>
    public void Fail(string error)
    {
        lock (_gate) _result = new SessionResult("failed", null, null, error);
    }
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
