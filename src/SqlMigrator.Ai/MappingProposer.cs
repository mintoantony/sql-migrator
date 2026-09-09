using SqlMigrator.Model.Mapping;
using SqlMigrator.Model.Schema;

namespace SqlMigrator.Ai;

/// <summary>A proposal plus everything that went wrong while producing it.</summary>
public sealed record ProposalResult(MigrationMapping Mapping, IReadOnlyList<string> Failures);

/// <summary>
/// Two passes: match tables across both schemas, then map columns for each matched pair.
/// Anything the model names that does not exist is dropped and recorded, never trusted.
/// </summary>
public sealed class MappingProposer(IChatClient client, AiOptions options)
{
    public async Task<ProposalResult> ProposeAsync(
        DbSchema source,
        DbSchema target,
        string sourceReference,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var failures = new List<string>();
        var tables = new List<TableMapping>();

        progress?.Report("Matching tables…");
        var matches = await MatchTables(source, target, failures, ct);

        foreach (var match in matches)
        {
            var sourceTable = source.Find(match.SourceTable);
            var targetTable = target.Find(match.TargetTable);

            if (sourceTable is null || targetTable is null)
            {
                failures.Add($"Model proposed a match involving {match.SourceTable} -> {match.TargetTable}, " +
                             "but at least one of those tables does not exist. Ignored.");
                continue;
            }

            progress?.Report($"Mapping columns for {sourceTable.FullName} -> {targetTable.FullName}…");

            var mapped = await MapColumns(sourceTable, targetTable, failures, ct);
            if (mapped is null) continue;

            tables.Add(mapped with
            {
                SourceTable = sourceTable.FullName,
                TargetTable = targetTable.FullName,
                Origin = Origin.Ai,
                Confidence = match.Confidence,
                Reason = match.Reason
            });
        }

        var mapping = new MigrationMapping(
            Name: $"{source.DatabaseName} to {target.DatabaseName}",
            GeneratedUtc: DateTimeOffset.UtcNow,
            Model: options.Model,
            SourceDatabase: source.DatabaseName,
            SourceReference: sourceReference,
            TargetDatabase: target.DatabaseName,
            Tables: tables);

        return new ProposalResult(mapping, failures);
    }

    private async Task<IReadOnlyList<TableMatch>> MatchTables(
        DbSchema source, DbSchema target, List<string> failures, CancellationToken ct)
    {
        var user = Prompts.TableMatchUser(source, target);

        var response = await CallWithOneRetry(Prompts.TableMatchSystem, user, failures, "table matching", ct);
        if (response is null) return [];

        try
        {
            return AiJson.Deserialize<TableMatchResponse>(response).Matches;
        }
        catch (AiException ex)
        {
            failures.Add($"Table matching returned unusable JSON: {ex.Message}");
            return [];
        }
    }

    private async Task<TableMapping?> MapColumns(
        TableInfo sourceTable, TableInfo targetTable, List<string> failures, CancellationToken ct)
    {
        var user = Prompts.ColumnMapUser(sourceTable, targetTable);
        var label = $"column mapping for {targetTable.FullName}";

        var response = await CallWithOneRetry(Prompts.ColumnMapSystem, user, failures, label, ct);
        if (response is null) return null;

        ColumnMapResponse parsed;
        try
        {
            parsed = AiJson.Deserialize<ColumnMapResponse>(response);
        }
        catch (AiException ex)
        {
            failures.Add($"Gave up on {label}: {ex.Message}");
            return null;
        }

        var columns = new List<ColumnMapping>();
        foreach (var proposal in parsed.Columns)
        {
            if (targetTable.Column(proposal.TargetColumn) is null)
            {
                failures.Add($"Model proposed target column {targetTable.FullName}.{proposal.TargetColumn}, " +
                             "which does not exist. Dropped.");
                continue;
            }

            if (!Enum.TryParse<RuleKind>(proposal.Rule, ignoreCase: true, out var rule))
            {
                failures.Add($"Model used unknown rule '{proposal.Rule}' for " +
                             $"{targetTable.FullName}.{proposal.TargetColumn}. Treated as copy.");
                rule = RuleKind.Copy;
            }

            columns.Add(new ColumnMapping(
                proposal.TargetColumn, rule, proposal.Expression, Origin.Ai,
                proposal.Confidence, proposal.Reason));
        }

        var unmapped = parsed.Unmapped
            .Where(u => targetTable.Column(u.TargetColumn) is not null)
            .Select(u => new UnmappedColumn(u.TargetColumn, u.Reason))
            .ToList();

        return new TableMapping(
            sourceTable.FullName, targetTable.FullName, columns, unmapped, Origin.Ai, null, null);
    }

    /// <summary>One retry, then give up. A model that fails twice is reported, not guessed at.</summary>
    private async Task<string?> CallWithOneRetry(
        string system, string user, List<string> failures, string label, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            string response;
            try
            {
                response = await client.CompleteJsonAsync(system, user, ct);
            }
            catch (AiException ex)
            {
                if (attempt == 2) { failures.Add($"Gave up on {label}: {ex.Message}"); return null; }
                continue;
            }

            if (LooksLikeJson(response)) return response;
            if (attempt == 2) failures.Add($"Gave up on {label}: model did not return JSON.");
        }
        return null;
    }

    private static bool LooksLikeJson(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith("```", StringComparison.Ordinal);
    }
}
