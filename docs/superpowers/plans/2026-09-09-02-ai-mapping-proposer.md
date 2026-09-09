# SQLMigrator AI Mapping Proposer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce a `MigrationMapping` automatically by asking an NVIDIA NIM model to match tables and then map columns, so a human starts from a proposal rather than a blank file.

**Architecture:** A new project, `SqlMigrator.Ai`, containing an OpenAI-compatible HTTP client, two prompts, and a two-pass orchestrator. It depends on the mapping and schema *records* only — never on `Microsoft.Data.SqlClient` — which is what makes "no data row can reach the model" a fact about the project graph rather than a promise. Achieving that requires extracting the records into their own project, which is this plan's first task.

**Tech Stack:** .NET 10, `System.Text.Json`, `HttpClient`, NVIDIA NIM at `https://integrate.api.nvidia.com/v1` (OpenAI-compatible `chat/completions`), xUnit.

**Spec:** `docs/superpowers/specs/2026-09-09-ai-sql-migrator-design.md`

**Depends on:** `docs/superpowers/plans/2026-09-09-01-core-engine.md`, completed.

## Global Constraints

- **`SqlMigrator.Ai` must reference `SqlMigrator.Model` and nothing else from this solution.** No `Microsoft.Data.SqlClient`, no `SqlMigrator.Core`. Task 1 adds a test that fails the build if this is ever violated.
- **The model receives metadata only** — table names, column names, data types, lengths, nullability, key information. Never a data row, a value, or a sample. There is no code path that could supply one, by construction.
- **The API key is read server-side from `NVIDIA_API_KEY`,** or from `appsettings.Local.json`, which is already gitignored. It is never logged, never included in an exception message, and never written to a test fixture.
- **No live API call in the normal test run.** Contract tests use recorded responses. The one live test is skipped unless both `NVIDIA_API_KEY` and `SQLMIGRATOR_AI_LIVE=1` are set.
- **Structured output only.** Every request asks for `response_format: {"type":"json_object"}`. A response that does not parse is retried exactly once, then reported as a failure for that table pair — never guessed at.
- **Schemas of roughly 50 tables per side.** Both summaries go in one prompt for the table-matching pass. No chunking.
- **.NET 10**, nullable enabled, warnings as errors — inherited from `Directory.Build.props`.

---

### Task 1: Extract `SqlMigrator.Model` and create `SqlMigrator.Ai`

The spec (§11) claims `SqlMigrator.Ai` cannot read a data row because it has no reference to `Microsoft.Data.SqlClient`. As Plan 1 leaves things, `Ai` would have to reference `SqlMigrator.Core` to get the `DbSchema` and `MigrationMapping` records — and `Core` references `SqlClient`, so the guarantee would be transitively false. Splitting the records into their own dependency-free project is what makes the claim true.

**Files:**
- Create: `src/SqlMigrator.Model/SqlMigrator.Model.csproj`
- Move: `src/SqlMigrator.Core/Schema/SchemaModels.cs` → `src/SqlMigrator.Model/Schema/SchemaModels.cs`
- Move: `src/SqlMigrator.Core/Mapping/MappingModels.cs` → `src/SqlMigrator.Model/Mapping/MappingModels.cs`
- Move: `src/SqlMigrator.Core/Mapping/MappingXml.cs` → `src/SqlMigrator.Model/Mapping/MappingXml.cs`
- Modify: `src/SqlMigrator.Core/SqlMigrator.Core.csproj` (drop the XSD embed, add a project reference)
- Modify: `src/SqlMigrator.Model/SqlMigrator.Model.csproj` (take over the XSD embed)
- Create: `src/SqlMigrator.Ai/SqlMigrator.Ai.csproj`
- Create: `src/SqlMigrator.Ai.Tests/SqlMigrator.Ai.Tests.csproj`
- Test: `src/SqlMigrator.Ai.Tests/ArchitectureTests.cs`

**Interfaces:**
- Consumes: every type from Plan 1.
- Produces: assembly `SqlMigrator.Model` exporting namespaces `SqlMigrator.Model.Schema` (`ColumnInfo`, `ForeignKeyInfo`, `TableInfo`, `DbSchema`) and `SqlMigrator.Model.Mapping` (`RuleKind`, `Origin`, `ColumnMapping`, `UnmappedColumn`, `TableMapping`, `MigrationMapping`, `MappingXml`, `MappingXmlException`). `SchemaReader` stays in `SqlMigrator.Core.Schema`.

- [ ] **Step 1: Create the two new projects and wire references**

```bash
dotnet new classlib -o src/SqlMigrator.Model -f net10.0
dotnet new classlib -o src/SqlMigrator.Ai -f net10.0
dotnet new xunit    -o src/SqlMigrator.Ai.Tests -f net10.0
rm src/SqlMigrator.Model/Class1.cs src/SqlMigrator.Ai/Class1.cs

dotnet sln add src/SqlMigrator.Model/SqlMigrator.Model.csproj
dotnet sln add src/SqlMigrator.Ai/SqlMigrator.Ai.csproj
dotnet sln add src/SqlMigrator.Ai.Tests/SqlMigrator.Ai.Tests.csproj

dotnet add src/SqlMigrator.Core reference src/SqlMigrator.Model
dotnet add src/SqlMigrator.Ai   reference src/SqlMigrator.Model
dotnet add src/SqlMigrator.Ai.Tests reference src/SqlMigrator.Ai
dotnet add src/SqlMigrator.Ai.Tests reference src/SqlMigrator.Model
```

`SqlMigrator.Ai` gets no reference to `SqlMigrator.Core` and no NuGet package beyond the framework.

- [ ] **Step 2: Move the record files and change their namespaces**

