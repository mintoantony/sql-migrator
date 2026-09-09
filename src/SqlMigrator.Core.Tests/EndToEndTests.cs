using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlMigrator.Core.Generation;
using SqlMigrator.Core.Schema;
using SqlMigrator.Core.Validation;
using SqlMigrator.Model.Mapping;
using SqlMigrator.Model.Schema;

namespace SqlMigrator.Core.Tests;

[Collection("sql")]
public class EndToEndTests
{
    private static MigrationMapping BuildMapping() => new(
        "Demo", new DateTimeOffset(2026, 9, 9, 11, 14, 0, TimeSpan.Zero), null,
        TestDatabases.SourceDb, $"[{TestDatabases.SourceDb}]", TestDatabases.TargetDb,
        [DemoMapping.CustomerToClient(), DemoMapping.OrderToOrder(), DemoMapping.OrderLineToOrderLine()]);

    [Fact]
    public async Task Generated_script_validates_runs_and_moves_every_row()
    {
        TestDatabases.Reset();

        var source = await SchemaReader.ReadAsync(TestDatabases.SourceConnectionString);
        var target = await SchemaReader.ReadAsync(TestDatabases.TargetConnectionString);
        var mapping = BuildMapping();

        // 1. It validates.
        var compiler = new SqlExpressionCompiler(TestDatabases.SourceConnectionString);
        var issues = await new MappingValidator(source, target, compiler).ValidateAsync(mapping);
        Assert.False(issues.HasBlocking(),
            "Blocking issues: " + string.Join(" | ", issues.Where(i => i.Severity == Severity.Blocking).Select(i => i.Message)));

        // 2. It round-trips through XML unchanged.
        var xml = MappingXml.Write(mapping);
        Assert.Equal(mapping, MappingXml.Read(xml));

        // 3. It generates.
        var sql = ScriptGenerator.Generate(mapping, target, new ScriptOptions(
            TestDatabases.Server, TestDatabases.Server,
            mapping.GeneratedUtc, "mappings/demo.xml", Sha256(xml)));

        // 4. It runs.
        ExecuteBatches(sql, TestDatabases.TargetConnectionString);

        // 5. Every row arrived, with keys preserved.
        Assert.Equal(Scalar(TestDatabases.SourceConnectionString, "SELECT COUNT(*) FROM dbo.Customer"),
                     Scalar(TestDatabases.TargetConnectionString, "SELECT COUNT(*) FROM dbo.Client"));
        Assert.Equal(Scalar(TestDatabases.SourceConnectionString, "SELECT COUNT(*) FROM dbo.[Order]"),
                     Scalar(TestDatabases.TargetConnectionString, "SELECT COUNT(*) FROM dbo.[Order]"));
        Assert.Equal(Scalar(TestDatabases.SourceConnectionString, "SELECT CHECKSUM_AGG(CustomerId) FROM dbo.Customer"),
                     Scalar(TestDatabases.TargetConnectionString, "SELECT CHECKSUM_AGG(ClientId) FROM dbo.Client"));

        // 6. The rules actually applied.
        Assert.True(Scalar(TestDatabases.TargetConnectionString, "SELECT MAX(LEN(ShortName)) FROM dbo.Client") <= 20);
        Assert.Equal(0, Scalar(TestDatabases.TargetConnectionString, "SELECT COUNT(*) FROM dbo.Client WHERE EmailDomain <> 'example.com'"));
        Assert.Equal(100, Scalar(TestDatabases.TargetConnectionString, "SELECT MAX(LEN(Summary)) FROM dbo.Client"));
        Assert.Equal(0, Scalar(TestDatabases.TargetConnectionString, "SELECT COUNT(*) FROM dbo.Client WHERE FullName NOT LIKE '% %'"));
    }

    [Fact]
    public async Task Rerunning_the_script_is_refused_rather_than_colliding()
    {
        TestDatabases.Reset();
        var target = await SchemaReader.ReadAsync(TestDatabases.TargetConnectionString);
        var mapping = BuildMapping();
        var sql = ScriptGenerator.Generate(mapping, target, new ScriptOptions(
            TestDatabases.Server, TestDatabases.Server, mapping.GeneratedUtc, "mappings/demo.xml", "0"));

        ExecuteBatches(sql, TestDatabases.TargetConnectionString);

        var ex = Assert.Throws<SqlException>(() => ExecuteBatches(sql, TestDatabases.TargetConnectionString));
        Assert.Contains("is not empty", ex.Message);
    }

    private static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static void ExecuteBatches(string sql, string connectionString)
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        cmd.ExecuteNonQuery();
    }

    private static int Scalar(string connectionString, string sql)
    {
        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = new SqlCommand(sql, conn);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
