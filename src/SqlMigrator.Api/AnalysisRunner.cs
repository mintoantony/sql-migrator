using SqlMigrator.Ai;
using SqlMigrator.Core.Schema;
using SqlMigrator.Core.Validation;

namespace SqlMigrator.Api;

/// <summary>Steps 1 to 4 of the pipeline: read schemas, propose, validate.</summary>
public static class AnalysisRunner
{
    /// <summary>
    /// Runs detached from the request that started it (see the fire-and-forget <c>Task.Run</c>
    /// in Program.cs), so nothing observes an exception thrown out of this method. Every step is
    /// therefore wrapped in one try/catch: whatever goes wrong — a bad connection, a malformed
    /// expression, an unreachable model provider — is recorded on the session as a "failed" state
    /// with a human-readable message, and the state always reaches a terminal value (never left
    /// stuck on "running" for a poller to wait on forever). Exception messages surfaced here come
    /// only from SqlClient/HTTP/AI layers that never echo a connection string or API key back, so
    /// none of that ever reaches <see cref="Session.Fail"/>.
    /// </summary>
    public static async Task RunAsync(
        Session session, IChatClient chatClient, AiOptions aiOptions, CancellationToken ct)
    {
        try
        {
            session.SetStep("Reading source schema…");
            session.SourceSchema = await SchemaReader.ReadAsync(session.SourceConnectionString, ct);

            session.SetStep("Reading target schema…");
            session.TargetSchema = await SchemaReader.ReadAsync(session.TargetConnectionString, ct);

            var progress = new Progress<string>(session.SetStep);
            var proposer = new MappingProposer(chatClient, aiOptions);
            var proposal = await proposer.ProposeAsync(
                session.SourceSchema, session.TargetSchema, session.SourceReference, progress, ct);

            session.Failures.AddRange(proposal.Failures);

            session.SetStep("Validating expressions…");
            var compiler = new SqlExpressionCompiler(session.SourceConnectionString);
            var validator = new MappingValidator(session.SourceSchema, session.TargetSchema, compiler);
            var issues = await validator.ValidateAsync(proposal.Mapping, ct);

            session.Issues.AddRange(issues.Select(DtoMapper.ToDto));
            session.UnmatchedSourceTables.AddRange(
                issues.Where(i => i.Code == "SRC001" && i.Table is not null).Select(i => i.Table!));

            // Every proposed expression has now been validated against the real database via
            // MappingValidator above; only that validated mapping — never the raw proposal — is
            // published to the session for a human to review.
            session.Complete(DtoMapper.ToDto(proposal.Mapping));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Superseded by a newer analysis. Nothing can poll this session any more, but the
            // state still has to reach a terminal value.
            session.Fail("Cancelled: a newer analysis replaced this one.");
        }
        catch (Exception ex)
        {
            session.Fail(ex.Message);
        }
    }
}