Move the three files listed above. In each, change the namespace:

- `SchemaModels.cs`: `namespace SqlMigrator.Core.Schema;` → `namespace SqlMigrator.Model.Schema;`
- `MappingModels.cs`: `namespace SqlMigrator.Core.Mapping;` → `namespace SqlMigrator.Model.Mapping;`
- `MappingXml.cs`: `namespace SqlMigrator.Core.Mapping;` → `namespace SqlMigrator.Model.Mapping;`

Then move the XSD embed. Remove this `ItemGroup` from `src/SqlMigrator.Core/SqlMigrator.Core.csproj` and add it to `src/SqlMigrator.Model/SqlMigrator.Model.csproj` unchanged:

```xml
  <ItemGroup>
    <EmbeddedResource Include="..\..\mappings\migration.xsd" LogicalName="SqlMigrator.Core.migration.xsd" />
  </ItemGroup>
```

Keep the `LogicalName` exactly as it is — `MappingXml.Validate` looks the resource up by that string, and changing both together is two chances to get it wrong for no benefit.

- [ ] **Step 3: Fix the using statements across the solution**

Add `using SqlMigrator.Model.Schema;` and/or `using SqlMigrator.Model.Mapping;` to every file that referenced the moved types, and remove the now-empty `using SqlMigrator.Core.Mapping;` where nothing else is used from it:

| File | Add |
|---|---|
| `src/SqlMigrator.Core/Schema/SchemaReader.cs` | `using SqlMigrator.Model.Schema;` |
| `src/SqlMigrator.Core/Validation/TypeCompatibility.cs` | `using SqlMigrator.Model.Schema;` (replaces `SqlMigrator.Core.Schema`) |
| `src/SqlMigrator.Core/Validation/MappingValidator.cs` | `using SqlMigrator.Model.Mapping;` and `using SqlMigrator.Model.Schema;` |
| `src/SqlMigrator.Core/Generation/TableOrder.cs` | `using SqlMigrator.Model.Schema;` |
| `src/SqlMigrator.Core/Generation/ScriptGenerator.cs` | `using SqlMigrator.Model.Mapping;` and `using SqlMigrator.Model.Schema;` |
| `src/SqlMigrator.Core.Tests/MappingXmlTests.cs` | `using SqlMigrator.Model.Mapping;` |
| `src/SqlMigrator.Core.Tests/DemoMapping.cs` | `using SqlMigrator.Model.Mapping;` |
| `src/SqlMigrator.Core.Tests/SchemaReaderTests.cs` | keep `using SqlMigrator.Core.Schema;` for `SchemaReader`, add `using SqlMigrator.Model.Schema;` |
| `src/SqlMigrator.Core.Tests/TypeCompatibilityTests.cs` | `using SqlMigrator.Model.Schema;` |
| `src/SqlMigrator.Core.Tests/MappingValidatorTests.cs` | add `using SqlMigrator.Model.Mapping;` and `using SqlMigrator.Model.Schema;` |
| `src/SqlMigrator.Core.Tests/TableOrderTests.cs` | `using SqlMigrator.Model.Schema;` |
| `src/SqlMigrator.Core.Tests/ScriptGeneratorTests.cs` | add `using SqlMigrator.Model.Mapping;` and `using SqlMigrator.Model.Schema;` |
| `src/SqlMigrator.Core.Tests/EndToEndTests.cs` | add `using SqlMigrator.Model.Mapping;` and `using SqlMigrator.Model.Schema;` |

Add `dotnet add src/SqlMigrator.Core.Tests reference src/SqlMigrator.Model` so the test project sees the moved types directly.

- [ ] **Step 4: Write the architecture test**

`src/SqlMigrator.Ai.Tests/ArchitectureTests.cs`:

```csharp
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
```

- [ ] **Step 5: Run to verify the architecture test fails**

Run: `dotnet test src/SqlMigrator.Ai.Tests`
Expected: FAIL — `SqlMigrator.Ai.AiOptions` does not exist yet.

- [ ] **Step 6: Add the placeholder that the test needs**

`src/SqlMigrator.Ai/AiOptions.cs`:

```csharp
namespace SqlMigrator.Ai;

/// <summary>Configuration for the model provider. Populated from environment or appsettings.Local.json.</summary>
public sealed class AiOptions
{
    /// <summary>OpenAI-compatible base URL. Point this at a self-hosted NIM container to keep everything local.</summary>
    public string BaseUrl { get; init; } = "https://integrate.api.nvidia.com/v1";

    public string ApiKey { get; init; } = "";

    /// <summary>Model identifier from the NIM catalogue. Must support JSON-mode output.</summary>
    public string Model { get; init; } = "";

    /// <summary>Proposals at or above this arrive pre-accepted in the review grid.</summary>
    public double ConfidenceThreshold { get; init; } = 0.75;

    public int TimeoutSeconds { get; init; } = 120;
}
```

- [ ] **Step 7: Run the whole suite**

Run: `dotnet test`
Expected: PASS — every Plan 1 test still green after the move, plus 3 architecture tests.

If Plan 1 tests fail here, the cause is a missed `using`, not a behaviour change. Nothing in this task alters logic.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "refactor: extract SqlMigrator.Model so the AI layer cannot depend on SqlClient"
```

---

### Task 2: NIM chat client

**Files:**
- Create: `src/SqlMigrator.Ai/IChatClient.cs`
- Create: `src/SqlMigrator.Ai/NimChatClient.cs`
- Test: `src/SqlMigrator.Ai.Tests/NimChatClientTests.cs`
- Test: `src/SqlMigrator.Ai.Tests/StubHandler.cs`

**Interfaces:**
- Consumes: `AiOptions` from Task 1.
- Produces: `IChatClient.CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken)` returning `Task<string>`; `NimChatClient(HttpClient, AiOptions)`; `AiException`.

- [ ] **Step 1: Write the stub handler and the failing test**

`src/SqlMigrator.Ai.Tests/StubHandler.cs`:

```csharp
using System.Net;

