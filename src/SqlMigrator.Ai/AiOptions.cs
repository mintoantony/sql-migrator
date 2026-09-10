namespace SqlMigrator.Ai;

/// <summary>Which wire protocol the model provider speaks.</summary>
public enum AiProvider
{
    /// <summary>The OpenAI chat-completions surface: NVIDIA NIM, LM Studio, vLLM, llama.cpp server and the like.</summary>
    OpenAi,

    /// <summary>Ollama's native chat API, which — unlike its OpenAI-compatible one — can set the context window.</summary>
    Ollama
}

/// <summary>Configuration for the model provider. Populated from environment or appsettings.Local.json.</summary>
public sealed class AiOptions
{
    public const string NvidiaBaseUrl = "https://integrate.api.nvidia.com/v1";
    public const string OllamaBaseUrl = "http://localhost:11434";

    public AiProvider Provider { get; init; } = AiProvider.OpenAi;

    /// <summary>
    /// For <see cref="AiProvider.OpenAi"/>, the base URL that /chat/completions hangs off. For
    /// <see cref="AiProvider.Ollama"/>, the server root that /api/chat hangs off — no /v1.
    /// </summary>
    public string BaseUrl { get; init; } = NvidiaBaseUrl;

    /// <summary>Sent as a bearer token when set. Optional for local servers.</summary>
    public string ApiKey { get; init; } = "";

    /// <summary>Model identifier as the provider names it. Must support JSON-mode output.</summary>
    public string Model { get; init; } = "";

    /// <summary>Proposals at or above this arrive pre-accepted in the review grid.</summary>
    public double ConfidenceThreshold { get; init; } = 0.75;

    public int TimeoutSeconds { get; init; } = 120;

    /// <summary>
    /// How many table pairs go into one column-mapping request. A value of 1 reproduces the
    /// original one-call-per-table behaviour exactly — useful as a bail-out if batching ever
    /// misbehaves against a particular provider. 0 or negative is treated as 1.
    /// </summary>
    public int ColumnBatchSize { get; init; } = 5;

    /// <summary>
    /// Ollama only: the context window, in tokens, requested for every call. Ollama's own default
    /// is small, and it truncates an over-long prompt rather than rejecting it, so a batched
    /// column-mapping prompt would silently lose tables. 0 or negative leaves it to the server.
    /// </summary>
    public int ContextLength { get; init; } = 8192;

    public static string DefaultBaseUrl(AiProvider provider) =>
        provider == AiProvider.Ollama ? OllamaBaseUrl : NvidiaBaseUrl;

    /// <summary>A local model can take minutes on one call, and its first call also loads the model.</summary>
    public static int DefaultTimeoutSeconds(AiProvider provider) =>
        provider == AiProvider.Ollama ? 600 : 120;

    /// <summary>Reads Ai:Provider. Unset means <see cref="AiProvider.OpenAi"/>; anything unrecognised is refused.</summary>
    public static AiProvider ParseProvider(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return AiProvider.OpenAi;

        // Matched against the names rather than via Enum.TryParse, which would also accept "7".
        var name = Enum.GetNames<AiProvider>()
            .FirstOrDefault(n => string.Equals(n, value.Trim(), StringComparison.OrdinalIgnoreCase));
        if (name is not null) return Enum.Parse<AiProvider>(name);

        throw new InvalidOperationException(
            $"Ai:Provider '{value}' is not recognised. Use '{AiProvider.OpenAi}' or '{AiProvider.Ollama}'.");
    }

    /// <summary>The client that speaks <see cref="Provider"/>'s protocol.</summary>
    public IChatClient CreateClient(HttpClient http) => Provider switch
    {
        AiProvider.Ollama => new OllamaChatClient(http, this),
        _ => new OpenAiChatClient(http, this)
    };
}
