using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SqlMigrator.Ai;
using SqlMigrator.Api;

namespace SqlMigrator.Api.Tests;

public class ReviewEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ReviewEndpointTests(WebApplicationFactory<Program> factory) =>
        _client = factory
            .WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IChatClient, ScriptedChatClient>()))
            .CreateClient();

    private static string Server =>
        Environment.GetEnvironmentVariable("SQLMIGRATOR_TEST_SQL") ?? "localhost";

    private async Task<(string SessionId, MappingDto Mapping)> ReadySession()
    {
        var started = (await (await _client.PostAsJsonAsync("/api/analyse", new AnalyseRequest(
            new ConnectionRequest(Server, "SqlMigratorDemo_Source"),
            new ConnectionRequest(Server, "SqlMigratorDemo_Target"),
            "[SqlMigratorDemo_Source]"))).Content.ReadFromJsonAsync<AnalyseStarted>())!;

        for (var attempt = 0; attempt < 120; attempt++)
        {
            var status = (await _client.GetFromJsonAsync<SessionStatus>($"/api/analyse/{started.SessionId}"))!;
            if (status.State == "ready") return (started.SessionId, status.Mapping!);
            if (status.State == "failed") throw new InvalidOperationException(status.Error);
            await Task.Delay(250);
        }
        throw new TimeoutException("Analysis did not finish.");
    }

    [Fact]
    public async Task Accepts_a_valid_expression_and_reports_its_type()
    {
        var (sessionId, _) = await ReadySession();

        // FirstName and LastName are each nvarchar(100) on the real fixture database, so a bare
        // CONCAT(FirstName, ' ', LastName) yields nvarchar(201) — one character too wide for
        // Client.FullName's nvarchar(200) — and would (correctly) report a narrowing warning.
        // LEFT(..., 200) keeps this a genuinely issue-free expression.
        var result = (await (await _client.PostAsJsonAsync("/api/expression/validate",
            new ValidateExpressionRequest(sessionId, "dbo.Customer", "dbo.Client", "FullName",
                "LEFT(CONCAT(FirstName, ' ', LastName), 200)")))
            .Content.ReadFromJsonAsync<ValidateExpressionResponse>())!;

        Assert.True(result.Ok);
        Assert.NotNull(result.ResultType);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task Rejects_an_expression_naming_a_column_that_does_not_exist()
    {
        var (sessionId, _) = await ReadySession();

        var result = (await (await _client.PostAsJsonAsync("/api/expression/validate",
            new ValidateExpressionRequest(sessionId, "dbo.Customer", "dbo.Client", "FullName", "NoSuchColumn")))
            .Content.ReadFromJsonAsync<ValidateExpressionResponse>())!;

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "EXP002");
    }

    [Fact]
    public async Task Rejects_an_expression_the_screen_refuses()
    {
        var (sessionId, _) = await ReadySession();

        var result = (await (await _client.PostAsJsonAsync("/api/expression/validate",
            new ValidateExpressionRequest(sessionId, "dbo.Customer", "dbo.Client", "FullName",
                "FirstName; DROP TABLE dbo.Customer")))
            .Content.ReadFromJsonAsync<ValidateExpressionResponse>())!;

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "EXP001");
    }

    [Fact]
    public async Task Rejects_an_expression_naming_a_malformed_source_table_without_a_500()
    {
        var (sessionId, _) = await ReadySession();

        var response = await _client.PostAsJsonAsync("/api/expression/validate",
            new ValidateExpressionRequest(sessionId, "dbo.", "dbo.Client", "FullName", "FirstName"));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<ValidateExpressionResponse>())!;
        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Code == "EXP003");
    }

    [Fact]
    public async Task Saves_the_mapping_as_xml_and_returns_its_hash()
    {
        var (sessionId, mapping) = await ReadySession();

        var result = (await (await _client.PostAsJsonAsync("/api/mapping/save",
            new SaveMappingRequest(sessionId, mapping)))
            .Content.ReadFromJsonAsync<SaveMappingResponse>())!;

        Assert.True(File.Exists(result.Path));
        Assert.Equal(64, result.Sha256.Length);
        Assert.Contains("<migration", File.ReadAllText(result.Path));
    }

    [Fact]
    public async Task Generates_a_script_that_inserts_parents_first()
    {
        var (sessionId, mapping) = await ReadySession();

        var result = (await (await _client.PostAsJsonAsync("/api/script/generate",
            new GenerateScriptRequest(sessionId, mapping)))
            .Content.ReadFromJsonAsync<GenerateScriptResponse>())!;

        Assert.Contains("BEGIN TRANSACTION;", result.Sql);
        Assert.True(result.Sql.IndexOf("INSERT INTO [dbo].[Client]", StringComparison.Ordinal)
                    < result.Sql.IndexOf("INSERT INTO [dbo].[Order]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Refuses_to_generate_a_script_when_a_blocking_issue_stands()
    {
        var (sessionId, mapping) = await ReadySession();

        var broken = mapping with
        {
            Tables = mapping.Tables.Select(t => t.TargetTable != "dbo.Client" ? t : t with
            {
                Columns = t.Columns
                    .Select(c => c.TargetColumn == "FullName" ? c with { Expression = "NoSuchColumn" } : c)
                    .ToList()
            }).ToList()
        };

        var response = await _client.PostAsJsonAsync("/api/script/generate",
            new GenerateScriptRequest(sessionId, broken));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }
}