namespace SqlMigrator.Ai.Tests;

/// <summary>Records the request and returns a canned response, so tests never touch the network.</summary>
public sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastBody { get; private set; }
    public int CallCount { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequest = request;
        LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
    }
}
```

`src/SqlMigrator.Ai.Tests/NimChatClientTests.cs`:

```csharp
using System.Net;
using System.Text.Json;

namespace SqlMigrator.Ai.Tests;

public class NimChatClientTests
{
    private const string SuccessBody = """
        {"choices":[{"message":{"role":"assistant","content":"{\"matches\":[]}"}}]}
        """;

    private static (NimChatClient Client, StubHandler Handler) Build(
        HttpStatusCode status = HttpStatusCode.OK, string body = SuccessBody)
    {
        var handler = new StubHandler(status, body);
        var http = new HttpClient(handler);
        var options = new AiOptions
        {
            BaseUrl = "https://example.invalid/v1",
            ApiKey = "nvapi-secret-value",
            Model = "test/model"
        };
        return (new NimChatClient(http, options), handler);
    }

    [Fact]
    public async Task Returns_the_assistant_message_content()
    {
        var (client, _) = Build();

        var content = await client.CompleteJsonAsync("system", "user");

        Assert.Equal("{\"matches\":[]}", content);
    }

    [Fact]
    public async Task Posts_to_chat_completions_with_both_messages_and_json_mode()
    {
        var (client, handler) = Build();

        await client.CompleteJsonAsync("SYSTEM TEXT", "USER TEXT");

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://example.invalid/v1/chat/completions", handler.LastRequest.RequestUri!.ToString());

        using var doc = JsonDocument.Parse(handler.LastBody!);
        var root = doc.RootElement;
        Assert.Equal("test/model", root.GetProperty("model").GetString());
        Assert.Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString());

