using SqlMigrator.Core.Generation;
using SqlMigrator.Core.Schema;
using SqlMigrator.Model.Mapping;
using SqlMigrator.Model.Schema;

namespace SqlMigrator.Core.Tests;

[Collection("sql")]
public class ScriptGeneratorTests
{
    private static ScriptOptions Options() => new(
        SourceServer: "localhost",
        TargetServer: "localhost",
        GeneratedUtc: new DateTimeOffset(2026, 9, 9, 11, 14, 0, TimeSpan.Zero),
        MappingFileName: "mappings/demo.xml",
        MappingSha256: "3f9a0000");

    private static MigrationMapping DemoMigrationMapping() => new(
        "Demo", new DateTimeOffset(2026, 9, 9, 11, 14, 0, TimeSpan.Zero), "test-model",
        TestDatabases.SourceDb, $"[{TestDatabases.SourceDb}]", TestDatabases.TargetDb,
        [DemoMapping.OrderToOrder(), DemoMapping.CustomerToClient(), DemoMapping.OrderLineToOrderLine()]);

    [Fact]
    public async Task Emits_parents_before_children_regardless_of_mapping_order()
    {
        TestDatabases.EnsureCreated();
        var target = await SchemaReader.ReadAsync(TestDatabases.TargetConnectionString);

        var sql = ScriptGenerator.Generate(DemoMigrationMapping(), target, Options());

        var client = sql.IndexOf("INSERT INTO [dbo].[Client]", StringComparison.Ordinal);
        var order = sql.IndexOf("INSERT INTO [dbo].[Order]", StringComparison.Ordinal);
        var line = sql.IndexOf("INSERT INTO [dbo].[OrderLine]", StringComparison.Ordinal);

        Assert.True(client < order, "Client must be inserted before Order");
        Assert.True(order < line, "Order must be inserted before OrderLine");
    }

    [Fact]
    public async Task Wraps_identity_tables_and_guards_emptiness()
    {
        TestDatabases.EnsureCreated();
        var target = await SchemaReader.ReadAsync(TestDatabases.TargetConnectionString);

        var sql = ScriptGenerator.Generate(DemoMigrationMapping(), target, Options());

        Assert.Contains("SET XACT_ABORT ON;", sql);
        Assert.Contains("BEGIN TRANSACTION;", sql);
        Assert.Contains("COMMIT;", sql);
        Assert.Contains("IF EXISTS (SELECT 1 FROM [dbo].[Client])", sql);
        Assert.Contains("SET IDENTITY_INSERT [dbo].[Client] ON;", sql);
        Assert.Contains("SET IDENTITY_INSERT [dbo].[Client] OFF;", sql);

        // Order and OrderLine are identity tables too — the guard and IDENTITY_INSERT wrap
        // must not be a Client-only accident.
        Assert.Contains("IF EXISTS (SELECT 1 FROM [dbo].[Order])", sql);
        Assert.Contains("SET IDENTITY_INSERT [dbo].[Order] ON;", sql);
        Assert.Contains("SET IDENTITY_INSERT [dbo].[Order] OFF;", sql);

        Assert.Contains("IF EXISTS (SELECT 1 FROM [dbo].[OrderLine])", sql);
        Assert.Contains("SET IDENTITY_INSERT [dbo].[OrderLine] ON;", sql);
        Assert.Contains("SET IDENTITY_INSERT [dbo].[OrderLine] OFF;", sql);
    }

