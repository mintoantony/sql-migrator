using SqlMigrator.Core.Mapping;

namespace SqlMigrator.Core.Tests;

public static class DemoMapping
{
    public static TableMapping CustomerToClient() => new(
        "dbo.Customer", "dbo.Client",
        [
            new ColumnMapping("ClientId",     RuleKind.Copy,     "CustomerId", Origin.Ai, 0.99),
            new ColumnMapping("FullName",     RuleKind.Concat,   "CONCAT(FirstName, ' ', LastName)", Origin.Ai, 0.95),
            new ColumnMapping("ShortName",    RuleKind.Truncate, "LEFT(LastName, 20)", Origin.Ai, 0.9),
            new ColumnMapping("EmailAddress", RuleKind.Copy,     "Email", Origin.Ai, 0.98),
            new ColumnMapping("EmailDomain",  RuleKind.Split,    "SUBSTRING(Email, CHARINDEX('@', Email) + 1, 100)", Origin.Ai, 0.8),
            new ColumnMapping("Summary",      RuleKind.Truncate, "LEFT(Description, 100)", Origin.Ai, 0.85),
            new ColumnMapping("Active",       RuleKind.Case,     "CAST(CASE WHEN IsActive = 1 THEN 1 ELSE 0 END AS bit)", Origin.Ai, 0.9),
            new ColumnMapping("CreatedUtc",   RuleKind.Copy,     "CreatedUtc", Origin.Ai, 0.99),
            new ColumnMapping("MigratedUtc",  RuleKind.Constant, "SYSUTCDATETIME()", Origin.Human)
        ],
        [new UnmappedColumn("Notes", "No corresponding source column; target column is nullable.")],
        Origin.Ai, 0.96, "Both hold one row per customer.");

    // The CAST on Active is not decoration. A bare CASE … THEN 1 ELSE 0 END yields int,
    // and int and bit are different categories, so TypeCompatibility would block it.
    // This is exactly the kind of correction the review screen exists to let a human make.

    public static TableMapping OrderToOrder() => new(
        "dbo.Order", "dbo.Order",
        [
            new ColumnMapping("OrderId",   RuleKind.Copy, "OrderId", Origin.Ai, 0.99),
            new ColumnMapping("ClientId",  RuleKind.Copy, "CustomerId", Origin.Ai, 0.9),
            new ColumnMapping("OrderDate", RuleKind.Copy, "OrderDate", Origin.Ai, 0.99),
            new ColumnMapping("Total",     RuleKind.Copy, "Total", Origin.Ai, 0.99)
        ],
        [], Origin.Ai, 0.98, "Same table name and shape.");

    public static TableMapping OrderLineToOrderLine() => new(
        "dbo.OrderLine", "dbo.OrderLine",
        [
            new ColumnMapping("OrderLineId", RuleKind.Copy, "OrderLineId", Origin.Ai, 0.99),
            new ColumnMapping("OrderId",     RuleKind.Copy, "OrderId", Origin.Ai, 0.99),
            new ColumnMapping("ProductName", RuleKind.Copy, "Product", Origin.Ai, 0.92),
            new ColumnMapping("Quantity",    RuleKind.Copy, "Qty", Origin.Ai, 0.92),
            new ColumnMapping("UnitPrice",   RuleKind.Copy, "UnitPrice", Origin.Ai, 0.99)
        ],
        [], Origin.Ai, 0.97, "Renamed columns only.");
}
