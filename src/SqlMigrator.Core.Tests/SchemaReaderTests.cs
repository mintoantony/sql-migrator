using SqlMigrator.Core.Schema;

namespace SqlMigrator.Core.Tests;

[Collection("sql")]
public class SchemaReaderTests
{
    [Fact]
    public async Task Reads_tables_columns_keys_and_foreign_keys()
    {
        TestDatabases.EnsureCreated();

        var schema = await SchemaReader.ReadAsync(TestDatabases.SourceConnectionString);

        Assert.Equal(TestDatabases.SourceDb, schema.DatabaseName);
        Assert.Equal(3, schema.Tables.Count);

        var customer = schema.Find("dbo.Customer")!;
        Assert.Equal(["CustomerId"], customer.PrimaryKeyColumns);

        var id = customer.Columns.Single(c => c.Name == "CustomerId");
        Assert.True(id.IsIdentity);
        Assert.False(id.IsNullable);
        Assert.Equal("int", id.DataType);

        var email = customer.Columns.Single(c => c.Name == "Email");
        Assert.True(email.IsNullable);
        Assert.Equal("nvarchar", email.DataType);
        Assert.Equal(200, email.MaxLength);   // characters, not bytes

        var total = schema.Find("dbo.Order")!.Columns.Single(c => c.Name == "Total");
        Assert.Equal((byte)18, total.Precision);
        Assert.Equal((byte)2, total.Scale);

        var fk = schema.ForeignKeys.Single(f => f.ParentTable == "Order");
        Assert.Equal("Customer", fk.ReferencedTable);
        Assert.Equal("dbo", fk.ReferencedSchema);
    }

    [Fact]
    public async Task Find_is_case_insensitive_and_returns_null_when_absent()
    {
        TestDatabases.EnsureCreated();

        var schema = await SchemaReader.ReadAsync(TestDatabases.SourceConnectionString);

        Assert.NotNull(schema.Find("DBO.CUSTOMER"));
        Assert.Null(schema.Find("dbo.NoSuchTable"));
    }
}
