using SqlMigrator.Model.Mapping;

namespace SqlMigrator.Core.Tests;

public class MappingXmlTests
{
    private static MigrationMapping SampleMapping() => new(
        Name: "Demo",
        GeneratedUtc: new DateTimeOffset(2026, 9, 9, 11, 14, 0, TimeSpan.Zero),
        Model: "test-model",
        SourceDatabase: "SqlMigratorDemo_Source",
        SourceReference: "[SqlMigratorDemo_Source]",
        TargetDatabase: "SqlMigratorDemo_Target",
        Tables:
        [
            new TableMapping(
                SourceTable: "dbo.Customer",
                TargetTable: "dbo.Client",
                Columns:
                [
                    new ColumnMapping("ClientId", RuleKind.Copy, "CustomerId", Origin.Ai, 0.99, null),
                    new ColumnMapping("FullName", RuleKind.Concat, "CONCAT(FirstName, ' ', LastName)", Origin.Ai, 0.9, "Two source columns"),
                    new ColumnMapping("ShortName", RuleKind.Truncate, "LEFT(LastName, 20)", Origin.Human, null, null)
                ],
                Unmapped: [new UnmappedColumn("Notes", "No corresponding source column.")],
                Origin: Origin.Ai,
                Confidence: 0.96,
                Reason: "Both hold one row per customer.")
        ]);

    [Fact]
    public void Round_trips_through_xml()
    {
        var original = SampleMapping();

        var restored = MappingXml.Read(MappingXml.Write(original));

        Assert.Equal(original, restored);
    }

    [Fact]
    public void Written_xml_validates_against_the_xsd()
    {
        var xml = MappingXml.Write(SampleMapping());

        var exception = Record.Exception(() => MappingXml.Read(xml));

        Assert.Null(exception);
    }

    [Fact]
    public void Rejects_an_unknown_rule_kind()
    {
        const string xml = """
            <migration name="X" generated="2026-09-09T11:14:00Z">
              <source database="S" reference="[S]" />
              <target database="T" />
              <table source="dbo.A" target="dbo.B" origin="ai">
                <column target="C" rule="teleport" expression="1" origin="ai" />
              </table>
            </migration>
            """;

        var ex = Assert.Throws<MappingXmlException>(() => MappingXml.Read(xml));
        Assert.Contains("teleport", ex.Message);
    }

    [Fact]
    public void Rejects_a_missing_required_attribute()
    {
        const string xml = """
            <migration name="X" generated="2026-09-09T11:14:00Z">
              <source database="S" reference="[S]" />
              <target database="T" />
              <table source="dbo.A" target="dbo.B" origin="ai">
                <column target="C" rule="copy" origin="ai" />
              </table>
            </migration>
            """;

        Assert.Throws<MappingXmlException>(() => MappingXml.Read(xml));
    }
}
