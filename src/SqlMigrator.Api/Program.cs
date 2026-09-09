using Microsoft.Data.SqlClient;
using SqlMigrator.Ai;
using SqlMigrator.Api;
using SqlMigrator.Core.Generation;
using SqlMigrator.Core.Validation;
using SqlMigrator.Model.Mapping;

var builder = WebApplication.CreateBuilder(args);

// Localhost only. Never bind a wildcard host: this process can reach two databases.
builder.WebHost.UseUrls("http://127.0.0.1:5199");
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true);

builder.Services.AddSingleton<SessionStore>();
builder.Services.AddHttpClient();

builder.Services.AddSingleton(_ => new AiOptions
{
    BaseUrl = builder.Configuration["Ai:BaseUrl"] ?? "https://integrate.api.nvidia.com/v1",
    // The key is server-side only: environment variable first, then the gitignored local
    // settings file. Never sourced from a request, never returned by an endpoint or logged.
    ApiKey = Environment.GetEnvironmentVariable("NVIDIA_API_KEY")
             ?? builder.Configuration["Ai:ApiKey"] ?? "",
    Model = builder.Configuration["Ai:Model"] ?? "",
    ConfidenceThreshold = double.TryParse(builder.Configuration["Ai:ConfidenceThreshold"], out var t) ? t : 0.75,
    TimeoutSeconds = int.TryParse(builder.Configuration["Ai:TimeoutSeconds"], out var timeout) ? timeout : 120,
    ColumnBatchSize = int.TryParse(builder.Configuration["Ai:ColumnBatchSize"], out var batch) ? batch : 5
});

builder.Services.AddSingleton<IChatClient>(sp => new NimChatClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
    sp.GetRequiredService<AiOptions>()));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/connections/test", async (ConnectionRequest request, CancellationToken ct) =>
{
    try
    {
        await using var conn = new SqlConnection(ConnectionStrings.For(request.Server, request.Database));
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(
            "SELECT LEFT(@@VERSION, 60), (SELECT COUNT(*) FROM sys.tables)", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        return Results.Ok(new ConnectionTestResponse(true, reader.GetString(0), reader.GetInt32(1), null));
    }
    catch (ArgumentException ex)
    {
        // Rejected by validation before any connection was attempted. The message names the
        // offending parameter only — never the value the caller sent.
        return Results.BadRequest(new ConnectionTestResponse(false, null, 0, ex.Message));
    }
    catch (SqlException ex)
    {
        // The message comes from the server and contains no credential, because none was sent.
        return Results.Ok(new ConnectionTestResponse(false, null, 0, ex.Message));
    }
});

app.MapPost("/api/analyse", (
    AnalyseRequest request,
    SessionStore store,
    IChatClient chatClient,
    AiOptions aiOptions) =>
{
    var session = store.Start(new Session
    {
        Id = Guid.NewGuid().ToString("n"),
        SourceConnectionString = ConnectionStrings.For(request.Source.Server, request.Source.Database),
        TargetConnectionString = ConnectionStrings.For(request.Target.Server, request.Target.Database),
        SourceServer = request.Source.Server,
        TargetServer = request.Target.Server,
        SourceReference = request.SourceReference
    });

    // Fire and forget: the browser polls GET /api/analyse/{id} for progress. AnalysisRunner
    // catches everything it can throw and always publishes a terminal state, so this task is
    // never awaited or observed here. It is cancelled, though: the token belongs to the session,
    // and store.Start fires it the moment a newer analysis replaces this one, so an abandoned
    // run stops calling the model instead of finishing for nobody.
    _ = Task.Run(() => AnalysisRunner.RunAsync(session, chatClient, aiOptions, session.RunCancellation));

    return Results.Ok(new AnalyseStarted(session.Id));
});

app.MapGet("/api/analyse/{id}", (string id, SessionStore store, AiOptions aiOptions) =>
{
    var session = store.Get(id);
    if (session is null) return Results.NotFound();

    // State/Step/Mapping/Error come from one atomic snapshot — see Session.Result — so this
    // can never report "ready" with a null Mapping or a stale Step from a torn read.
    var result = session.Result;
    return Results.Ok(new SessionStatus(
        result.State, result.Step, result.Mapping,
        session.Issues.ToList(), session.Failures.ToList(), session.UnmatchedSourceTables.ToList(),
        result.Error, aiOptions.ConfidenceThreshold));
});

