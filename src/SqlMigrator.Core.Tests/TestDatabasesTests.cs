using Microsoft.Data.SqlClient;

namespace SqlMigrator.Core.Tests;

[Collection("sql")]
public class TestDatabasesTests
{
    [Fact]
    public void Fixtures_create_both_databases_with_expected_tables()
    {
        TestDatabases.EnsureCreated();

        Assert.Equal(3, CountTables(TestDatabases.SourceConnectionString));
        Assert.Equal(3, CountTables(TestDatabases.TargetConnectionString));
    }

    [Fact]
    public void Target_starts_empty()
    {
        TestDatabases.EnsureCreated();

        using var conn = new SqlConnection(TestDatabases.TargetConnectionString);
        conn.Open();
        using var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.Client", conn);
        Assert.Equal(0, (int)cmd.ExecuteScalar()!);
    }

    private static int CountTables(string connectionString)
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = new SqlCommand("SELECT COUNT(*) FROM sys.tables", conn);
        return (int)cmd.ExecuteScalar()!;
    }
}