        var messages = root.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("SYSTEM TEXT", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("USER TEXT", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Sends_the_key_as_a_bearer_token()
    {
        var (client, handler) = Build();

        await client.CompleteJsonAsync("s", "u");

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("nvapi-secret-value", handler.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task An_error_response_throws_without_leaking_the_key()
    {
        var (client, _) = Build(HttpStatusCode.Unauthorized, """{"error":"invalid api key"}""");

        var ex = await Assert.ThrowsAsync<AiException>(() => client.CompleteJsonAsync("s", "u"));

        Assert.Contains("401", ex.Message);
        Assert.DoesNotContain("nvapi-secret-value", ex.Message);
        Assert.DoesNotContain("nvapi-secret-value", ex.ToString());
    }

    [Fact]
    public async Task A_response_with_no_choices_throws()
    {
        var (client, _) = Build(HttpStatusCode.OK, """{"choices":[]}""");

        await Assert.ThrowsAsync<AiException>(() => client.CompleteJsonAsync("s", "u"));
    }
}
```

The key-leak test matters because an exception message is the most common way a secret ends up in a log.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/SqlMigrator.Ai.Tests --filter NimChatClientTests`
Expected: FAIL — `NimChatClient` does not exist.

- [ ] **Step 3: Write the interface**

`src/SqlMigrator.Ai/IChatClient.cs`:

```csharp
namespace SqlMigrator.Ai;

public sealed class AiException(string message) : Exception(message);

/// <summary>A model that answers with a JSON document. Deliberately narrow so tests can fake it.</summary>
public interface IChatClient
{
    Task<string> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);
}
```

- [ ] **Step 4: Implement the client**

`src/SqlMigrator.Ai/NimChatClient.cs`:

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SqlMigrator.Ai;

/// <summary>
/// Talks the OpenAI-compatible chat-completions surface. NVIDIA NIM is the default,
/// but any provider on that surface works by changing BaseUrl.
/// </summary>
public sealed class NimChatClient(HttpClient http, AiOptions options) : IChatClient
{
    public async Task<string> CompleteJsonAsync(
        string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = options.Model,
                temperature = 0.1,
                response_format = new { type = "json_object" },
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user",   content = userPrompt }
                }
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // Deliberately reports the status and a truncated body, never the request headers.
            throw new AiException(
                $"Model provider returned {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body, 400)}");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0)
                throw new AiException("Model provider returned no choices.");

            return choices[0].GetProperty("message").GetProperty("content").GetString()
                   ?? throw new AiException("Model provider returned an empty message.");
        }
        catch (JsonException ex)
        {
            throw new AiException($"Could not read the provider response: {ex.Message}");
        }
        catch (KeyNotFoundException)
        {
            throw new AiException("Provider response did not have the expected chat-completions shape.");
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test src/SqlMigrator.Ai.Tests --filter NimChatClientTests`
Expected: PASS, 5 tests.

- [ ] **Step 6: Commit**

```bash
git add src/SqlMigrator.Ai/IChatClient.cs src/SqlMigrator.Ai/NimChatClient.cs src/SqlMigrator.Ai.Tests
git commit -m "feat: OpenAI-compatible NIM chat client with JSON-mode requests"
```

---

### Task 3: Schema summaries, prompts, and response contracts

**Files:**
- Create: `src/SqlMigrator.Ai/SchemaSummary.cs`
- Create: `src/SqlMigrator.Ai/Contracts.cs`
- Create: `src/SqlMigrator.Ai/Prompts.cs`
- Test: `src/SqlMigrator.Ai.Tests/SchemaSummaryTests.cs`
- Test: `src/SqlMigrator.Ai.Tests/ContractsTests.cs`

**Interfaces:**
- Consumes: `DbSchema`, `TableInfo`, `ColumnInfo` from `SqlMigrator.Model.Schema`.
- Produces: `SchemaSummary.Render(DbSchema)` and `SchemaSummary.RenderTable(TableInfo)` returning `string`; records `TableMatch`, `TableMatchResponse`, `ColumnProposal`, `UnmappedProposal`, `ColumnMapResponse`; `AiJson.Deserialize<T>(string)`; `Prompts.TableMatchSystem`, `Prompts.TableMatchUser(DbSchema, DbSchema)`, `Prompts.ColumnMapSystem`, `Prompts.ColumnMapUser(TableInfo, TableInfo)`.

- [ ] **Step 1: Write the failing tests**

`src/SqlMigrator.Ai.Tests/SchemaSummaryTests.cs`:

```csharp
using SqlMigrator.Model.Schema;

namespace SqlMigrator.Ai.Tests;

public class SchemaSummaryTests
{
    private static DbSchema Schema() => new(
        "DemoDb",
        [
            new TableInfo("dbo", "Customer",
            [
                new ColumnInfo("CustomerId", "int", 4, 10, 0, false, true, false, 1),
                new ColumnInfo("Email", "nvarchar", 200, 0, 0, true, false, false, 2)
            ],
            ["CustomerId"])
        ],
        [new ForeignKeyInfo("FK_Order_Customer", "dbo", "Order", "dbo", "Customer")]);

    [Fact]
    public void Renders_table_column_type_nullability_and_key()
    {
        var text = SchemaSummary.Render(Schema());

        Assert.Contains("dbo.Customer", text);
        Assert.Contains("CustomerId int NOT NULL IDENTITY PK", text);
        Assert.Contains("Email nvarchar(200) NULL", text);
    }

    [Fact]
    public void Renders_foreign_keys_so_the_model_can_see_relationships()
    {
        var text = SchemaSummary.Render(Schema());

        Assert.Contains("dbo.Order -> dbo.Customer", text);
    }

    [Fact]
    public void Renders_max_length_as_MAX()
    {
        var table = new TableInfo("dbo", "T",
            [new ColumnInfo("Body", "nvarchar", -1, 0, 0, true, false, false, 1)], []);

        Assert.Contains("Body nvarchar(MAX) NULL", SchemaSummary.RenderTable(table));
    }

    [Fact]
    public void Renders_decimal_precision_and_scale()
    {
        var table = new TableInfo("dbo", "T",
            [new ColumnInfo("Total", "decimal", 9, 18, 2, false, false, false, 1)], []);

        Assert.Contains("Total decimal(18,2) NOT NULL", SchemaSummary.RenderTable(table));
    }
}
```

`src/SqlMigrator.Ai.Tests/ContractsTests.cs`:

```csharp
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
```

The markdown-fence case is not theoretical: models wrap JSON in fences often enough that handling it here is cheaper than a retry.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test src/SqlMigrator.Ai.Tests --filter "SchemaSummaryTests|ContractsTests"`
Expected: FAIL — `SchemaSummary` and `AiJson` do not exist.

- [ ] **Step 3: Implement the schema summary**

`src/SqlMigrator.Ai/SchemaSummary.cs`:

```csharp
using System.Text;
using SqlMigrator.Model.Schema;

namespace SqlMigrator.Ai;

/// <summary>
/// Renders structural metadata as compact text for a prompt. There is no overload that
/// accepts data — this type only ever sees a DbSchema.
/// </summary>
public static class SchemaSummary
{
    public static string Render(DbSchema schema)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Database: {schema.DatabaseName}");
        sb.AppendLine();

        foreach (var table in schema.Tables)
            sb.AppendLine(RenderTable(table));

        if (schema.ForeignKeys.Count > 0)
        {
            sb.AppendLine("Foreign keys:");
            foreach (var fk in schema.ForeignKeys)
                sb.AppendLine($"  {fk.ParentFullName} -> {fk.ReferencedFullName}");
        }

        return sb.ToString();
    }

    public static string RenderTable(TableInfo table)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"TABLE {table.FullName}");
        foreach (var column in table.Columns)
        {
            var pk = table.PrimaryKeyColumns.Contains(column.Name, StringComparer.OrdinalIgnoreCase) ? " PK" : "";
            var identity = column.IsIdentity ? " IDENTITY" : "";
            var nullability = column.IsNullable ? "NULL" : "NOT NULL";
            var @default = column.HasDefault ? " DEFAULT" : "";
            sb.AppendLine($"  {column.Name} {RenderType(column)} {nullability}{identity}{pk}{@default}");
        }
        return sb.ToString();
    }

    private static string RenderType(ColumnInfo column) => column.DataType.ToLowerInvariant() switch
    {
        "char" or "varchar" or "nchar" or "nvarchar" or "binary" or "varbinary" =>
            $"{column.DataType}({(column.MaxLength == -1 ? "MAX" : column.MaxLength.ToString())})",
        "decimal" or "numeric" => $"{column.DataType}({column.Precision},{column.Scale})",
        _ => column.DataType
    };
}
```

Note the ordering in the expected string: `CustomerId int NOT NULL IDENTITY PK` — nullability, then identity, then key. The test asserts that exact order, so do not reshuffle it.

- [ ] **Step 4: Implement the contracts**

`src/SqlMigrator.Ai/Contracts.cs`:

```csharp
using System.Text.Json;

namespace SqlMigrator.Ai;

public sealed record TableMatch(string SourceTable, string TargetTable, double Confidence, string Reason);

public sealed record TableMatchResponse(List<TableMatch>? Matches)
{
    public List<TableMatch> Matches { get; init; } = Matches ?? [];
}

public sealed record ColumnProposal(
    string TargetColumn, string Rule, string Expression, double Confidence, string Reason);

public sealed record UnmappedProposal(string TargetColumn, string Reason);

public sealed record ColumnMapResponse(List<ColumnProposal>? Columns, List<UnmappedProposal>? Unmapped)
{
    public List<ColumnProposal> Columns { get; init; } = Columns ?? [];
    public List<UnmappedProposal> Unmapped { get; init; } = Unmapped ?? [];
}

public static class AiJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static T Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(StripFence(json), Options)
                   ?? throw new AiException("Model returned a JSON null.");
        }
        catch (JsonException ex)
        {
            throw new AiException($"Model response was not valid JSON: {ex.Message}");
        }
    }

    /// <summary>Removes a ```json fence, which models add often enough to be worth handling.</summary>
    private static string StripFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0) return trimmed;

        var body = trimmed[(firstNewline + 1)..];
        var closing = body.LastIndexOf("```", StringComparison.Ordinal);
        return (closing < 0 ? body : body[..closing]).Trim();
    }
}
```

- [ ] **Step 5: Implement the prompts**

`src/SqlMigrator.Ai/Prompts.cs`:

```csharp
using SqlMigrator.Model.Schema;

namespace SqlMigrator.Ai;

public static class Prompts
{
    public const string TableMatchSystem = """
        You match tables between two SQL Server database schemas for a data migration.

        You are given a SOURCE schema and a TARGET schema. Decide which source table
        supplies the rows for each target table. Each target table is filled from at
        most one source table. A target table with no plausible source is simply left
        out of your answer — do not invent a match.

        Reply with JSON only, in exactly this shape:
        {"matches":[{"sourceTable":"dbo.X","targetTable":"dbo.Y","confidence":0.0,"reason":"one short sentence"}]}

        confidence is between 0 and 1 and expresses how sure you are. Use table names
        exactly as given, including the schema prefix.
        """;

