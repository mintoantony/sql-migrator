using SqlMigrator.Api;

namespace SqlMigrator.Api.Tests;

/// <summary>
/// No database required: <see cref="MappingFileNames.BuildPath"/> is exercised purely as a path
/// builder, proving a caller-supplied TargetDatabase can't escape the mappings directory.
/// </summary>
public class MappingFileNamesTests
{
    private static readonly string Directory =
        Path.Combine(Path.GetTempPath(), "SqlMigratorMappingFileNamesTests");

    [Fact]
    public void Rejects_a_target_database_containing_a_traversal_segment()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => MappingFileNames.BuildPath(Directory, @"..\..\evil", DateTimeOffset.UtcNow));

        Assert.Equal("targetDatabase", ex.ParamName);
        Assert.DoesNotContain("evil", ex.Message);
    }

    [Fact]
    public void Rejects_a_rooted_target_database_path()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => MappingFileNames.BuildPath(Directory, @"C:\Windows\evil", DateTimeOffset.UtcNow));

        Assert.Equal("targetDatabase", ex.ParamName);
        Assert.DoesNotContain("evil", ex.Message);
    }

    [Fact]
    public void Rejects_a_forward_slash_rooted_target_database_path()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => MappingFileNames.BuildPath(Directory, "/etc/evil", DateTimeOffset.UtcNow));

        Assert.Equal("targetDatabase", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_an_empty_or_whitespace_target_database(string targetDatabase)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => MappingFileNames.BuildPath(Directory, targetDatabase, DateTimeOffset.UtcNow));

        Assert.Equal("targetDatabase", ex.ParamName);
    }

    [Fact]
    public void Rejects_an_unreasonably_long_target_database()
    {
        var targetDatabase = new string('a', 500);

        var ex = Assert.Throws<ArgumentException>(
            () => MappingFileNames.BuildPath(Directory, targetDatabase, DateTimeOffset.UtcNow));

        Assert.Equal("targetDatabase", ex.ParamName);
    }

    [Fact]
    public void Accepts_a_legitimate_database_name_and_resolves_inside_the_directory()
    {
        var timestamp = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

        var path = MappingFileNames.BuildPath(Directory, "SqlMigratorDemo_Target", timestamp);

        Assert.StartsWith(Path.GetFullPath(Directory), path);
        Assert.EndsWith("SqlMigratorDemo_Target-20260909-120000.xml", path);
    }
}
