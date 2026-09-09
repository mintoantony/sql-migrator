using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SqlMigrator.Api;

namespace SqlMigrator.Api.Tests;

public class ConnectionEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ConnectionEndpointTests(WebApplicationFactory<Program> factory) =>
        _client = factory.CreateClient();

    private static string Server =>
        Environment.GetEnvironmentVariable("SQLMIGRATOR_TEST_SQL") ?? "localhost";

    /// <summary>
    /// Uses master rather than a fixture database on purpose. This project creates no
    /// fixtures; an earlier version asserted an exact table count against
    /// SqlMigratorDemo_Source, which only exists because SqlMigrator.Core.Tests creates
    /// it — an undeclared dependency on another test assembly. It passed whenever a
    /// previous Core run had left the database behind, and failed in a solution-wide run
    /// where Core was concurrently dropping and recreating it.
    ///
    /// What this test is actually for is the endpoint: that a reachable database yields
    /// Ok, a server version, and a table count. Pinning the count to a specific fixture
    /// tested the fixture instead. SchemaReaderTests already covers the demo schema's
    /// exact shape, and does own the fixtures.
    /// </summary>
    [Fact]
    public async Task Reports_version_and_table_count_for_a_reachable_database()
    {
        var response = await _client.PostAsJsonAsync("/api/connections/test",
            new ConnectionRequest(Server, "master"));

        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<ConnectionTestResponse>())!;

        Assert.True(result.Ok, result.Error);
        Assert.Contains("SQL Server", result.Version);
        Assert.True(result.TableCount >= 0, $"Expected a table count, got {result.TableCount}.");
    }

    [Fact]
    public async Task Reports_a_failure_rather_than_throwing_for_an_unreachable_database()
    {
        var response = await _client.PostAsJsonAsync("/api/connections/test",
            new ConnectionRequest(Server, "NoSuchDatabase_xyz"));

        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<ConnectionTestResponse>())!;

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Never_echoes_a_connection_string()
    {
        var response = await _client.PostAsJsonAsync("/api/connections/test",
            new ConnectionRequest(Server, "master"));

        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("Integrated Security", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TrustServerCertificate", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_a_server_value_that_attempts_key_injection_without_touching_the_database()
    {
        var response = await _client.PostAsJsonAsync("/api/connections/test",
            new ConnectionRequest("localhost;Server=evil.host", "master"));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<ConnectionTestResponse>())!;

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain("evil.host", result.Error);
    }
}
