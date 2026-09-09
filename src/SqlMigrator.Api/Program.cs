using Microsoft.Data.SqlClient;
using SqlMigrator.Api;

var builder = WebApplication.CreateBuilder(args);

// Localhost only. Never bind a wildcard host: this process can reach two databases.
builder.WebHost.UseUrls("http://127.0.0.1:5199");
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true);

builder.Services.AddSingleton<SessionStore>();
builder.Services.AddHttpClient();

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

app.Run();

/// <summary>Exposed so WebApplicationFactory can host this app in tests.</summary>
public partial class Program;