    public static string TableMatchUser(DbSchema source, DbSchema target) => $"""
        SOURCE SCHEMA
        {SchemaSummary.Render(source)}

        TARGET SCHEMA
        {SchemaSummary.Render(target)}
        """;

    public const string ColumnMapSystem = """
        You map columns from one SQL Server source table onto one target table for a
        data migration, and you may use SQL expressions to reshape values.

        For each target column, give a T-SQL expression over the SOURCE table's columns.
        The expression is placed in the SELECT list of:
            INSERT INTO target (...) SELECT <your expressions> FROM source
        so it must be a single expression: no semicolons, no comments, no subqueries
        against other tables, no INSERT/UPDATE/DELETE/EXEC, and no reference to any
        table other than the source table given.

        Classify each expression with one rule:
          copy      — the column as-is
          truncate  — shortened to fit, e.g. LEFT(Name, 50)
          concat    — several source columns combined, e.g. CONCAT(A, ' ', B)
          split     — part of one source column, e.g. SUBSTRING(Email, CHARINDEX('@', Email) + 1, 100)
          case      — a conditional translation, e.g. CASE WHEN A = 1 THEN 1 ELSE 0 END
          constant  — no source column involved, e.g. SYSUTCDATETIME()

        Respect the target column's type and length. If a target column has no sensible
        source, put it in "unmapped" rather than inventing an expression. Identity
        columns on the target should still be mapped when the source has the matching
        key, because keys are preserved.

        Reply with JSON only, in exactly this shape:
        {"columns":[{"targetColumn":"X","rule":"copy","expression":"Y","confidence":0.0,"reason":"one short sentence"}],
         "unmapped":[{"targetColumn":"Z","reason":"one short sentence"}]}
        """;

    public static string ColumnMapUser(TableInfo sourceTable, TableInfo targetTable) => $"""
        SOURCE TABLE
        {SchemaSummary.RenderTable(sourceTable)}

        TARGET TABLE
        {SchemaSummary.RenderTable(targetTable)}
        """;
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test src/SqlMigrator.Ai.Tests --filter "SchemaSummaryTests|ContractsTests"`
Expected: PASS, 9 tests.

- [ ] **Step 7: Commit**

```bash
git add src/SqlMigrator.Ai/SchemaSummary.cs src/SqlMigrator.Ai/Contracts.cs src/SqlMigrator.Ai/Prompts.cs src/SqlMigrator.Ai.Tests/SchemaSummaryTests.cs src/SqlMigrator.Ai.Tests/ContractsTests.cs
git commit -m "feat: schema summaries, prompts and structured-output contracts"
```

---

### Task 4: The two-pass mapping proposer

**Files:**
- Create: `src/SqlMigrator.Ai/MappingProposer.cs`
- Test: `src/SqlMigrator.Ai.Tests/MappingProposerTests.cs`
- Test: `src/SqlMigrator.Ai.Tests/LiveNimSmokeTests.cs`

**Interfaces:**
- Consumes: `IChatClient` (Task 2), `Prompts`, `AiJson`, contracts (Task 3), `DbSchema`/`MigrationMapping` from `SqlMigrator.Model`.
- Produces: `ProposalResult(MigrationMapping Mapping, IReadOnlyList<string> Failures)` and `MappingProposer.ProposeAsync(DbSchema source, DbSchema target, string sourceReference, IProgress<string>?, CancellationToken)` returning `Task<ProposalResult>`. Plan 3's API calls exactly this.

- [ ] **Step 1: Write the failing test**

`src/SqlMigrator.Ai.Tests/MappingProposerTests.cs`:

```csharp
using SqlMigrator.Model.Mapping;
using SqlMigrator.Model.Schema;

namespace SqlMigrator.Ai.Tests;

public class MappingProposerTests
{
    /// <summary>Returns a scripted response per call, so a two-pass run is fully deterministic.</summary>
    private sealed class ScriptedClient(params string[] responses) : IChatClient
    {
        private int _index;
        public int CallCount => _index;
        public List<string> UserPrompts { get; } = [];

