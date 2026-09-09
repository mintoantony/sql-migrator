using SqlMigrator.Core.Generation;
using SqlMigrator.Core.Mapping;
using SqlMigrator.Core.Schema;

namespace SqlMigrator.Core.Tests;

[Collection("sql")]
public class ScriptGeneratorTests
{
    private static ScriptOptions Options() => new(
        SourceServer: "localhost",
        TargetServer: "localhost",
        GeneratedUtc: new DateTimeOffset(2026, 9, 9, 11, 14, 0, TimeSpan.Zero),
        MappingFileName: "mappings/demo.xml",
        MappingSha256: "3f9a0000");

    private static MigrationMapping DemoMigrationMapping() => new(
        "Demo", new DateTimeOffset(2026, 9, 9, 11, 14, 0, TimeSpan.Zero), "test-model",
        TestDatabases.SourceDb, $"[{TestDatabases.SourceDb}]", TestDatabases.TargetDb,
        [DemoMapping.OrderToOrder(), DemoMapping.CustomerToClient(), DemoMapping.OrderLineToOrderLine()]);

    [Fact]
    public async Task Emits_parents_before_children_regardless_of_mapping_order()
    {
        TestDatabases.EnsureCreated();
        var target = await SchemaReader.ReadAsync(TestDatabases.TargetConnectionString);

        var sql = ScriptGenerator.Generate(DemoMigrationMapping(), target, Options());

        var client = sql.IndexOf("INSERT INTO [dbo].[Client]", StringComparison.Ordinal);
        var order = sql.IndexOf("INSERT INTO [dbo].[Order]", StringComparison.Ordinal);
        var line = sql.IndexOf("INSERT INTO [dbo].[OrderLine]", StringComparison.Ordinal);

        Assert.True(client < order, "Client must be inserted before Order");
        Assert.True(order < line, "Order must be inserted before OrderLine");
    }

    [Fact]
    public async Task Wraps_identity_tables_and_guards_emptiness()
    {
        TestDatabases.EnsureCreated();
        var target = await SchemaReader.ReadAsync(TestDatabases.TargetConnectionString);

        var sql = ScriptGenerator.Generate(DemoMigrationMapping(), target, Options());

        Assert.Contains("SET XACT_ABORT ON;", sql);
        Assert.Contains("BEGIN TRANSACTION;", sql);
        Assert.Contains("COMMIT;", sql);
        Assert.Contains("IF EXISTS (SELECT 1 FROM [dbo].[Client])", sql);
        Assert.Contains("SET IDENTITY_INSERT [dbo].[Client] ON;", sql);
        Assert.Contains("SET IDENTITY_INSERT [dbo].[Client] OFF;", sql);
    }

    [Fact]
    public async Task Emits_expressions_and_the_source_reference_verbatim()
    {
        TestDatabases.EnsureCreated();
        var target = await SchemaReader.ReadAsync(TestDatabases.TargetConnectionString);

        var sql = ScriptGenerator.Generate(DemoMigrationMapping(), target, Options());

        Assert.Contains("CONCAT(FirstName, ' ', LastName)", sql);
        Assert.Contains($"FROM [{TestDatabases.SourceDb}].[dbo].[Customer]", sql);
        Assert.Contains("-- Source: localhost . SqlMigratorDemo_Source", sql);
        Assert.Contains("3f9a0000", sql);
    }

    [Fact]
    public async Task Never_emits_a_credential()
    {
        TestDatabases.EnsureCreated();
        var target = await SchemaReader.ReadAsync(TestDatabases.TargetConnectionString);

        var sql = ScriptGenerator.Generate(DemoMigrationMapping(), target, Options());

        Assert.DoesNotContain("Password", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Integrated Security", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Refuses_to_generate_when_the_foreign_keys_form_a_cycle()
    {
        TestDatabases.EnsureCreated();
        var target = await SchemaReader.ReadAsync(TestDatabases.TargetConnectionString);
        var cyclic = new DbSchema(target.DatabaseName, target.Tables,
        [
            new ForeignKeyInfo("FK_Client_Order", "dbo", "Client", "dbo", "Order"),
            new ForeignKeyInfo("FK_Order_Client", "dbo", "Order", "dbo", "Client")
        ]);

        var ex = Assert.Throws<ScriptGenerationException>(
            () => ScriptGenerator.Generate(DemoMigrationMapping(), cyclic, Options()));

        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
