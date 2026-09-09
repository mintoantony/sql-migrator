namespace SqlMigrator.Ai;

public sealed class AiException(string message) : Exception(message);

/// <summary>A model that answers with a JSON document. Deliberately narrow so tests can fake it.</summary>
public interface IChatClient
{
    Task<string> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);
}