        public Task<string> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            UserPrompts.Add(userPrompt);
            var response = responses[Math.Min(_index, responses.Length - 1)];
            _index++;
            return Task.FromResult(response);
        }
    }

    private static DbSchema Source() => new("SrcDb",
        [new TableInfo("dbo", "Customer",
            [
                new ColumnInfo("CustomerId", "int", 4, 10, 0, false, true, false, 1),
                new ColumnInfo("FirstName", "nvarchar", 100, 0, 0, false, false, false, 2),
                new ColumnInfo("LastName", "nvarchar", 100, 0, 0, false, false, false, 3)
            ], ["CustomerId"])],
        []);

    private static DbSchema Target() => new("TgtDb",
        [new TableInfo("dbo", "Client",
            [
                new ColumnInfo("ClientId", "int", 4, 10, 0, false, true, false, 1),
                new ColumnInfo("FullName", "nvarchar", 200, 0, 0, false, false, false, 2),
                new ColumnInfo("Notes", "nvarchar", 500, 0, 0, true, false, false, 3)
            ], ["ClientId"])],
        []);

    private const string MatchResponse = """
        {"matches":[{"sourceTable":"dbo.Customer","targetTable":"dbo.Client","confidence":0.96,"reason":"Both hold customers."}]}
        """;

    private const string ColumnResponse = """
        {"columns":[
          {"targetColumn":"ClientId","rule":"copy","expression":"CustomerId","confidence":0.99,"reason":"Key."},
          {"targetColumn":"FullName","rule":"concat","expression":"CONCAT(FirstName, ' ', LastName)","confidence":0.9,"reason":"Two parts."}],
         "unmapped":[{"targetColumn":"Notes","reason":"No source column."}]}
        """;

    [Fact]
    public async Task Builds_a_mapping_from_two_passes()
    {
        var client = new ScriptedClient(MatchResponse, ColumnResponse);
        var proposer = new MappingProposer(client, new AiOptions { Model = "test/model" });

        var result = await proposer.ProposeAsync(Source(), Target(), "[SrcDb]");

        Assert.Equal(2, client.CallCount);           // one match pass, one column pass
        Assert.Empty(result.Failures);

        var table = Assert.Single(result.Mapping.Tables);
        Assert.Equal("dbo.Customer", table.SourceTable);
        Assert.Equal("dbo.Client", table.TargetTable);
        Assert.Equal(Origin.Ai, table.Origin);
        Assert.Equal(0.96, table.Confidence);

        Assert.Equal(2, table.Columns.Count);
        Assert.Equal(RuleKind.Concat, table.Columns[1].Rule);
        Assert.Equal("CONCAT(FirstName, ' ', LastName)", table.Columns[1].Expression);
        Assert.Equal(Origin.Ai, table.Columns[1].Origin);

        Assert.Equal("Notes", Assert.Single(table.Unmapped).TargetColumn);
        Assert.Equal("test/model", result.Mapping.Model);
        Assert.Equal("[SrcDb]", result.Mapping.SourceReference);
    }

    [Fact]
    public async Task Retries_once_then_records_a_failure_for_that_table_pair()
    {
        var client = new ScriptedClient(MatchResponse, "garbage", "still garbage");
        var proposer = new MappingProposer(client, new AiOptions { Model = "m" });

        var result = await proposer.ProposeAsync(Source(), Target(), "[SrcDb]");

        Assert.Equal(3, client.CallCount);            // match + two column attempts
        Assert.Empty(result.Mapping.Tables);
        Assert.Contains(result.Failures, f => f.Contains("dbo.Client"));
    }

    [Fact]
    public async Task Ignores_a_match_naming_a_table_that_does_not_exist()
    {
        const string bogus = """
            {"matches":[{"sourceTable":"dbo.Nope","targetTable":"dbo.Client","confidence":0.9,"reason":"Hallucinated."}]}
            """;
        var client = new ScriptedClient(bogus);
        var proposer = new MappingProposer(client, new AiOptions { Model = "m" });

        var result = await proposer.ProposeAsync(Source(), Target(), "[SrcDb]");

        Assert.Empty(result.Mapping.Tables);
        Assert.Contains(result.Failures, f => f.Contains("dbo.Nope"));
        Assert.Equal(1, client.CallCount);            // no column pass for a match that cannot exist
    }

    [Fact]
    public async Task Drops_a_proposed_column_that_is_not_on_the_target_table()
    {
        const string extraColumn = """
            {"columns":[
              {"targetColumn":"ClientId","rule":"copy","expression":"CustomerId","confidence":0.9,"reason":"Key."},
              {"targetColumn":"Invented","rule":"copy","expression":"FirstName","confidence":0.9,"reason":"Hallucinated."}]}
            """;
        var client = new ScriptedClient(MatchResponse, extraColumn);
        var proposer = new MappingProposer(client, new AiOptions { Model = "m" });

        var result = await proposer.ProposeAsync(Source(), Target(), "[SrcDb]");

        var table = Assert.Single(result.Mapping.Tables);
        Assert.DoesNotContain(table.Columns, c => c.TargetColumn == "Invented");
        Assert.Contains(result.Failures, f => f.Contains("Invented"));
    }

