using System.Globalization;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace SqlMigrator.Model.Mapping;

public sealed class MappingXmlException(string message) : Exception(message);

public static class MappingXml
{
    public static string Write(MigrationMapping mapping)
    {
        var doc = new XDocument(
            new XElement("migration",
                new XAttribute("name", mapping.Name),
                new XAttribute("generated", mapping.GeneratedUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)),
                mapping.Model is null ? null : new XAttribute("model", mapping.Model),
                new XElement("source",
                    new XAttribute("database", mapping.SourceDatabase),
                    new XAttribute("reference", mapping.SourceReference)),
                new XElement("target",
                    new XAttribute("database", mapping.TargetDatabase)),
                mapping.Tables.Select(WriteTable)));

        return doc.ToString();
    }

    private static XElement WriteTable(TableMapping table) =>
        new("table",
            new XAttribute("source", table.SourceTable),
            new XAttribute("target", table.TargetTable),
            new XAttribute("origin", Lower(table.Origin)),
            table.Confidence is null ? null : new XAttribute("confidence", Num(table.Confidence.Value)),
            table.Reason is null ? null : new XElement("reason", table.Reason),
            table.Columns.Select(c =>
                new XElement("column",
                    new XAttribute("target", c.TargetColumn),
                    new XAttribute("rule", Lower(c.Rule)),
                    new XAttribute("expression", c.Expression),
                    new XAttribute("origin", Lower(c.Origin)),
                    c.Confidence is null ? null : new XAttribute("confidence", Num(c.Confidence.Value)),
                    c.Reason is null ? null : new XAttribute("reason", c.Reason))),
            table.Unmapped.Select(u =>
                new XElement("unmapped",
                    new XAttribute("target", u.TargetColumn),
                    new XAttribute("reason", u.Reason))));

    public static MigrationMapping Read(string xml)
    {
        var doc = Validate(xml);
        var root = doc.Root!;

        return new MigrationMapping(
            Name: Attr(root, "name"),
            GeneratedUtc: DateTimeOffset.Parse(Attr(root, "generated"), CultureInfo.InvariantCulture,
                                               DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            Model: root.Attribute("model")?.Value,
            SourceDatabase: Attr(root.Element("source")!, "database"),
            SourceReference: Attr(root.Element("source")!, "reference"),
            TargetDatabase: Attr(root.Element("target")!, "database"),
            Tables: root.Elements("table").Select(ReadTable).ToList());
    }

    private static TableMapping ReadTable(XElement table) => new(
        SourceTable: Attr(table, "source"),
        TargetTable: Attr(table, "target"),
        Columns: table.Elements("column").Select(c => new ColumnMapping(
            TargetColumn: Attr(c, "target"),
            Rule: Enum.Parse<RuleKind>(Attr(c, "rule"), ignoreCase: true),
            Expression: Attr(c, "expression"),
            Origin: Enum.Parse<Origin>(Attr(c, "origin"), ignoreCase: true),
            Confidence: ParseNum(c.Attribute("confidence")?.Value),
            Reason: c.Attribute("reason")?.Value)).ToList(),
        Unmapped: table.Elements("unmapped").Select(u => new UnmappedColumn(
            Attr(u, "target"), Attr(u, "reason"))).ToList(),
        Origin: Enum.Parse<Origin>(Attr(table, "origin"), ignoreCase: true),
        Confidence: ParseNum(table.Attribute("confidence")?.Value),
        Reason: table.Element("reason")?.Value);

    private static XDocument Validate(string xml)
    {
        var schemas = new XmlSchemaSet();
        using var stream = typeof(MappingXml).Assembly
            .GetManifestResourceStream("SqlMigrator.Core.migration.xsd")
            ?? throw new InvalidOperationException("Embedded migration.xsd is missing.");
        schemas.Add(null, XmlReader.Create(stream));

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (XmlException ex) { throw new MappingXmlException($"Mapping XML is not well formed: {ex.Message}"); }

        var errors = new List<string>();
        doc.Validate(schemas, (_, e) => errors.Add(e.Message));

        if (errors.Count > 0)
            throw new MappingXmlException("Mapping XML failed schema validation: " + string.Join("; ", errors));

        return doc;
    }

    private static string Attr(XElement element, string name) =>
        element.Attribute(name)?.Value
        ?? throw new MappingXmlException($"<{element.Name}> is missing required attribute '{name}'.");

    private static string Lower<T>(T value) where T : struct, Enum =>
        value.ToString().ToLowerInvariant();

    private static string Num(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static double? ParseNum(string? value) =>
        value is null ? null : double.Parse(value, CultureInfo.InvariantCulture);
}