    [Fact]
    public async Task Leaves_identity_insert_off_on_the_failure_path_too()
    {
        // IDENTITY_INSERT is session-level state that XACT_ABORT's rollback does not reset, so
        // the OFF for an identity table must appear on both the success and the failure path.
        TestDatabases.EnsureCreated();
        var target = await SchemaReader.ReadAsync(TestDatabases.TargetConnectionString);

        var sql = ScriptGenerator.Generate(DemoMigrationMapping(), target, Options());

        Assert.Contains("BEGIN TRY", sql);
        Assert.Contains("BEGIN CATCH", sql);
        // Exactly two OFFs per identity table: one on the TRY path, one on the CATCH path.
        Assert.Equal(2, CountOccurrences(sql, "SET IDENTITY_INSERT [dbo].[Client] OFF;"));
        // The original error must still surface — not swallowed by the cleanup.
        Assert.Contains("THROW;", sql);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    [Fact]
    public async Task Emits_expressions_verbatim_and_a_validated_source_reference()
    {
        // "Verbatim" applies to the expression only. The source reference is not passed
        // through untouched — it is validated and re-quoted by SqlIdentifier.QuoteSourceReference
        // (see the Source_reference_* tests below) — this well-formed, already-bracketed value
        // just happens to re-quote to the same text.
        TestDatabases.EnsureCreated();
        var target = await SchemaReader.ReadAsync(TestDatabases.TargetConnectionString);

        var sql = ScriptGenerator.Generate(DemoMigrationMapping(), target, Options());

        Assert.Contains("LEFT(CONCAT(FirstName, ' ', LastName), 200)", sql);
        Assert.Contains($"FROM [{TestDatabases.SourceDb}].[dbo].[Customer]", sql);
        Assert.Contains("-- Source: localhost . SqlMigratorDemo_Source", sql);
        Assert.Contains("3f9a0000", sql);
    }

    [Fact]
    public async Task Never_emits_a_credential()
    {
        TestDatabases.EnsureCreated();
        var target = await SchemaReader.ReadAsync(TestDatabases.TargetConnectionString);

        var sql = ScriptGenerator.Generate(DemoMigrationMapping(), target, Options());

        Assert.DoesNotContain("Password", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Integrated Security", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Refuses_to_generate_when_the_foreign_keys_form_a_cycle()
    {
        TestDatabases.EnsureCreated();
        var target = await SchemaReader.ReadAsync(TestDatabases.TargetConnectionString);
        var cyclic = new DbSchema(target.DatabaseName, target.Tables,
        [
            new ForeignKeyInfo("FK_Client_Order", "dbo", "Client", "dbo", "Order"),
            new ForeignKeyInfo("FK_Order_Client", "dbo", "Order", "dbo", "Client")
        ]);

        var ex = Assert.Throws<ScriptGenerationException>(
            () => ScriptGenerator.Generate(DemoMigrationMapping(), cyclic, Options()));

        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // --- Injection-hardening tests -------------------------------------------------------
    // These build the schema and mapping in memory, so they need no database and run fast.
    // Table/column names come from a schema; expressions come from a language model. Both
    // flow unchecked into the emitted script, so every identifier and every value embedded in
    // a string literal must be escaped, however hostile the input.

    private static DbSchema SingleTableSchema(string schemaName, string tableName, string columnName, bool identity) =>
        new(
            "TestDb",
            [
                new TableInfo(
                    schemaName,
                    tableName,
                    [new ColumnInfo(columnName, "int", 0, 0, 0, IsNullable: false, IsIdentity: identity, HasDefault: false, OrdinalPosition: 1)],
                    [columnName])
            ],
            []);

    private static MigrationMapping SingleTableMapping(
        string sourceTable, string targetTable, string sourceColumn, string targetColumn,
        string sourceReference = "[SourceDb]") =>
        new(
            "Injection", new DateTimeOffset(2026, 9, 9, 11, 14, 0, TimeSpan.Zero), "test-model",
            "SourceDb", sourceReference, "TestDb",
            [new TableMapping(
                sourceTable, targetTable,
                [new ColumnMapping(targetColumn, RuleKind.Copy, sourceColumn, Origin.Ai, 0.99)],
                [], Origin.Ai, 0.99, "test")]);

    [Fact]
    public void Escapes_an_embedded_closing_bracket_in_every_identifier_site()
    {
        var schema = SingleTableSchema("dbo", "Ord]er", "Col]A", identity: true);
        var mapping = SingleTableMapping("dbo.Src]Tbl", "dbo.Ord]er", "SrcCol", "Col]A");

        var sql = ScriptGenerator.Generate(mapping, schema, Options());

        // A lone ']' would close the bracketed identifier early; T-SQL escapes it by doubling.
        Assert.Contains("IF EXISTS (SELECT 1 FROM [dbo].[Ord]]er])", sql);
        Assert.Contains("INSERT INTO [dbo].[Ord]]er] ([Col]]A])", sql);
        Assert.Contains("FROM [SourceDb].[dbo].[Src]]Tbl];", sql);
        Assert.Contains("SET IDENTITY_INSERT [dbo].[Ord]]er] ON;", sql);
        Assert.Contains("SET IDENTITY_INSERT [dbo].[Ord]]er] OFF;", sql);

        // The naive (unescaped) forms must never appear — that's the injection this guards against.
        Assert.DoesNotContain("[dbo].[Ord]er]", sql);
        Assert.DoesNotContain("[Col]A]", sql);
    }

    [Fact]
    public void Escapes_an_embedded_single_quote_in_string_literals()
    {
        var schema = SingleTableSchema("dbo", "O'Brien", "Col", identity: false);
        var mapping = SingleTableMapping("dbo.Src", "dbo.O'Brien", "SrcCol", "Col");

        var sql = ScriptGenerator.Generate(mapping, schema, Options());

        // A lone ''' would close the string literal early; T-SQL escapes it by doubling.
        Assert.Contains("THROW 50001, 'Target table dbo.O''Brien is not empty", sql);
        Assert.Contains("PRINT CONCAT('dbo.O''Brien: ', @@ROWCOUNT, ' rows');", sql);
        Assert.DoesNotContain("Target table dbo.O'Brien is not empty", sql);
    }

    [Fact]
    public void Comment_markers_and_newlines_stay_inertly_inside_the_brackets_that_contain_them()
    {
        var schema = SingleTableSchema("dbo", "Foo--Bar", "Baz\nQux", identity: false);
        var mapping = SingleTableMapping("dbo.Src", "dbo.Foo--Bar", "SrcCol", "Baz\nQux");

        var sql = ScriptGenerator.Generate(mapping, schema, Options());

        // '--' and a raw newline are ordinary characters inside a bracket-quoted identifier;
        // they only become dangerous if the quoting is broken, so a single intact bracket pair
        // around each name is the assertion that matters.
        Assert.Contains("[dbo].[Foo--Bar]", sql);
        Assert.Contains("[Baz\nQux]", sql);
    }

    [Fact]
    public void Escapes_the_classic_bracket_breakout_payload()
    {
        // The exact shape proven exploitable against a sibling generator: an interior ']'
        // followed by a second statement and a line comment to swallow the rest of the batch.
        const string payload = "Customer]; SELECT 1 AS Pwn --";
        var schema = SingleTableSchema("dbo", payload, "Col", identity: false);
        var mapping = SingleTableMapping("dbo.Src", $"dbo.{payload}", "SrcCol", "Col");

        var sql = ScriptGenerator.Generate(mapping, schema, Options());

        Assert.Contains($"[dbo].[{payload.Replace("]", "]]")}]", sql);
        // The naive (Trim-only) quoting this replaces would close the bracket right after
        // "Customer", leaving "; SELECT 1 AS Pwn --]" as live, uncommented script text.
        Assert.DoesNotContain("[dbo].[Customer]; SELECT 1 AS Pwn --]", sql);
    }

    // --- SourceReference: the fourth injection --------------------------------------------
    // Unlike a table/column identifier, SourceReference is not free text — spec §2.5 fixes
    // its shape as one or two bracket-quoted parts (a same-instance database, or
    // [LinkedServer].[Database]). It used to be interpolated straight into the emitted FROM
    // clause with no quoting, escaping or validation at any hop from the browser: the Angular
    // UI auto-fills it from the source database name, so a database literally named
    // "X]; DROP TABLE dbo.Invoices --" produced live DDL in a script the tool had just
    // certified as validated. The fix rejects anything that is not exactly the legitimate
    // shape, rather than trying to sanitise it.

    [Theory]
    [InlineData("[SqlMigratorDemo_Source]")]
    [InlineData("[LINKEDSRV].[SqlMigratorDemo_Source]")]
    public void Legitimate_source_reference_forms_are_emitted_quoted(string sourceReference)
    {
        var schema = SingleTableSchema("dbo", "Tbl", "Col", identity: false);
        var mapping = SingleTableMapping("dbo.Src", "dbo.Tbl", "SrcCol", "Col", sourceReference);

        var sql = ScriptGenerator.Generate(mapping, schema, Options());

        Assert.Contains($"FROM {sourceReference}.[dbo].[Src];", sql);
    }

    [Fact]
    public void SourceReference_containing_a_closing_bracket_is_refused_before_any_sql_is_built()
    {
        // The exact C1 payload: an interior ']' followed by a second statement, closed with a
        // trailing ']' so it still looks superficially well-formed.
        var schema = SingleTableSchema("dbo", "Tbl", "Col", identity: false);
        var mapping = SingleTableMapping("dbo.Src", "dbo.Tbl", "SrcCol", "Col",
            sourceReference: "[X]; DROP TABLE dbo.Invoices --]");

        var ex = Assert.Throws<ScriptGenerationException>(
            () => ScriptGenerator.Generate(mapping, schema, Options()));
        Assert.Contains("source reference", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SourceReference_containing_an_unbracketed_single_quote_is_refused()
    {
        var schema = SingleTableSchema("dbo", "Tbl", "Col", identity: false);
        var mapping = SingleTableMapping("dbo.Src", "dbo.Tbl", "SrcCol", "Col",
            sourceReference: "O'Brien'; DROP TABLE dbo.Invoices --");

        Assert.Throws<ScriptGenerationException>(
            () => ScriptGenerator.Generate(mapping, schema, Options()));
    }

    [Fact]
    public void SourceReference_with_a_trailing_drop_table_statement_is_refused()
    {
        // A well-formed bracket-quoted part is not enough on its own: trailing text after the
        // closing bracket must also be refused, not silently dropped or accepted.
        var schema = SingleTableSchema("dbo", "Tbl", "Col", identity: false);
        var mapping = SingleTableMapping("dbo.Src", "dbo.Tbl", "SrcCol", "Col",
            sourceReference: "[SqlMigratorDemo_Source]; DROP TABLE dbo.Invoices --");

        Assert.Throws<ScriptGenerationException>(
            () => ScriptGenerator.Generate(mapping, schema, Options()));
    }

    // --- Script header comments: the fifth injection (C2) ---------------------------------
    // SourceDatabase, TargetDatabase and Model are free text — SourceDatabase/TargetDatabase
    // can come straight off the request body or off a live server's database name, and Model
    // off a language model's self-reported name — and used to be interpolated raw into "--"
    // header comment lines. A "--" comment ends at the first CR or LF, so a value containing
    // either one could close the comment and let the rest of its own text run as a live
    // statement, above BEGIN TRANSACTION, before the script's own safety net even starts. The
    // fix strips CR/LF via SqlComment.Sanitize; these tests assert on the emitted text that a
    // payload can no longer start a new line, let alone one above BEGIN TRANSACTION.

    private static void AssertNoLiveLineAboveTransaction(string sql)
    {
        var beginTransaction = sql.IndexOf("BEGIN TRANSACTION;", StringComparison.Ordinal);
        Assert.True(beginTransaction > 0, "Script must contain BEGIN TRANSACTION;");

        foreach (var line in sql[..beginTransaction].Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimEnd('\r');
            Assert.True(
                trimmed.Length == 0 || trimmed.StartsWith("--", StringComparison.Ordinal)
                    || trimmed is "SET XACT_ABORT ON;",
                $"Line above BEGIN TRANSACTION must be blank, a comment, or the XACT_ABORT setup — was: {trimmed}");
        }
    }

    [Theory]
    [InlineData("SqlMigratorDemo_Source\nDROP TABLE dbo.Invoices;--")]
    [InlineData("SqlMigratorDemo_Source\r\nDROP TABLE dbo.Invoices;--")]
    [InlineData("SqlMigratorDemo_Source\nDROP TABLE dbo.Invoices;--\n")]
    public void A_newline_in_SourceDatabase_cannot_start_a_line_above_the_transaction(string payload)
    {
        var schema = SingleTableSchema("dbo", "Tbl", "Col", identity: false);
        var mapping = new MigrationMapping(
            "Injection", new DateTimeOffset(2026, 9, 9, 11, 14, 0, TimeSpan.Zero), "test-model",
            payload, "[SourceDb]", "TestDb",
            [new TableMapping(
                "dbo.Src", "dbo.Tbl",
                [new ColumnMapping("Col", RuleKind.Copy, "SrcCol", Origin.Ai, 0.99)],
                [], Origin.Ai, 0.99, "test")]);

        var sql = ScriptGenerator.Generate(mapping, schema, Options());

        Assert.DoesNotContain("\nDROP TABLE", sql);
        AssertNoLiveLineAboveTransaction(sql);
    }

    [Theory]
    [InlineData("TestDb\nDROP TABLE dbo.Invoices;--")]
    [InlineData("TestDb\r\nDROP TABLE dbo.Invoices;--")]
    public void A_newline_in_TargetDatabase_cannot_start_a_line_above_the_transaction(string payload)
    {
        var schema = SingleTableSchema("dbo", "Tbl", "Col", identity: false);
        var mapping = new MigrationMapping(
            "Injection", new DateTimeOffset(2026, 9, 9, 11, 14, 0, TimeSpan.Zero), "test-model",
            "SourceDb", "[SourceDb]", payload,
            [new TableMapping(
                "dbo.Src", "dbo.Tbl",
                [new ColumnMapping("Col", RuleKind.Copy, "SrcCol", Origin.Ai, 0.99)],
                [], Origin.Ai, 0.99, "test")]);

        var sql = ScriptGenerator.Generate(mapping, schema, Options());

        Assert.DoesNotContain("\nDROP TABLE", sql);
        AssertNoLiveLineAboveTransaction(sql);
    }

    [Theory]
    [InlineData("test-model\nDROP TABLE dbo.Invoices;--")]
    [InlineData("test-model\r\nDROP TABLE dbo.Invoices;--")]
    public void A_newline_in_Model_cannot_start_a_line_above_the_transaction(string payload)
    {
        var schema = SingleTableSchema("dbo", "Tbl", "Col", identity: false);
        var mapping = new MigrationMapping(
            "Injection", new DateTimeOffset(2026, 9, 9, 11, 14, 0, TimeSpan.Zero), payload,
            "SourceDb", "[SourceDb]", "TestDb",
            [new TableMapping(
                "dbo.Src", "dbo.Tbl",
                [new ColumnMapping("Col", RuleKind.Copy, "SrcCol", Origin.Ai, 0.99)],
                [], Origin.Ai, 0.99, "test")]);

        var sql = ScriptGenerator.Generate(mapping, schema, Options());

        Assert.DoesNotContain("\nDROP TABLE", sql);
        Assert.Contains("-- Mapping proposed by: test-modelDROP TABLE dbo.Invoices;--", sql);
        AssertNoLiveLineAboveTransaction(sql);
    }
}