app.MapPost("/api/expression/validate", async (
    ValidateExpressionRequest request, SessionStore store, CancellationToken ct) =>
{
    var session = store.Get(request.SessionId);
    if (session?.SourceSchema is null || session.TargetSchema is null) return Results.NotFound();

    var issues = new List<IssueDto>();

    // Gate 1: the textual screen. A human just edited this expression, which makes it the most
    // likely place someone pastes something dangerous — it gets no exemption from any gate.
    var screen = ExpressionScreen.Screen(request.Expression);
    if (!screen.Ok)
    {
        issues.Add(new IssueDto("EXP001", "Blocking", screen.Reason!, request.TargetTable, request.TargetColumn));
        return Results.Ok(new ValidateExpressionResponse(false, null, issues));
    }

    // Gate 2: a real compile via SELECT TOP 0 under SchemaOnly. Unlike the mapping-driven paths,
    // SourceTable here comes straight from the caller rather than a name already confirmed to
    // exist in a schema we just read, so SqlExpressionCompiler's ArgumentException for a malformed
    // table name is reachable and is turned into a validation issue rather than a 500.
    // OperationCanceledException and genuine connection failures are left to propagate: an
    // unreachable database is a server-side problem, not "your expression is invalid".
    var compiler = new SqlExpressionCompiler(session.SourceConnectionString);
    CompileResult compiled;
    try
    {
        compiled = await compiler.CompileAsync(request.SourceTable, request.Expression, ct);
    }
    catch (ArgumentException ex)
    {
        issues.Add(new IssueDto("EXP003", "Blocking", $"Invalid source table: {ex.Message}",
            request.TargetTable, request.TargetColumn));
        return Results.Ok(new ValidateExpressionResponse(false, null, issues));
    }

    if (!compiled.Ok)
    {
        issues.Add(new IssueDto("EXP002", "Blocking",
            $"Expression did not compile: {compiled.Error}", request.TargetTable, request.TargetColumn));
        return Results.Ok(new ValidateExpressionResponse(false, null, issues));
    }

    var targetColumn = session.TargetSchema.Find(request.TargetTable)?.Column(request.TargetColumn);
    if (targetColumn is null)
    {
        issues.Add(new IssueDto("MAP003", "Blocking",
            $"Target column {request.TargetColumn} does not exist on {request.TargetTable}.",
            request.TargetTable, request.TargetColumn));
        return Results.Ok(new ValidateExpressionResponse(false, null, issues));
    }

    // Gate 3: the type check.
    var check = TypeCompatibility.Check(compiled.ResultType!, targetColumn);
    if (!check.Compatible)
        issues.Add(new IssueDto("TYP001", "Blocking", check.Message!, request.TargetTable, request.TargetColumn));
    else if (check.Narrowing)
        issues.Add(new IssueDto("TYP002", "Warning", check.Message!, request.TargetTable, request.TargetColumn));

    var typeName = $"{compiled.ResultType!.DataType}" +
                   (compiled.ResultType.MaxLength > 0 ? $"({compiled.ResultType.MaxLength})" : "");

    return Results.Ok(new ValidateExpressionResponse(check.Compatible, typeName, issues));
});

app.MapPost("/api/mapping/save", (SaveMappingRequest request, SessionStore store) =>
{
    var session = store.Get(request.SessionId);
    if (session is null) return Results.NotFound();

    var mapping = DtoMapper.ToDomain(request.Mapping, DateTimeOffset.UtcNow);
    var xml = MappingXml.Write(mapping);

    var directory = Path.Combine(AppContext.BaseDirectory, "mappings");
    Directory.CreateDirectory(directory);

    string path;
    try
    {
        // TargetDatabase arrives on the request body and is untrusted: it must not be able to
        // steer where this file gets written. See MappingFileNames for the validation.
        path = MappingFileNames.BuildPath(directory, mapping.TargetDatabase, DateTimeOffset.UtcNow);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    File.WriteAllText(path, xml);

    var sha = Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(xml)));

    return Results.Ok(new SaveMappingResponse(path, sha));
});

app.MapPost("/api/script/generate", async (
    GenerateScriptRequest request, SessionStore store, CancellationToken ct) =>
{
    var session = store.Get(request.SessionId);
    if (session?.SourceSchema is null || session.TargetSchema is null) return Results.NotFound();

    var mapping = DtoMapper.ToDomain(request.Mapping, DateTimeOffset.UtcNow);

    // The human-approved mapping is re-proven from scratch — the same validator, the same three
    // gates per expression, run again against the live database. Nothing here trusts that an
    // earlier /api/expression/validate call ever happened for any given cell.
    var compiler = new SqlExpressionCompiler(session.SourceConnectionString);
    var validator = new MappingValidator(session.SourceSchema, session.TargetSchema, compiler);
    var issues = await validator.ValidateAsync(mapping, ct);

    // Last gate before a human runs SQL against a real database: refuse outright if anything
    // blocking still stands, no matter what the request claims was already fixed.
    if (issues.HasBlocking())
    {
        return Results.BadRequest(new GenerateScriptResponse(
            "", issues.Where(i => i.Severity == Severity.Blocking).Select(DtoMapper.ToDto).ToList()));
    }

    var xml = MappingXml.Write(mapping);
    var sha = Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(xml)));

    try
    {
        var sql = ScriptGenerator.Generate(mapping, session.TargetSchema, new ScriptOptions(
            session.SourceServer, session.TargetServer, DateTimeOffset.UtcNow, "reviewed mapping", sha));

        return Results.Ok(new GenerateScriptResponse(sql, issues.Select(DtoMapper.ToDto).ToList()));
    }
    catch (ScriptGenerationException ex)
    {
        // A foreign-key cycle is only detectable at generation time, so it surfaces here
        // as ORD001 rather than as an unhandled 500.
        return Results.BadRequest(new GenerateScriptResponse("",
            [new IssueDto("ORD001", "Blocking", ex.Message, null, null)]));
    }
});

app.Run();

/// <summary>Exposed so WebApplicationFactory can host this app in tests.</summary>
public partial class Program;
