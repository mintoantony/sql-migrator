namespace SqlMigrator.Ai.Tests;

/// <summary>
/// The spec's guarantee that no data row can reach the model is a property of the
/// project graph. These tests are what keep it true.
/// </summary>
public class ArchitectureTests
{
    [Fact]
    public void Ai_project_file_references_only_the_model_project()
    {
        var csproj = File.ReadAllText(RepoFile("src/SqlMigrator.Ai/SqlMigrator.Ai.csproj"));

        Assert.DoesNotContain("Microsoft.Data.SqlClient", csproj, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SqlMigrator.Core", csproj, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SqlMigrator.Model", csproj, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ai_assembly_does_not_load_a_sql_client()
    {
        var referenced = typeof(SqlMigrator.Ai.AiOptions).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name);

        Assert.DoesNotContain("Microsoft.Data.SqlClient", referenced);
        Assert.DoesNotContain("System.Data.SqlClient", referenced);
    }

    [Fact]
    public void Model_project_has_no_database_dependency()
    {
        var csproj = File.ReadAllText(RepoFile("src/SqlMigrator.Model/SqlMigrator.Model.csproj"));

        Assert.DoesNotContain("SqlClient", csproj, StringComparison.OrdinalIgnoreCase);
    }

    internal static string RepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate {relativePath} above {AppContext.BaseDirectory}");
    }
}
