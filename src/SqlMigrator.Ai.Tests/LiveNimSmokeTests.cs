using SqlMigrator.Model.Schema;

namespace SqlMigrator.Ai.Tests;

/// <summary>
/// The only test that talks to NVIDIA. Skipped unless SQLMIGRATOR_AI_LIVE=1 and
/// NVIDIA_API_KEY are both set, so CI never needs a key and never spends money.
/// Run it once when choosing the model — this is the acceptance criterion from spec §6.
/// </summary>
public class LiveNimSmokeTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("SQLMIGRATOR_AI_LIVE") == "1"
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NVIDIA_API_KEY"));

    [SkippableFact]
    public async Task Proposes_a_mapping_for_the_demo_schemas_without_retry()
    {
        Skip.IfNot(Enabled, "Set SQLMIGRATOR_AI_LIVE=1 and NVIDIA_API_KEY to run the live smoke test.");

        var options = new AiOptions
        {
            ApiKey = Environment.GetEnvironmentVariable("NVIDIA_API_KEY")!,
            Model = Environment.GetEnvironmentVariable("SQLMIGRATOR_AI_MODEL")
                    ?? throw new InvalidOperationException("Set SQLMIGRATOR_AI_MODEL to the model under evaluation.")
        };

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds) };
        var proposer = new MappingProposer(new NimChatClient(http, options), options);

        var result = await proposer.ProposeAsync(DemoSchemas.Source(), DemoSchemas.Target(), "[Src]");

        Assert.Empty(result.Failures);
        var table = Assert.Single(result.Mapping.Tables);
        Assert.Equal("dbo.Client", table.TargetTable);
        Assert.Contains(table.Columns, c => c.TargetColumn == "FullName");
    }
}

/// <summary>Small schemas shaped like the real fixtures, without needing a database.</summary>
public static class DemoSchemas
{
    public static DbSchema Source() => new("SrcDb",
        [new TableInfo("dbo", "Customer",
            [
                new ColumnInfo("CustomerId", "int", 4, 10, 0, false, true, false, 1),
                new ColumnInfo("FirstName", "nvarchar", 100, 0, 0, false, false, false, 2),
                new ColumnInfo("LastName", "nvarchar", 100, 0, 0, false, false, false, 3),
                new ColumnInfo("Email", "nvarchar", 200, 0, 0, true, false, false, 4)
            ], ["CustomerId"])],
        []);

    public static DbSchema Target() => new("TgtDb",
        [new TableInfo("dbo", "Client",
            [
                new ColumnInfo("ClientId", "int", 4, 10, 0, false, true, false, 1),
                new ColumnInfo("FullName", "nvarchar", 200, 0, 0, false, false, false, 2),
                new ColumnInfo("EmailAddress", "nvarchar", 200, 0, 0, true, false, false, 3)
            ], ["ClientId"])],
        []);
}
