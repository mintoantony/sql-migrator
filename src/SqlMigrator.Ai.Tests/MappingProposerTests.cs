using System.Net.Http;
using SqlMigrator.Model.Mapping;
using SqlMigrator.Model.Schema;

namespace SqlMigrator.Ai.Tests;

public class MappingProposerTests
{
    /// <summary>
    /// Returns a scripted response per call, so a two-pass run is fully deterministic. An entry
    /// may be an <see cref="Exception"/> instead of a JSON string, in which case it is thrown
    /// from that call — used to script provider/network failures.
    /// </summary>
    private sealed class ScriptedClient(params object[] responses) : IChatClient
    {
        private int _index;
        public int CallCount => _index;
        public List<string> UserPrompts { get; } = [];

        public Task<string> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            UserPrompts.Add(userPrompt);
            var response = responses[Math.Min(_index, responses.Length - 1)];
            _index++;
            if (response is Exception ex) throw ex;
            return Task.FromResult((string)response);
        }
    }

    /// <summary>Cancels its own caller-supplied token on the first call, simulating a human hitting Ctrl+C.</summary>
    private sealed class CancellingClient(CancellationTokenSource cts) : IChatClient
    {
        public Task<string> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult("unreachable");
        }
    }

    private static DbSchema Source() => new("SrcDb",
        [new TableInfo("dbo", "Customer",
            [
                new ColumnInfo("CustomerId", "int", 4, 10, 0, false, true, false, 1),
                new ColumnInfo("FirstName", "nvarchar", 100, 0, 0, false, false, false, 2),
                new ColumnInfo("LastName", "nvarchar", 100, 0, 0, false, false, false, 3)
            ], ["CustomerId"])],
        []);

    private static DbSchema Target() => new("TgtDb",
        [new TableInfo("dbo", "Client",
            [
                new ColumnInfo("ClientId", "int", 4, 10, 0, false, true, false, 1),
                new ColumnInfo("FullName", "nvarchar", 200, 0, 0, false, false, false, 2),
                new ColumnInfo("Notes", "nvarchar", 500, 0, 0, true, false, false, 3)
            ], ["ClientId"])],
        []);

    private const string MatchResponse = """
        {"matches":[{"sourceTable":"dbo.Customer","targetTable":"dbo.Client","confidence":0.96,"reason":"Both hold customers."}]}
        """;

    private const string ColumnResponse = """
        {"columns":[
          {"targetColumn":"ClientId","rule":"copy","expression":"CustomerId","confidence":0.99,"reason":"Key."},
          {"targetColumn":"FullName","rule":"concat","expression":"CONCAT(FirstName, ' ', LastName)","confidence":0.9,"reason":"Two parts."}],
         "unmapped":[{"targetColumn":"Notes","reason":"No source column."}]}
        """;

    [Fact]
    public async Task Builds_a_mapping_from_two_passes()
    {
        var client = new ScriptedClient(MatchResponse, ColumnResponse);
        var proposer = new MappingProposer(client, new AiOptions { Model = "test/model" });

        var result = await proposer.ProposeAsync(Source(), Target(), "[SrcDb]");

        Assert.Equal(2, client.CallCount);           // one match pass, one column pass
        Assert.Empty(result.Failures);

        var table = Assert.Single(result.Mapping.Tables);
        Assert.Equal("dbo.Customer", table.SourceTable);
        Assert.Equal("dbo.Client", table.TargetTable);
        Assert.Equal(Origin.Ai, table.Origin);
        Assert.Equal(0.96, table.Confidence);

        Assert.Equal(2, table.Columns.Count);
        Assert.Equal(RuleKind.Concat, table.Columns[1].Rule);
        Assert.Equal("CONCAT(FirstName, ' ', LastName)", table.Columns[1].Expression);
        Assert.Equal(Origin.Ai, table.Columns[1].Origin);

        Assert.Equal("Notes", Assert.Single(table.Unmapped).TargetColumn);
        Assert.Equal("test/model", result.Mapping.Model);
        Assert.Equal("[SrcDb]", result.Mapping.SourceReference);
    }

    [Fact]
    public async Task Retries_once_then_records_a_failure_for_that_table_pair()
    {
        var client = new ScriptedClient(MatchResponse, "garbage", "still garbage");
        var proposer = new MappingProposer(client, new AiOptions { Model = "m" });

        var result = await proposer.ProposeAsync(Source(), Target(), "[SrcDb]");

        Assert.Equal(3, client.CallCount);            // match + two column attempts
        Assert.Empty(result.Mapping.Tables);
        Assert.Contains(result.Failures, f => f.Contains("dbo.Client"));
    }

    [Fact]
    public async Task Ignores_a_match_naming_a_table_that_does_not_exist()
    {
        const string bogus = """
            {"matches":[{"sourceTable":"dbo.Nope","targetTable":"dbo.Client","confidence":0.9,"reason":"Hallucinated."}]}
            """;
        var client = new ScriptedClient(bogus);
        var proposer = new MappingProposer(client, new AiOptions { Model = "m" });

        var result = await proposer.ProposeAsync(Source(), Target(), "[SrcDb]");

        Assert.Empty(result.Mapping.Tables);
        Assert.Contains(result.Failures, f => f.Contains("dbo.Nope"));
        Assert.Equal(1, client.CallCount);            // no column pass for a match that cannot exist
    }

    [Fact]
    public async Task Drops_a_proposed_column_that_is_not_on_the_target_table()
    {
        const string extraColumn = """
            {"columns":[
              {"targetColumn":"ClientId","rule":"copy","expression":"CustomerId","confidence":0.9,"reason":"Key."},
              {"targetColumn":"Invented","rule":"copy","expression":"FirstName","confidence":0.9,"reason":"Hallucinated."}]}
            """;
        var client = new ScriptedClient(MatchResponse, extraColumn);
        var proposer = new MappingProposer(client, new AiOptions { Model = "m" });

        var result = await proposer.ProposeAsync(Source(), Target(), "[SrcDb]");

        var table = Assert.Single(result.Mapping.Tables);
        Assert.DoesNotContain(table.Columns, c => c.TargetColumn == "Invented");
        Assert.Contains(result.Failures, f => f.Contains("Invented"));
    }

    [Fact]
    public async Task An_unknown_rule_falls_back_to_copy_and_is_recorded()
    {
        const string weirdRule = """
            {"columns":[{"targetColumn":"ClientId","rule":"teleport","expression":"CustomerId","confidence":0.9,"reason":"?"}]}
            """;
        var client = new ScriptedClient(MatchResponse, weirdRule);
        var proposer = new MappingProposer(client, new AiOptions { Model = "m" });

        var result = await proposer.ProposeAsync(Source(), Target(), "[SrcDb]");

        Assert.Equal(RuleKind.Copy, Assert.Single(result.Mapping.Tables).Columns[0].Rule);
        Assert.Contains(result.Failures, f => f.Contains("teleport"));
    }

    [Fact]
    public async Task Reports_progress_per_table_pair()
    {
        var reported = new List<string>();
        var client = new ScriptedClient(MatchResponse, ColumnResponse);
        var proposer = new MappingProposer(client, new AiOptions { Model = "m" });

        await proposer.ProposeAsync(Source(), Target(), "[SrcDb]",
            new Progress<string>(m => reported.Add(m)));

        // Progress<T> posts asynchronously; give the callbacks a moment to land.
        await Task.Delay(50);
        Assert.Contains(reported, m => m.Contains("dbo.Customer"));
    }

    [Fact]
    public async Task Duplicate_target_column_keeps_the_first_proposal_and_discards_the_rest()
    {
        // Same target column named twice, second time in different case: SQL Server identifiers
        // are case-insensitive, so "ClientId" and "clientid" are the same real column, and the
        // generated INSERT can only list it once.
        const string duplicateColumn = """
            {"columns":[
              {"targetColumn":"ClientId","rule":"copy","expression":"CustomerId","confidence":0.9,"reason":"First."},
              {"targetColumn":"clientid","rule":"copy","expression":"CustomerId","confidence":0.5,"reason":"Second, different case."}]}
            """;
        var client = new ScriptedClient(MatchResponse, duplicateColumn);
        var proposer = new MappingProposer(client, new AiOptions { Model = "m" });

        var result = await proposer.ProposeAsync(Source(), Target(), "[SrcDb]");

        var table = Assert.Single(result.Mapping.Tables);
        var column = Assert.Single(table.Columns);
        Assert.Equal("ClientId", column.TargetColumn);
        Assert.Equal(0.9, column.Confidence);          // the first proposal wins, not the second
        Assert.Equal("First.", column.Reason);
        Assert.Contains(result.Failures, f =>
            f.Contains("clientid", StringComparison.OrdinalIgnoreCase) && f.Contains("more than once"));
    }

    [Fact]
    public async Task A_network_failure_for_one_table_costs_only_that_table()
    {
        var source = new DbSchema("SrcDb",
            [
                new TableInfo("dbo", "Customer",
                    [new ColumnInfo("CustomerId", "int", 4, 10, 0, false, true, false, 1)], ["CustomerId"]),
                new TableInfo("dbo", "Order",
                    [new ColumnInfo("OrderId", "int", 4, 10, 0, false, true, false, 1)], ["OrderId"])
            ], []);

        var target = new DbSchema("TgtDb",
            [
                new TableInfo("dbo", "Client",
                    [new ColumnInfo("ClientId", "int", 4, 10, 0, false, true, false, 1)], ["ClientId"]),
                new TableInfo("dbo", "Purchase",
                    [new ColumnInfo("PurchaseId", "int", 4, 10, 0, false, true, false, 1)], ["PurchaseId"])
            ], []);

        const string matchTwoTables = """
            {"matches":[
              {"sourceTable":"dbo.Customer","targetTable":"dbo.Client","confidence":0.9,"reason":"Customers."},
              {"sourceTable":"dbo.Order","targetTable":"dbo.Purchase","confidence":0.9,"reason":"Orders."}]}
            """;

        const string purchaseColumns = """
            {"columns":[{"targetColumn":"PurchaseId","rule":"copy","expression":"OrderId","confidence":0.9,"reason":"Key."}]}
            """;

        // Customer -> Client's column-mapping call fails with a network error on both attempts;
        // Order -> Purchase's call succeeds. The bad table must not abort the good one.
        var client = new ScriptedClient(
            matchTwoTables,
            new HttpRequestException("connection reset"),
            new HttpRequestException("connection reset"),
            purchaseColumns);
        var proposer = new MappingProposer(client, new AiOptions { Model = "m" });

        var result = await proposer.ProposeAsync(source, target, "[SrcDb]");

        var table = Assert.Single(result.Mapping.Tables);
        Assert.Equal("dbo.Order", table.SourceTable);
        Assert.Equal("dbo.Purchase", table.TargetTable);
        Assert.Contains(result.Failures, f => f.Contains("dbo.Client") && f.Contains("connection reset"));
    }

    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_being_recorded_as_a_failure()
    {
        using var cts = new CancellationTokenSource();
        var client = new CancellingClient(cts);
        var proposer = new MappingProposer(client, new AiOptions { Model = "m" });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            proposer.ProposeAsync(Source(), Target(), "[SrcDb]", ct: cts.Token));
    }
}
