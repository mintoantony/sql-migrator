using SqlMigrator.Api;

namespace SqlMigrator.Api.Tests;

/// <summary>
/// Proves the fix for SessionStore.Get() handing back a session whose mutable lists could tear
/// or throw "collection was modified" under a concurrent writer: a background analysis mutating
/// Issues/Failures/UnmatchedSourceTables while a status poll reads them.
/// </summary>
public class SessionStoreTests
{
    private static Session NewSession(string id) => new()
    {
        Id = id,
        SourceConnectionString = "n/a",
        TargetConnectionString = "n/a",
        SourceServer = "localhost",
        TargetServer = "localhost",
        SourceReference = "n/a",
    };

    [Fact]
    public void Get_returns_the_session_started_under_the_same_id()
    {
        var store = new SessionStore();
        var started = store.Start(NewSession("s1"));

        var fetched = store.Get("s1");

        Assert.Same(started, fetched);
    }

    [Fact]
    public void Get_returns_null_for_an_unknown_or_superseded_id()
    {
        var store = new SessionStore();
        store.Start(NewSession("s1"));
        store.Start(NewSession("s2"));

        Assert.Null(store.Get("s1"));
    }

    [Fact]
    public async Task Concurrent_writes_and_reads_on_a_sessions_lists_never_throw_or_tear()
    {
        var store = new SessionStore();
        var session = store.Start(NewSession("s1"));

        const int writes = 2000;
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < writes; i++)
            {
                session.Issues.Add(new IssueDto("CODE", "Info", $"message {i}", null, null));
                session.Failures.AddRange(["failure"]);
                session.UnmatchedSourceTables.Add("dbo.Table");
            }
        });

        var reader = Task.Run(() =>
        {
            for (var i = 0; i < writes; i++)
            {
                var current = store.Get("s1")!;
                // A snapshot copy must reflect a consistent count at the instant it was taken —
                // reading it never throws even while the writer is still appending.
                var snapshot = current.Issues.ToList();
                _ = snapshot.Count;
            }
        });

        await Task.WhenAll(writer, reader);

        Assert.Equal(writes, session.Issues.Count);
        Assert.Equal(writes, session.Failures.Count);
        Assert.Equal(writes, session.UnmatchedSourceTables.Count);
    }

    /// <summary>
    /// The store holds one session, but the run behind a superseded session was left going: it
    /// kept calling the model provider to completion for a result no poll could ever read. Each
    /// press of Analyse therefore added another full set of model requests on top of the last.
    /// Starting a session must cancel the run of the one it replaces.
    /// </summary>
    [Fact]
    public void Start_cancels_the_run_of_the_session_it_supersedes()
    {
        var store = new SessionStore();
        var first = store.Start(NewSession("s1"));
        Assert.False(first.RunCancellation.IsCancellationRequested);

        var second = store.Start(NewSession("s2"));

        Assert.True(first.RunCancellation.IsCancellationRequested);
        Assert.False(second.RunCancellation.IsCancellationRequested);
    }
}
