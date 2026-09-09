using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SqlMigrator.Ai;
using SqlMigrator.Api;

namespace SqlMigrator.Api.Tests;

/// <summary>Replaces the real model with scripted responses so the endpoint test needs no API key.</summary>
public sealed class ScriptedChatClient : IChatClient
{
    private int _index;

    public Task<string> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var response = _index++ == 0 ? Matches : Columns(userPrompt);
        return Task.FromResult(response);
    }

    private const string Matches = """
        {"matches":[
          {"sourceTable":"dbo.Customer","targetTable":"dbo.Client","confidence":0.96,"reason":"Customers."},
          {"sourceTable":"dbo.Order","targetTable":"dbo.Order","confidence":0.98,"reason":"Orders."},
          {"sourceTable":"dbo.OrderLine","targetTable":"dbo.OrderLine","confidence":0.97,"reason":"Lines."}]}
        """;

    private static string Columns(string userPrompt) =>
        userPrompt.Contains("dbo.Client") ? """
            {"columns":[
              {"targetColumn":"ClientId","rule":"copy","expression":"CustomerId","confidence":0.99,"reason":"Key."},
              {"targetColumn":"FullName","rule":"concat","expression":"CONCAT(FirstName, ' ', LastName)","confidence":0.9,"reason":"Two parts."},
              {"targetColumn":"ShortName","rule":"truncate","expression":"LEFT(LastName, 20)","confidence":0.85,"reason":"Fits 20."},
              {"targetColumn":"EmailAddress","rule":"copy","expression":"Email","confidence":0.95,"reason":"Same."},
              {"targetColumn":"EmailDomain","rule":"split","expression":"SUBSTRING(Email, CHARINDEX('@', Email) + 1, 100)","confidence":0.8,"reason":"After the at sign."},
              {"targetColumn":"Summary","rule":"truncate","expression":"LEFT(Description, 100)","confidence":0.8,"reason":"Fits 100."},
              {"targetColumn":"Active","rule":"case","expression":"CAST(CASE WHEN IsActive = 1 THEN 1 ELSE 0 END AS bit)","confidence":0.9,"reason":"Bit."},
              {"targetColumn":"CreatedUtc","rule":"copy","expression":"CreatedUtc","confidence":0.99,"reason":"Same."},
              {"targetColumn":"MigratedUtc","rule":"constant","expression":"SYSUTCDATETIME()","confidence":0.6,"reason":"Now."}],
             "unmapped":[{"targetColumn":"Notes","reason":"No source column."}]}
            """
        : userPrompt.Contains("dbo.OrderLine") ? """
            {"columns":[
              {"targetColumn":"OrderLineId","rule":"copy","expression":"OrderLineId","confidence":0.99,"reason":"Key."},
              {"targetColumn":"OrderId","rule":"copy","expression":"OrderId","confidence":0.99,"reason":"Key."},
              {"targetColumn":"ProductName","rule":"copy","expression":"Product","confidence":0.9,"reason":"Renamed."},
              {"targetColumn":"Quantity","rule":"copy","expression":"Qty","confidence":0.9,"reason":"Renamed."},
              {"targetColumn":"UnitPrice","rule":"copy","expression":"UnitPrice","confidence":0.99,"reason":"Same."}]}
            """
        : """
            {"columns":[
              {"targetColumn":"OrderId","rule":"copy","expression":"OrderId","confidence":0.99,"reason":"Key."},
              {"targetColumn":"ClientId","rule":"copy","expression":"CustomerId","confidence":0.9,"reason":"Renamed FK."},
              {"targetColumn":"OrderDate","rule":"copy","expression":"OrderDate","confidence":0.99,"reason":"Same."},
              {"targetColumn":"Total","rule":"copy","expression":"Total","confidence":0.99,"reason":"Same."}]}
            """;
}

public class AnalysisEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public AnalysisEndpointTests(WebApplicationFactory<Program> factory) =>
        _client = factory
            .WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IChatClient, ScriptedChatClient>()))
            .CreateClient();

    private static string Server =>
        Environment.GetEnvironmentVariable("SQLMIGRATOR_TEST_SQL") ?? "localhost";

    private static AnalyseRequest Request() => new(
        new ConnectionRequest(Server, "SqlMigratorDemo_Source"),
        new ConnectionRequest(Server, "SqlMigratorDemo_Target"),
        "[SqlMigratorDemo_Source]");

    private async Task<SessionStatus> RunToCompletion()
    {
        var started = (await (await _client.PostAsJsonAsync("/api/analyse", Request()))
            .Content.ReadFromJsonAsync<AnalyseStarted>())!;

        for (var attempt = 0; attempt < 120; attempt++)
        {
            var status = (await _client.GetFromJsonAsync<SessionStatus>($"/api/analyse/{started.SessionId}"))!;
            if (status.State != "running") return status;
            await Task.Delay(250);
        }
        throw new TimeoutException("Analysis did not finish within 30 seconds.");
    }

    [Fact]
    public async Task Produces_a_validated_mapping_for_all_three_tables()
    {
        var status = await RunToCompletion();

        Assert.Equal("ready", status.State);
        Assert.Equal(3, status.Mapping!.Tables.Count);
        Assert.Contains(status.Mapping.Tables, t => t.TargetTable == "dbo.Client");
        Assert.Empty(status.Failures);
    }

    [Fact]
    public async Task Attaches_issues_from_the_validator()
    {
        var status = await RunToCompletion();

        // Description is nvarchar(400) truncated to 100 by the rule, so no narrowing warning;
        // but IDENTITY_INSERT is informational on every table with a mapped identity column.
        Assert.Contains(status.Issues, i => i.Code == "IDN001" && i.Severity == "Info");
        Assert.DoesNotContain(status.Issues, i => i.Severity == "Blocking");
    }

    [Fact]
    public async Task Unknown_session_is_a_404()
    {
        var response = await _client.GetAsync("/api/analyse/not-a-session");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
