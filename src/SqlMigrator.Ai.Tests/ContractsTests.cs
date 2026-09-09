namespace SqlMigrator.Ai.Tests;

public class ContractsTests
{
    [Fact]
    public void Parses_a_table_match_response()
    {
        const string json = """
            {"matches":[{"sourceTable":"dbo.Customer","targetTable":"dbo.Client","confidence":0.96,"reason":"Both hold customers."}]}
            """;

        var parsed = AiJson.Deserialize<TableMatchResponse>(json);

        var match = Assert.Single(parsed.Matches);
        Assert.Equal("dbo.Customer", match.SourceTable);
        Assert.Equal(0.96, match.Confidence);
    }

    [Fact]
    public void Parses_a_column_map_response_including_unmapped()
    {
        const string json = """
            {"columns":[{"targetColumn":"FullName","rule":"concat","expression":"CONCAT(FirstName, ' ', LastName)","confidence":0.9,"reason":"Two parts."}],
             "unmapped":[{"targetColumn":"Notes","reason":"No source column."}]}
            """;

        var parsed = AiJson.Deserialize<ColumnMapResponse>(json);

        Assert.Equal("concat", Assert.Single(parsed.Columns).Rule);
        Assert.Equal("Notes", Assert.Single(parsed.Unmapped).TargetColumn);
    }

    [Fact]
    public void Tolerates_missing_optional_arrays()
    {
        var parsed = AiJson.Deserialize<ColumnMapResponse>("""{"columns":[]}""");

        Assert.Empty(parsed.Columns);
        Assert.Empty(parsed.Unmapped);
    }

    [Fact]
    public void Strips_a_markdown_fence_some_models_add_despite_json_mode()
    {
        const string json = "```json\n{\"matches\":[]}\n```";

        var parsed = AiJson.Deserialize<TableMatchResponse>(json);

        Assert.Empty(parsed.Matches);
    }

    [Fact]
    public void Malformed_json_throws_AiException()
    {
        Assert.Throws<AiException>(() => AiJson.Deserialize<TableMatchResponse>("not json at all"));
    }
}
