namespace SqlMigrator.Ai;

// AiException lives in Contracts.cs. Tasks 2 and 3 were written on separate branches and
// each declared it; the surviving copy is the one with an inner-exception constructor,
// which the JSON layer needs to preserve the underlying JsonException.

/// <summary>A model that answers with a JSON document. Deliberately narrow so tests can fake it.</summary>
public interface IChatClient
{
    Task<string> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);
}
