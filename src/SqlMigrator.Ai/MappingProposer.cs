using System.Net.Http;
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

        var matchedPairs = new List<(TableMatch Match, TableInfo Source, TableInfo Target)>();
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

            matchedPairs.Add((match, sourceTable, targetTable));
        }

        // ColumnBatchSize governs how many table pairs share one column-mapping request. A
        // batch of exactly one pair goes through the original single-pair call/prompt/shape
        // unchanged — that is what makes ColumnBatchSize = 1 reproduce today's exact
        // one-call-per-table behaviour, and it is why ColumnMapResponse is still here.
        var batchSize = options.ColumnBatchSize <= 0 ? 1 : options.ColumnBatchSize;

        foreach (var chunk in matchedPairs.Chunk(batchSize))
        {
            if (chunk.Length == 1)
            {
                var (match, sourceTable, targetTable) = chunk[0];
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
                continue;
            }

            progress?.Report(
                $"Mapping columns for {chunk.Length} tables: " +
                $"{string.Join(", ", chunk.Select(p => $"{p.Source.FullName} -> {p.Target.FullName}"))}…");

            var mappedByTarget = await MapColumnsBatch(chunk, failures, ct);

            foreach (var pair in chunk)
            {
                if (!mappedByTarget.TryGetValue(pair.Target.FullName, out var mappedTable)) continue;

                tables.Add(mappedTable with
                {
                    Origin = Origin.Ai,
                    Confidence = pair.Match.Confidence,
                    Reason = pair.Match.Reason
                });
            }
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

        return BuildTableMapping(sourceTable, targetTable, parsed.Columns, parsed.Unmapped, failures);
    }

    /// <summary>
    /// Column mapping for several table pairs in one request. Used whenever a chunk holds more
    /// than one pair; a lone pair goes through <see cref="MapColumns"/> instead, unchanged.
    /// </summary>
    private async Task<Dictionary<string, TableMapping>> MapColumnsBatch(
        (TableMatch Match, TableInfo Source, TableInfo Target)[] batch, List<string> failures, CancellationToken ct)
    {
        var result = new Dictionary<string, TableMapping>(StringComparer.OrdinalIgnoreCase);

        var user = Prompts.ColumnMapBatchUser(batch.Select(p => (p.Source, p.Target)).ToList());
        var label = $"column mapping for {string.Join(", ", batch.Select(p => p.Target.FullName))}";

        var response = await CallWithOneRetry(Prompts.ColumnMapBatchSystem, user, failures, label, ct);
        if (response is null) return result;

        BatchColumnMapResponse parsed;
        try
        {
            parsed = AiJson.Deserialize<BatchColumnMapResponse>(response);
        }
        catch (AiException ex)
        {
            failures.Add($"Gave up on {label}: {ex.Message}");
            return result;
        }

        var byTarget = new Dictionary<string, TableColumnMap>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in parsed.Tables)
        {
            var inBatch = batch.Any(p => string.Equals(p.Target.FullName, entry.TargetTable, StringComparison.OrdinalIgnoreCase));
            if (!inBatch)
            {
                failures.Add($"Model's column-mapping response named {entry.TargetTable}, which is not part of " +
                             "this batch. Ignored.");
                continue;
            }

            if (!byTarget.TryAdd(entry.TargetTable, entry))
            {
                failures.Add($"Model's column-mapping response named {entry.TargetTable} more than once. " +
                             "Kept the first entry and discarded the rest.");
            }
        }

        foreach (var (_, sourceTable, targetTable) in batch)
        {
            if (!byTarget.TryGetValue(targetTable.FullName, out var entry))
            {
                failures.Add($"Model omitted {targetTable.FullName} from the column-mapping batch response. " +
                             "Left unmapped.");
                continue;
            }

            result[targetTable.FullName] = BuildTableMapping(sourceTable, targetTable, entry.Columns, entry.Unmapped, failures);
        }

        return result;
    }

    /// <summary>Shared column/unmapped validation for both the single-pair and batched shapes.</summary>
    private static TableMapping BuildTableMapping(
        TableInfo sourceTable, TableInfo targetTable,
        IReadOnlyList<ColumnProposal> proposedColumns, IReadOnlyList<UnmappedProposal> proposedUnmapped,
        List<string> failures)
    {
        var columns = new List<ColumnMapping>();
        var seenTargetColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var proposal in proposedColumns)
        {
            if (targetTable.Column(proposal.TargetColumn) is null)
            {
                failures.Add($"Model proposed target column {targetTable.FullName}.{proposal.TargetColumn}, " +
                             "which does not exist. Dropped.");
                continue;
            }

            if (!seenTargetColumns.Add(proposal.TargetColumn))
            {
                failures.Add($"Model proposed target column {targetTable.FullName}.{proposal.TargetColumn} " +
                             "more than once. Kept the first proposal and discarded this one.");
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

        var unmapped = proposedUnmapped
            .Where(u => targetTable.Column(u.TargetColumn) is not null)
            .Select(u => new UnmappedColumn(u.TargetColumn, u.Reason))
            .ToList();

        return new TableMapping(
            sourceTable.FullName, targetTable.FullName, columns, unmapped, Origin.Ai, null, null);
    }

    /// <summary>
    /// One retry, then give up. A model that fails twice is reported, not guessed at.
    ///
    /// A failure for this table pair alone — bad JSON, a provider error, a network blip, or a
    /// provider-side timeout — is recorded in <paramref name="failures"/> and the caller moves on
    /// to the next table. A genuine caller cancellation (the caller's own <paramref name="ct"/>
    /// firing) is different: it must abort the whole run, so it is left to propagate rather than
    /// being collected as a per-table failure. <see cref="OpenAiChatClient"/> and
    /// <see cref="OllamaChatClient"/> implement their per-request timeout as a token linked to
    /// <paramref name="ct"/>, so a timeout also surfaces
    /// as <see cref="OperationCanceledException"/> — the two are told apart by checking whether
    /// the caller's own token is the one that actually fired.
    /// </summary>
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
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The provider's own request timeout fired, not the caller's cancellation.
                if (attempt == 2) { failures.Add($"Gave up on {label}: the model provider timed out."); return null; }
                continue;
            }
            catch (Exception ex) when (ex is AiException or HttpRequestException)
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
