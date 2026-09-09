using SqlMigrator.Core.Schema;
using SqlMigrator.Core.Validation;

namespace SqlMigrator.Core.Tests;

public class TypeCompatibilityTests
{
    private static ColumnInfo Target(string type, int maxLength = 0, byte precision = 0, byte scale = 0) =>
        new("T", type, maxLength, precision, scale, IsNullable: true, IsIdentity: false, HasDefault: false, OrdinalPosition: 1);

    private static SqlTypeInfo Source(string type, int maxLength = 0, byte precision = 0, byte scale = 0) =>
        new(type, maxLength, precision, scale, IsNullable: true);

    [Fact]
    public void Same_type_same_size_is_clean()
    {
        var check = TypeCompatibility.Check(Source("nvarchar", 100), Target("nvarchar", 100));

        Assert.True(check.Compatible);
        Assert.False(check.Narrowing);
    }

    [Fact]
    public void Widening_within_a_category_is_clean()
    {
        Assert.True(TypeCompatibility.Check(Source("int"), Target("bigint")).Compatible);
        Assert.False(TypeCompatibility.Check(Source("nvarchar", 50), Target("nvarchar", 200)).Narrowing);
    }

    [Fact]
    public void Narrowing_a_string_is_compatible_but_flagged()
    {
        var check = TypeCompatibility.Check(Source("nvarchar", 200), Target("nvarchar", 100));

        Assert.True(check.Compatible);
        Assert.True(check.Narrowing);
        Assert.Contains("200", check.Message);
    }

    [Fact]
    public void Narrowing_a_decimal_is_flagged()
    {
        var check = TypeCompatibility.Check(Source("decimal", 0, 18, 4), Target("decimal", 0, 9, 2));

        Assert.True(check.Compatible);
        Assert.True(check.Narrowing);
    }

    [Fact]
    public void Max_length_target_is_never_narrowing()
    {
        var check = TypeCompatibility.Check(Source("nvarchar", 400), Target("nvarchar", -1));

        Assert.False(check.Narrowing);
    }

    [Theory]
    [InlineData("nvarchar", "int")]
    [InlineData("datetime2", "bit")]
    [InlineData("uniqueidentifier", "int")]
    public void Different_categories_are_incompatible(string sourceType, string targetType)
    {
        var check = TypeCompatibility.Check(Source(sourceType, 50), Target(targetType, 50));

        Assert.False(check.Compatible);
        Assert.Contains(sourceType, check.Message);
    }

    [Fact]
    public void Category_lookup_is_case_insensitive()
    {
        Assert.Equal(TypeCategory.String, TypeCompatibility.CategoryOf("NVARCHAR"));
        Assert.Equal(TypeCategory.Integer, TypeCompatibility.CategoryOf("BigInt"));
    }

    [Fact]
    public void Identical_unmapped_type_geography_to_geography_is_compatible()
    {
        var check = TypeCompatibility.Check(Source("geography"), Target("geography"));

        Assert.True(check.Compatible);
        Assert.False(check.Narrowing);
    }

    [Fact]
    public void Identical_unmapped_type_hierarchyid_to_hierarchyid_is_compatible()
    {
        var check = TypeCompatibility.Check(Source("hierarchyid"), Target("hierarchyid"));

        Assert.True(check.Compatible);
        Assert.False(check.Narrowing);
    }

    [Fact]
    public void Different_unmapped_types_are_still_incompatible()
    {
        var check = TypeCompatibility.Check(Source("geography"), Target("nvarchar", 50));

        Assert.False(check.Compatible);
    }

    [Fact]
    public void Narrowing_a_string_of_the_same_type_name_is_still_flagged()
    {
        var check = TypeCompatibility.Check(Source("nvarchar", 200), Target("nvarchar", 100));

        Assert.True(check.Compatible);
        Assert.True(check.Narrowing);
    }
}