    [Fact]
    public async Task An_unknown_rule_falls_back_to_copy_and_is_recorded()
    {
        const string weirdRule = """
            {"columns":[{"targetColumn":"ClientId","rule":"teleport","expression":"CustomerId","confidence":0.9,"reason":"?"}]}
            """;
        var client = new ScriptedClient(MatchResponse, weirdRule);
        var proposer = new MappingProposer(client, new AiOptions { Model = "m" });

        var result = await proposer.ProposeAsync(Source(), Target(), "[SrcDb]");

        Assert.Equal(RuleKind.Copy, Assert.Single(result.Mapping.Tables).Columns[0].Rule);
        Assert.Contains(result.Failures, f => f.Contains("teleport"));
    }

    [Fact]
    public async Task Reports_progress_per_table_pair()
    {
        var reported = new List<string>();
        var client = new ScriptedClient(MatchResponse, ColumnResponse);
        var proposer = new MappingProposer(client, new AiOptions { Model = "m" });

        await proposer.ProposeAsync(Source(), Target(), "[SrcDb]",
            new Progress<string>(m => reported.Add(m)));

        // Progress<T> posts asynchronously; give the callbacks a moment to land.
        await Task.Delay(50);
        Assert.Contains(reported, m => m.Contains("dbo.Customer"));
    }
}
```

The last three tests are the important ones: they encode that a hallucinated table, a hallucinated column, and an invented rule are all handled without a crash and without silently entering the mapping.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test src/SqlMigrator.Ai.Tests --filter MappingProposerTests`
Expected: FAIL — `MappingProposer` does not exist.

- [ ] **Step 3: Implement the proposer**

`src/SqlMigrator.Ai/MappingProposer.cs`:

```csharp
using SqlMigrator.Model.Mapping;
using SqlMigrator.Model.Schema;

namespace SqlMigrator.Ai;

/// <summary>A proposal plus everything that went wrong while producing it.</summary>
public sealed record ProposalResult(MigrationMapping Mapping, IReadOnlyList<string> Failures);

/// <summary>
/// Two passes: match tables across both schemas, then map columns for each matched pair.
/// Anything the model names that does not exist is dropped and recorded, never trusted.
/// </summary>
public sealed class MappingProposer(IChatClient client, AiOptions options)
{
    public async Task<ProposalResult> ProposeAsync(
        DbSchema source,
        DbSchema target,
        string sourceReference,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var failures = new List<string>();
        var tables = new List<TableMapping>();

        progress?.Report("Matching tables…");
        var matches = await MatchTables(source, target, failures, ct);

        foreach (var match in matches)
        {
            var sourceTable = source.Find(match.SourceTable);
            var targetTable = target.Find(match.TargetTable);

            if (sourceTable is null || targetTable is null)
            {
                failures.Add($"Model proposed a match involving {match.SourceTable} -> {match.TargetTable}, " +
                             "but at least one of those tables does not exist. Ignored.");
                continue;
            }

            progress?.Report($"Mapping columns for {sourceTable.FullName} -> {targetTable.FullName}…");

            var mapped = await MapColumns(sourceTable, targetTable, failures, ct);
            if (mapped is null) continue;

            tables.Add(mapped with
            {
                SourceTable = sourceTable.FullName,
                TargetTable = targetTable.FullName,
                Origin = Origin.Ai,
                Confidence = match.Confidence,
                Reason = match.Reason
            });
        }

        var mapping = new MigrationMapping(
            Name: $"{source.DatabaseName} to {target.DatabaseName}",
            GeneratedUtc: DateTimeOffset.UtcNow,
            Model: options.Model,
            SourceDatabase: source.DatabaseName,
            SourceReference: sourceReference,
            TargetDatabase: target.DatabaseName,
            Tables: tables);

        return new ProposalResult(mapping, failures);
    }

    private async Task<IReadOnlyList<TableMatch>> MatchTables(
        DbSchema source, DbSchema target, List<string> failures, CancellationToken ct)
    {
        var user = Prompts.TableMatchUser(source, target);

        var response = await CallWithOneRetry(Prompts.TableMatchSystem, user, failures, "table matching", ct);
        if (response is null) return [];

        try
        {
            return AiJson.Deserialize<TableMatchResponse>(response).Matches;
        }
        catch (AiException ex)
        {
            failures.Add($"Table matching returned unusable JSON: {ex.Message}");
            return [];
        }
    }

    private async Task<TableMapping?> MapColumns(
        TableInfo sourceTable, TableInfo targetTable, List<string> failures, CancellationToken ct)
    {
        var user = Prompts.ColumnMapUser(sourceTable, targetTable);
        var label = $"column mapping for {targetTable.FullName}";

        var response = await CallWithOneRetry(Prompts.ColumnMapSystem, user, failures, label, ct);
        if (response is null) return null;

        ColumnMapResponse parsed;
        try
        {
            parsed = AiJson.Deserialize<ColumnMapResponse>(response);
        }
        catch (AiException ex)
        {
            failures.Add($"Gave up on {label}: {ex.Message}");
            return null;
        }

        var columns = new List<ColumnMapping>();
        foreach (var proposal in parsed.Columns)
        {
            if (targetTable.Column(proposal.TargetColumn) is null)
            {
                failures.Add($"Model proposed target column {targetTable.FullName}.{proposal.TargetColumn}, " +
                             "which does not exist. Dropped.");
                continue;
            }

            if (!Enum.TryParse<RuleKind>(proposal.Rule, ignoreCase: true, out var rule))
            {
                failures.Add($"Model used unknown rule '{proposal.Rule}' for " +
                             $"{targetTable.FullName}.{proposal.TargetColumn}. Treated as copy.");
                rule = RuleKind.Copy;
            }

            columns.Add(new ColumnMapping(
                proposal.TargetColumn, rule, proposal.Expression, Origin.Ai,
                proposal.Confidence, proposal.Reason));
        }

        var unmapped = parsed.Unmapped
            .Where(u => targetTable.Column(u.TargetColumn) is not null)
            .Select(u => new UnmappedColumn(u.TargetColumn, u.Reason))
            .ToList();

        return new TableMapping(
            sourceTable.FullName, targetTable.FullName, columns, unmapped, Origin.Ai, null, null);
    }

    /// <summary>One retry, then give up. A model that fails twice is reported, not guessed at.</summary>
    private async Task<string?> CallWithOneRetry(
        string system, string user, List<string> failures, string label, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            string response;
            try
            {
                response = await client.CompleteJsonAsync(system, user, ct);
            }
            catch (AiException ex)
            {
                if (attempt == 2) { failures.Add($"Gave up on {label}: {ex.Message}"); return null; }
                continue;
            }

            if (LooksLikeJson(response)) return response;
            if (attempt == 2) failures.Add($"Gave up on {label}: model did not return JSON.");
        }
        return null;
    }

    private static bool LooksLikeJson(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith("```", StringComparison.Ordinal);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test src/SqlMigrator.Ai.Tests --filter MappingProposerTests`
Expected: PASS, 6 tests.

- [ ] **Step 5: Write the live smoke test**

`src/SqlMigrator.Ai.Tests/LiveNimSmokeTests.cs`:

```csharp
using SqlMigrator.Model.Schema;

namespace SqlMigrator.Ai.Tests;

/// <summary>
/// The only test that talks to NVIDIA. Skipped unless SQLMIGRATOR_AI_LIVE=1 and
/// NVIDIA_API_KEY are both set, so CI never needs a key and never spends money.
/// Run it once when choosing the model — this is the acceptance criterion from spec §6.
/// </summary>
public class LiveNimSmokeTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("SQLMIGRATOR_AI_LIVE") == "1"
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NVIDIA_API_KEY"));

    [SkippableFact]
    public async Task Proposes_a_mapping_for_the_demo_schemas_without_retry()
    {
        Skip.IfNot(Enabled, "Set SQLMIGRATOR_AI_LIVE=1 and NVIDIA_API_KEY to run the live smoke test.");

        var options = new AiOptions
        {
            ApiKey = Environment.GetEnvironmentVariable("NVIDIA_API_KEY")!,
            Model = Environment.GetEnvironmentVariable("SQLMIGRATOR_AI_MODEL")
                    ?? throw new InvalidOperationException("Set SQLMIGRATOR_AI_MODEL to the model under evaluation.")
        };

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds) };
        var proposer = new MappingProposer(new NimChatClient(http, options), options);

        var result = await proposer.ProposeAsync(DemoSchemas.Source(), DemoSchemas.Target(), "[Src]");

        Assert.Empty(result.Failures);
        var table = Assert.Single(result.Mapping.Tables);
        Assert.Equal("dbo.Client", table.TargetTable);
        Assert.Contains(table.Columns, c => c.TargetColumn == "FullName");
    }
}

/// <summary>Small schemas shaped like the real fixtures, without needing a database.</summary>
public static class DemoSchemas
{
    public static DbSchema Source() => new("SrcDb",
        [new TableInfo("dbo", "Customer",
            [
                new ColumnInfo("CustomerId", "int", 4, 10, 0, false, true, false, 1),
                new ColumnInfo("FirstName", "nvarchar", 100, 0, 0, false, false, false, 2),
                new ColumnInfo("LastName", "nvarchar", 100, 0, 0, false, false, false, 3),
                new ColumnInfo("Email", "nvarchar", 200, 0, 0, true, false, false, 4)
            ], ["CustomerId"])],
        []);

    public static DbSchema Target() => new("TgtDb",
        [new TableInfo("dbo", "Client",
            [
                new ColumnInfo("ClientId", "int", 4, 10, 0, false, true, false, 1),
                new ColumnInfo("FullName", "nvarchar", 200, 0, 0, false, false, false, 2),
                new ColumnInfo("EmailAddress", "nvarchar", 200, 0, 0, true, false, false, 3)
            ], ["ClientId"])],
        []);
}
```

`SkippableFact` comes from the `Xunit.SkippableFact` package:

```bash
dotnet add src/SqlMigrator.Ai.Tests package Xunit.SkippableFact
```

If that package does not resolve for the installed xUnit version, replace `[SkippableFact]` with `[Fact]` and make the first line `if (!Enabled) return;` — the test then passes trivially when disabled, which is acceptable for a smoke test.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test`
Expected: PASS. The live test reports as skipped.

- [ ] **Step 7: Commit**

```bash
git add src/SqlMigrator.Ai/MappingProposer.cs src/SqlMigrator.Ai.Tests
git commit -m "feat: two-pass AI mapping proposer that discards what the model invents"
```

---

## Done when

- `dotnet test` passes with no API key present, and the live smoke test reports as skipped.
- The architecture tests fail the build if anyone gives `SqlMigrator.Ai` a database dependency.
- A hallucinated table, a hallucinated column and an invented rule are each proven to be dropped and recorded rather than entering the mapping.
- With `SQLMIGRATOR_AI_LIVE=1`, `NVIDIA_API_KEY` and `SQLMIGRATOR_AI_MODEL` set, the live smoke test passes — which is the spec §6 acceptance criterion for the chosen model.

Plan 3 (`2026-09-09-03-web-app.md`) puts the API and the Angular review screen on top of these two layers.
