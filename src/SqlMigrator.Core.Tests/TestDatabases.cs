using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SqlMigrator.Core.Tests;

/// <summary>Creates the demo fixture databases once per test run.</summary>
public static class TestDatabases
{
    public const string SourceDb = "SqlMigratorDemo_Source";
    public const string TargetDb = "SqlMigratorDemo_Target";

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
            Execute(FindFixture("source.sql"));
            Execute(FindFixture("target.sql"));
            _created = true;
        }
    }

    /// <summary>Forces the next EnsureCreated to rebuild both databases from scratch.</summary>
    public static void Reset()
    {
        lock (Gate) { _created = false; }
        EnsureCreated();
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

    /// <summary>Runs a script, splitting on GO because SqlCommand cannot handle batch separators.</summary>
    private static void Execute(string scriptPath)
    {
        var batches = Regex.Split(
            File.ReadAllText(scriptPath),
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
