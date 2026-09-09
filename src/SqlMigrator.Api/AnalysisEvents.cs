using System.Runtime.CompilerServices;
using SqlMigrator.Ai;

namespace SqlMigrator.Api;

/// <summary>
/// The status of one analysis as a server-sent-events stream: every published change, then the
/// terminal state, then the stream ends. The browser opens exactly one request per analysis.
/// It used to poll GET /api/analyse/{id} every half second instead, which put well over a
/// hundred requests in the network tab for a one-minute run — easy to mistake for a hundred
/// calls to the model, which makes two or three.
/// </summary>
public static class AnalysisEvents
{
    /// <summary>Re-sends the current status after this long without a change, so the connection never looks dead.</summary>
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);

    public static async IAsyncEnumerable<SessionStatus> Stream(
        Session session, AiOptions aiOptions, [EnumeratorCancellation] CancellationToken ct)
    {
        var result = session.Result;
        while (true)
        {
            yield return Snapshot(session, result, aiOptions);
            if (result.State != "running") yield break;

            result = await session.WaitForChangeAsync(result, KeepAlive, ct);
        }
    }

    /// <summary>The same shape GET /api/analyse/{id} returns, from one atomic result snapshot.</summary>
    public static SessionStatus Snapshot(Session session, SessionResult result, AiOptions aiOptions) => new(
        result.State, result.Step, result.Mapping,
        session.Issues.ToList(), session.Failures.ToList(), session.UnmatchedSourceTables.ToList(),
        result.Error, aiOptions.ConfidenceThreshold);
}
