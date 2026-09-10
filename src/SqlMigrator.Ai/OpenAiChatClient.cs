using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SqlMigrator.Ai;

/// <summary>
/// Talks the OpenAI-compatible chat-completions surface. NVIDIA NIM is the default, but any
/// provider on that surface works by changing BaseUrl. For Ollama use <see cref="OllamaChatClient"/>.
/// </summary>
public sealed class OpenAiChatClient(HttpClient http, AiOptions options) : IChatClient
{
    public async Task<string> CompleteJsonAsync(
        string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        // The HttpClient is injected and may be shared, so we can't set its Timeout
        // (that throws once the client has sent a request, and would affect other
        // callers anyway). Enforce options.TimeoutSeconds per-request instead, linked
        // to the caller's token so either one still cancels the request.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/chat/completions")
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

        // Local servers (LM Studio, vLLM, llama.cpp) usually need no key; an empty bearer is noise.
        if (!string.IsNullOrEmpty(options.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        using var response = await http.SendAsync(request, linkedCts.Token);
        var body = await response.Content.ReadAsStringAsync(linkedCts.Token);

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
