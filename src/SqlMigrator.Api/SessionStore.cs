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
/// Starting a new one cancels the run behind the old one — see <see cref="RunCancellation"/>.
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
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _run = new();

    /// <summary>
    /// Fires when this session is superseded. The analysis behind a session runs detached from
    /// the request that started it, so this token is the only thing that can stop it — and it
    /// must be stopped: an abandoned run kept calling the model provider to completion for a
    /// result no poll could ever read, so every press of Analyse stacked a further full set of
    /// model requests on top of the last.
    /// </summary>
    public CancellationToken RunCancellation => _run.Token;

    internal void CancelRun() => _run.Cancel();

    /// <summary>An atomic snapshot of (State, Step, Mapping, Error) — never a torn read.</summary>
    public SessionResult Result
    {
        get { lock (_gate) return _result; }
    }

    /// <summary>
    /// Completes as soon as <see cref="Result"/> differs from <paramref name="seen"/>, or with
    /// the current (unchanged) result once <paramref name="keepAlive"/> has elapsed. This is what
    /// lets the status be pushed to the browser over one open request instead of polled: a
    /// waiter sleeps until a writer publishes, and the keep-alive gives the stream something to
    /// send periodically so an idle connection is not mistaken for a dead one.
    /// </summary>
    public async Task<SessionResult> WaitForChangeAsync(
        SessionResult seen, TimeSpan keepAlive, CancellationToken ct)
    {
        var deadline = Task.Delay(keepAlive, ct);
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_result != seen) return _result;
                changed = _changed.Task;
            }

            if (await Task.WhenAny(changed, deadline) == deadline)
            {
                await deadline; // observes the cancellation, if that is why it completed
                lock (_gate) return _result;
            }
        }
    }

    /// <summary>Updates the progress message shown while the run is still "running".</summary>
    public void SetStep(string? step)
    {
        lock (_gate) Publish(_result with { Step = step });
    }

    /// <summary>Publishes State="ready" and the mapping together, in one step.</summary>
    public void Complete(MappingDto mapping) =>
        Publish(new SessionResult("ready", null, mapping, null));

    /// <summary>Publishes State="failed" and the error message together, in one step.</summary>
    public void Fail(string error) =>
        Publish(new SessionResult("failed", null, null, error));

    /// <summary>Swaps the snapshot and wakes every <see cref="WaitForChangeAsync"/> waiter, under the gate.</summary>
    private void Publish(SessionResult result)
    {
        lock (_gate)
        {
            _result = result;
            var waiters = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.SetResult();
        }
    }
}

public sealed class SessionStore
{
    private readonly Lock _gate = new();
    private Session? _current;

    /// <summary>Makes <paramref name="session"/> current and cancels the run of the one it replaces.</summary>
    public Session Start(Session session)
    {
        Session? superseded;
        lock (_gate) { superseded = _current; _current = session; }

        // Outside the lock: cancellation runs continuations synchronously, and none of them
        // should ever execute while the store's gate is held.
        superseded?.CancelRun();
        return session;
    }

    public Session? Get(string id)
    {
        lock (_gate) return _current?.Id == id ? _current : null;
    }
}
