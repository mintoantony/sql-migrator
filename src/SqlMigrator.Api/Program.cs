using Microsoft.Data.SqlClient;
using SqlMigrator.Ai;
using SqlMigrator.Api;

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
    ConfidenceThreshold = double.TryParse(builder.Configuration["Ai:ConfidenceThreshold"], out var t) ? t : 0.75
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
    // never awaited or observed here.
    _ = Task.Run(() => AnalysisRunner.RunAsync(session, chatClient, aiOptions, CancellationToken.None));

    return Results.Ok(new AnalyseStarted(session.Id));
});

app.MapGet("/api/analyse/{id}", (string id, SessionStore store) =>
{
    var session = store.Get(id);
    if (session is null) return Results.NotFound();

    // State/Step/Mapping/Error come from one atomic snapshot — see Session.Result — so this
    // can never report "ready" with a null Mapping or a stale Step from a torn read.
    var result = session.Result;
    return Results.Ok(new SessionStatus(
        result.State, result.Step, result.Mapping,
        session.Issues.ToList(), session.Failures.ToList(), session.UnmatchedSourceTables.ToList(),
        result.Error));
});

app.Run();

/// <summary>Exposed so WebApplicationFactory can host this app in tests.</summary>
public partial class Program;
