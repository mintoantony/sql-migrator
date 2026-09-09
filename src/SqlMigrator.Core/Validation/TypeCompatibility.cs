using SqlMigrator.Model.Schema;

namespace SqlMigrator.Core.Validation;

/// <summary>The type an expression yields, as SQL Server reported it.</summary>
public sealed record SqlTypeInfo(
    string DataType,
    int MaxLength,
    byte Precision,
    byte Scale,
    bool IsNullable);

public enum TypeCategory
{
    String, Integer, ExactNumeric, ApproximateNumeric, DateTime, Binary, Bit, Guid, Xml, Other
}

public sealed record TypeCheck(bool Compatible, bool Narrowing, string? Message);

public static class TypeCompatibility
{
    private static readonly Dictionary<string, TypeCategory> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["char"] = TypeCategory.String,     ["varchar"] = TypeCategory.String,
        ["nchar"] = TypeCategory.String,    ["nvarchar"] = TypeCategory.String,
        ["text"] = TypeCategory.String,     ["ntext"] = TypeCategory.String,
        ["sysname"] = TypeCategory.String,

        ["tinyint"] = TypeCategory.Integer, ["smallint"] = TypeCategory.Integer,
        ["int"] = TypeCategory.Integer,     ["bigint"] = TypeCategory.Integer,

        ["decimal"] = TypeCategory.ExactNumeric, ["numeric"] = TypeCategory.ExactNumeric,
        ["money"] = TypeCategory.ExactNumeric,   ["smallmoney"] = TypeCategory.ExactNumeric,

        ["float"] = TypeCategory.ApproximateNumeric, ["real"] = TypeCategory.ApproximateNumeric,

        ["date"] = TypeCategory.DateTime,      ["time"] = TypeCategory.DateTime,
        ["datetime"] = TypeCategory.DateTime,  ["datetime2"] = TypeCategory.DateTime,
        ["smalldatetime"] = TypeCategory.DateTime, ["datetimeoffset"] = TypeCategory.DateTime,

        ["binary"] = TypeCategory.Binary, ["varbinary"] = TypeCategory.Binary, ["image"] = TypeCategory.Binary,

        ["bit"] = TypeCategory.Bit,
        ["uniqueidentifier"] = TypeCategory.Guid,
        ["xml"] = TypeCategory.Xml
    };

    /// <summary>Integer widths in bytes, used to decide whether an integer conversion narrows.</summary>
    private static readonly Dictionary<string, int> IntegerWidths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tinyint"] = 1, ["smallint"] = 2, ["int"] = 4, ["bigint"] = 8
    };

    public static TypeCategory CategoryOf(string sqlTypeName) =>
        Categories.TryGetValue(sqlTypeName, out var category) ? category : TypeCategory.Other;

    public static TypeCheck Check(SqlTypeInfo source, ColumnInfo target)
    {
        var sourceCategory = CategoryOf(source.DataType);
        var targetCategory = CategoryOf(target.DataType);

        if (sourceCategory != targetCategory || sourceCategory == TypeCategory.Other)
        {
            // An unmapped type (spatial, hierarchyid, sql_variant, CLR UDTs, …) has no length or
            // precision semantics to compare, but copying a column into a column of the exact same
            // type is trivially valid — only rescue pairs this guard would otherwise reject outright.
            if (string.Equals(source.DataType, target.DataType, StringComparison.OrdinalIgnoreCase))
            {
                return new TypeCheck(true, false, null);
            }

            return new TypeCheck(false, false,
                $"Expression yields {source.DataType} but target column is {target.DataType}; " +
                "these types are not convertible by an INSERT … SELECT.");
        }

        return sourceCategory switch
        {
            TypeCategory.String or TypeCategory.Binary => CheckLength(source, target),
            TypeCategory.Integer => CheckIntegerWidth(source, target),
            TypeCategory.ExactNumeric => CheckPrecision(source, target),
            _ => new TypeCheck(true, false, null)
        };
    }

    private static TypeCheck CheckLength(SqlTypeInfo source, ColumnInfo target)
    {
        if (target.MaxLength == -1) return new TypeCheck(true, false, null);   // MAX holds anything
        if (source.MaxLength == -1 || source.MaxLength > target.MaxLength)
        {
            var sourceLength = source.MaxLength == -1 ? "MAX" : source.MaxLength.ToString();
            return new TypeCheck(true, true,
                $"Narrowing: expression yields {source.DataType}({sourceLength}) into {target.DataType}({target.MaxLength}). " +
                "Values longer than the target will fail at run time unless the expression truncates them.");
        }
        return new TypeCheck(true, false, null);
    }

    private static TypeCheck CheckIntegerWidth(SqlTypeInfo source, ColumnInfo target)
    {
        var sourceWidth = IntegerWidths.GetValueOrDefault(source.DataType, 4);
        var targetWidth = IntegerWidths.GetValueOrDefault(target.DataType, 4);
        return sourceWidth > targetWidth
            ? new TypeCheck(true, true, $"Narrowing: {source.DataType} into {target.DataType}.")
            : new TypeCheck(true, false, null);
    }

    private static TypeCheck CheckPrecision(SqlTypeInfo source, ColumnInfo target)
    {
        var integralSource = source.Precision - source.Scale;
        var integralTarget = target.Precision - target.Scale;
        return integralSource > integralTarget || source.Scale > target.Scale
            ? new TypeCheck(true, true,
                $"Narrowing: {source.DataType}({source.Precision},{source.Scale}) into " +
                $"{target.DataType}({target.Precision},{target.Scale}).")
            : new TypeCheck(true, false, null);
    }
}
