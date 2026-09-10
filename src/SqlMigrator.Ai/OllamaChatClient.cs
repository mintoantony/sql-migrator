using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SqlMigrator.Ai;

/// <summary>
/// Talks Ollama's native chat API (POST /api/chat). Ollama also serves an OpenAI-compatible
/// /v1 surface, which <see cref="OpenAiChatClient"/> could use, but that surface cannot set
/// the context window — and Ollama's default window is small enough that a batched
/// column-mapping prompt overflows it, whereupon Ollama truncates the prompt instead of
/// failing. The model then maps part of a schema and nothing says so. Here every request
/// carries <see cref="AiOptions.ContextLength"/> as num_ctx.
/// </summary>
public sealed class OllamaChatClient(HttpClient http, AiOptions options) : IChatClient
{
    public async Task<string> CompleteJsonAsync(
        string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        // Same per-request timeout as OpenAiChatClient, for the same reason: the injected
        // HttpClient may be shared, so its own Timeout is not ours to set.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        // A dictionary rather than an anonymous type so num_ctx can be left out entirely.
        var modelOptions = new Dictionary<string, object> { ["temperature"] = 0.1 };
        if (options.ContextLength > 0) modelOptions["num_ctx"] = options.ContextLength;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/api/chat")
        {
            Content = JsonContent.Create(new
            {
                model = options.Model,
                stream = false,
                format = "json",
                options = modelOptions,
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user",   content = userPrompt }
                }
            })
        };

        // A local Ollama needs no key. One behind an authenticating proxy may.
        if (!string.IsNullOrEmpty(options.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, linkedCts.Token);
        }
        catch (HttpRequestException ex)
        {
            // By far the likeliest cause locally, and the raw socket error does not say so.
            throw new AiException(
                $"Could not reach Ollama at {options.BaseUrl} — is it running? {ex.Message}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(linkedCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                // Ollama's 404 body already says "model not found, try pulling it first".
                throw new AiException(
                    $"Ollama returned {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body, 400)}");
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.TryGetProperty("done_reason", out var doneReason) && doneReason.GetString() == "length")
                {
                    throw new AiException(
                        "The model ran out of context before finishing its answer. " +
                        "Raise Ai:ContextLength or lower Ai:ColumnBatchSize.");
                }

                return root.GetProperty("message").GetProperty("content").GetString()
                       ?? throw new AiException("Ollama returned an empty message.");
            }
            catch (JsonException ex)
            {
                throw new AiException($"Could not read the Ollama response: {ex.Message}");
            }
            catch (KeyNotFoundException)
            {
                throw new AiException("Ollama response did not have the expected /api/chat shape.");
            }
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
