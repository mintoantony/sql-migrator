using System.Net;
using System.Text.Json;

namespace SqlMigrator.Ai.Tests;

public class OllamaChatClientTests
{
    private const string SuccessBody = """
        {"model":"test-model","message":{"role":"assistant","content":"{\"matches\":[]}"},"done":true,"done_reason":"stop"}
        """;

    private static AiOptions Options(string apiKey = "", int contextLength = 8192, int timeoutSeconds = 30) => new()
    {
        Provider = AiProvider.Ollama,
        BaseUrl = "http://ollama.invalid:11434/",
        ApiKey = apiKey,
        Model = "test-model",
        ContextLength = contextLength,
        TimeoutSeconds = timeoutSeconds
    };

    private static (OllamaChatClient Client, StubHandler Handler) Build(
        HttpStatusCode status = HttpStatusCode.OK, string body = SuccessBody, AiOptions? options = null)
    {
        var handler = new StubHandler(status, body);
        return (new OllamaChatClient(new HttpClient(handler), options ?? Options()), handler);
    }

    [Fact]
    public async Task Returns_the_assistant_message_content()
    {
        var (client, _) = Build();

        var content = await client.CompleteJsonAsync("system", "user");

        Assert.Equal("{\"matches\":[]}", content);
    }

    [Fact]
    public async Task Posts_to_the_native_chat_api_with_json_format_and_no_streaming()
    {
        var (client, handler) = Build();

        await client.CompleteJsonAsync("SYSTEM TEXT", "USER TEXT");

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("http://ollama.invalid:11434/api/chat", handler.LastRequest.RequestUri!.ToString());

        using var doc = JsonDocument.Parse(handler.LastBody!);
        var root = doc.RootElement;
        Assert.Equal("test-model", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal("json", root.GetProperty("format").GetString());

        var messages = root.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("SYSTEM TEXT", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("USER TEXT", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Requests_the_configured_context_window()
    {
        var (client, handler) = Build(options: Options(contextLength: 16384));

        await client.CompleteJsonAsync("s", "u");

        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(16384, doc.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
    }

    [Fact]
    public async Task Leaves_the_context_window_to_the_server_when_not_positive()
    {
        var (client, handler) = Build(options: Options(contextLength: 0));

        await client.CompleteJsonAsync("s", "u");

        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.False(doc.RootElement.GetProperty("options").TryGetProperty("num_ctx", out _));
    }

    [Fact]
    public async Task Sends_no_authorization_header_without_a_key()
    {
        var (client, handler) = Build();

        await client.CompleteJsonAsync("s", "u");

        Assert.Null(handler.LastRequest!.Headers.Authorization);
    }

    [Fact]
    public async Task Sends_a_configured_key_as_a_bearer_token()
    {
        var (client, handler) = Build(options: Options(apiKey: "proxy-secret"));

        await client.CompleteJsonAsync("s", "u");

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("proxy-secret", handler.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task A_missing_model_reports_the_status_and_ollamas_message()
    {
        var (client, _) = Build(HttpStatusCode.NotFound,
            """{"error":"model \"test-model\" not found, try pulling it first"}""");

        var ex = await Assert.ThrowsAsync<AiException>(() => client.CompleteJsonAsync("s", "u"));

        Assert.Contains("404", ex.Message);
        Assert.Contains("try pulling it first", ex.Message);
    }

    [Fact]
    public async Task An_answer_cut_off_by_the_context_window_throws_rather_than_returning_half_a_document()
    {
        var (client, _) = Build(body: """
            {"message":{"role":"assistant","content":"{\"matches\":[{\"sourceTable\":"},"done":true,"done_reason":"length"}
            """);

        var ex = await Assert.ThrowsAsync<AiException>(() => client.CompleteJsonAsync("s", "u"));

        Assert.Contains("Ai:ContextLength", ex.Message);
    }

    [Fact]
    public async Task A_response_without_a_message_throws()
    {
        var (client, _) = Build(body: """{"done":true}""");

        await Assert.ThrowsAsync<AiException>(() => client.CompleteJsonAsync("s", "u"));
    }

    [Fact]
    public async Task An_unreachable_server_is_reported_as_such()
    {
        var client = new OllamaChatClient(new HttpClient(new RefusingHandler()), Options());

        var ex = await Assert.ThrowsAsync<AiException>(() => client.CompleteJsonAsync("s", "u"));

        Assert.Contains("Could not reach Ollama at http://ollama.invalid:11434/", ex.Message);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task Configured_timeout_cancels_the_request()
    {
        using var handler = new BlockingHandler();
        var client = new OllamaChatClient(new HttpClient(handler), Options(timeoutSeconds: 0));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CompleteJsonAsync("s", "u"));
    }
}
