using System.Net;
using System.Text.Json;

namespace SqlMigrator.Ai.Tests;

public class OpenAiChatClientTests
{
    private const string SuccessBody = """
        {"choices":[{"message":{"role":"assistant","content":"{\"matches\":[]}"}}]}
        """;

    private static (OpenAiChatClient Client, StubHandler Handler) Build(
        HttpStatusCode status = HttpStatusCode.OK, string body = SuccessBody, string apiKey = "nvapi-secret-value")
    {
        var handler = new StubHandler(status, body);
        var http = new HttpClient(handler);
        var options = new AiOptions
        {
            BaseUrl = "https://example.invalid/v1",
            ApiKey = apiKey,
            Model = "test/model"
        };
        return (new OpenAiChatClient(http, options), handler);
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
    public async Task Sends_no_authorization_header_without_a_key()
    {
        var (client, handler) = Build(apiKey: "");

        await client.CompleteJsonAsync("s", "u");

        Assert.Null(handler.LastRequest!.Headers.Authorization);
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

    [Fact]
    public async Task Configured_timeout_cancels_the_request()
    {
        // A near-zero timeout combined with a handler that blocks until cancelled
        // proves the timeout actually reaches the request, without waiting out a
        // real timeout period.
        using var handler = new BlockingHandler();
        using var http = new HttpClient(handler);
        var options = new AiOptions
        {
            BaseUrl = "https://example.invalid/v1",
            ApiKey = "nvapi-secret-value",
            Model = "test/model",
            TimeoutSeconds = 0
        };
        var client = new OpenAiChatClient(http, options);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.CompleteJsonAsync("s", "u"));
    }
}
