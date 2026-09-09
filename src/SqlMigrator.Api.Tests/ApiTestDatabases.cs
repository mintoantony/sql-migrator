using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SqlMigrator.Api.Tests;

/// <summary>
/// Creates the Api test project's own fixture databases once per test run.
///
/// SqlMigrator.Api.Tests used to validate expressions against SqlMigratorDemo_Source/_Target,
/// but those only exist because SqlMigrator.Core.Tests creates them — an undeclared dependency
/// on another test assembly, and a race against it: a solution-wide run could see Core.Tests'
/// EndToEndTests drop and recreate those databases mid-run, so Api tests intermittently failed
/// with "Login failed for user" (the database briefly did not exist). These tests genuinely need
/// a real schema (they validate expressions against it), so unlike ConnectionEndpointTests
/// (which switched to master because it never needed a fixture), the fix here is for this
/// project to own a fixture pair no other project touches.
/// </summary>
public static class ApiTestDatabases
{
    public const string SourceDb = "SqlMigratorApiDemo_Source";
    public const string TargetDb = "SqlMigratorApiDemo_Target";

    public static string Server =>
        Environment.GetEnvironmentVariable("SQLMIGRATOR_TEST_SQL") ?? "localhost";

    public static string MasterConnectionString => Build("master");
    public static string SourceConnectionString => Build(SourceDb);
    public static string TargetConnectionString => Build(TargetDb);

    private static readonly Lock Gate = new();
    private static bool _created;

    public static void EnsureCreated()
    {
        lock (Gate)
        {
            if (_created) return;
            // fixtures/source.sql and target.sql hardcode the Core pair's own names
            // (SqlMigratorDemo_Source/_Target). Substitute this project's names so
            // the script targets SqlMigratorApiDemo_Source/_Target instead — running
            // it unmodified would drop and recreate the Core pair out from under
            // SqlMigrator.Core.Tests, reintroducing the exact race this fixes.
            Execute(FindFixture("source.sql"), "SqlMigratorDemo_Source", SourceDb);
            Execute(FindFixture("target.sql"), "SqlMigratorDemo_Target", TargetDb);
            _created = true;
        }
    }

    private static string Build(string database) =>
        $"Server={Server};Database={database};Integrated Security=True;TrustServerCertificate=True";

    /// <summary>Walks up from the test binaries to the repo root to locate fixtures/.</summary>
    private static string FindFixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "fixtures", name);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate fixtures/{name} above {AppContext.BaseDirectory}");
    }

    /// <summary>
    /// Runs a script, splitting on GO because SqlCommand cannot handle batch separators, after
    /// substituting the placeholder database name the fixture file hardcodes with this
    /// project's own database name.
    /// </summary>
    private static void Execute(string scriptPath, string placeholderDb, string actualDb)
    {
        var script = File.ReadAllText(scriptPath).Replace(placeholderDb, actualDb, StringComparison.Ordinal);
        var batches = Regex.Split(
            script,
            @"^\s*GO\s*$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);

        using var conn = new SqlConnection(MasterConnectionString);
        conn.Open();
        foreach (var batch in batches)
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            using var cmd = new SqlCommand(batch, conn) { CommandTimeout = 120 };
            cmd.ExecuteNonQuery();
        }
    }
}

/// <summary>Creates the Api project's fixture databases once before any test in the collection runs.</summary>
public sealed class ApiSqlFixture : IAsyncLifetime
{
    public Task InitializeAsync()
    {
        ApiTestDatabases.EnsureCreated();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

[CollectionDefinition("api-sql")]
public class ApiSqlCollection : ICollectionFixture<ApiSqlFixture>;
