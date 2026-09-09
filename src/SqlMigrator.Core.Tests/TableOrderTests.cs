using SqlMigrator.Core.Generation;
using SqlMigrator.Core.Schema;

namespace SqlMigrator.Core.Tests;

public class TableOrderTests
{
    private static ForeignKeyInfo Fk(string child, string parent) =>
        new($"FK_{child}_{parent}", "dbo", child, "dbo", parent);

    [Fact]
    public void Parents_come_before_children()
    {
        string[] tables = ["dbo.OrderLine", "dbo.Order", "dbo.Client"];
        ForeignKeyInfo[] keys = [Fk("Order", "Client"), Fk("OrderLine", "Order")];

        Assert.True(TableOrder.TrySort(tables, keys, out var ordered, out _));

        Assert.Equal(["dbo.Client", "dbo.Order", "dbo.OrderLine"], ordered);
    }

    [Fact]
    public void Unrelated_tables_keep_a_stable_alphabetical_order()
    {
        string[] tables = ["dbo.Zebra", "dbo.Apple"];

        Assert.True(TableOrder.TrySort(tables, [], out var ordered, out _));

        Assert.Equal(["dbo.Apple", "dbo.Zebra"], ordered);
    }

    [Fact]
    public void Foreign_keys_to_tables_outside_the_mapping_are_ignored()
    {
        string[] tables = ["dbo.Order"];
        ForeignKeyInfo[] keys = [Fk("Order", "Client")];   // Client is not being migrated

        Assert.True(TableOrder.TrySort(tables, keys, out var ordered, out _));

        Assert.Equal(["dbo.Order"], ordered);
    }

    [Fact]
    public void Self_reference_is_not_a_cycle()
    {
        string[] tables = ["dbo.Employee"];
        ForeignKeyInfo[] keys = [Fk("Employee", "Employee")];

        Assert.True(TableOrder.TrySort(tables, keys, out var ordered, out _));

        Assert.Equal(["dbo.Employee"], ordered);
    }

    [Fact]
    public void A_cycle_is_reported_with_the_tables_involved()
    {
        string[] tables = ["dbo.A", "dbo.B"];
        ForeignKeyInfo[] keys = [Fk("A", "B"), Fk("B", "A")];

        Assert.False(TableOrder.TrySort(tables, keys, out _, out var cycle));

        Assert.Contains("dbo.A", cycle);
        Assert.Contains("dbo.B", cycle);
    }
}
