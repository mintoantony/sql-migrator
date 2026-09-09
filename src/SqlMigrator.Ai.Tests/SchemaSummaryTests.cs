using SqlMigrator.Model.Schema;

namespace SqlMigrator.Ai.Tests;

public class SchemaSummaryTests
{
    private static DbSchema Schema() => new(
        "DemoDb",
        [
            new TableInfo("dbo", "Customer",
            [
                new ColumnInfo("CustomerId", "int", 4, 10, 0, false, true, false, 1),
                new ColumnInfo("Email", "nvarchar", 200, 0, 0, true, false, false, 2)
            ],
            ["CustomerId"])
        ],
        [new ForeignKeyInfo("FK_Order_Customer", "dbo", "Order", "dbo", "Customer")]);

    [Fact]
    public void Renders_table_column_type_nullability_and_key()
    {
        var text = SchemaSummary.Render(Schema());

        Assert.Contains("dbo.Customer", text);
        Assert.Contains("CustomerId int NOT NULL IDENTITY PK", text);
        Assert.Contains("Email nvarchar(200) NULL", text);
    }

    [Fact]
    public void Renders_foreign_keys_so_the_model_can_see_relationships()
    {
        var text = SchemaSummary.Render(Schema());

        Assert.Contains("dbo.Order -> dbo.Customer", text);
    }

    [Fact]
    public void Renders_max_length_as_MAX()
    {
        var table = new TableInfo("dbo", "T",
            [new ColumnInfo("Body", "nvarchar", -1, 0, 0, true, false, false, 1)], []);

        Assert.Contains("Body nvarchar(MAX) NULL", SchemaSummary.RenderTable(table));
    }

    [Fact]
    public void Renders_decimal_precision_and_scale()
    {
        var table = new TableInfo("dbo", "T",
            [new ColumnInfo("Total", "decimal", 9, 18, 2, false, false, false, 1)], []);

        Assert.Contains("Total decimal(18,2) NOT NULL", SchemaSummary.RenderTable(table));
    }
}
