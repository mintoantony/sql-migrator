using System.Text.Json;

namespace SqlMigrator.Ai;

/// <summary>Raised when a model response cannot be parsed into the shape we asked for.</summary>
public sealed class AiException : Exception
{
    public AiException(string message) : base(message)
    {
    }

    public AiException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public sealed record TableMatch(string SourceTable, string TargetTable, double Confidence, string Reason);

public sealed record TableMatchResponse(List<TableMatch>? Matches)
{
    public List<TableMatch> Matches { get; init; } = Matches ?? [];
}

public sealed record ColumnProposal(
    string TargetColumn, string Rule, string Expression, double Confidence, string Reason);

public sealed record UnmappedProposal(string TargetColumn, string Reason);

public sealed record ColumnMapResponse(List<ColumnProposal>? Columns, List<UnmappedProposal>? Unmapped)
{
    public List<ColumnProposal> Columns { get; init; } = Columns ?? [];
    public List<UnmappedProposal> Unmapped { get; init; } = Unmapped ?? [];
}

public static class AiJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static T Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(StripFence(json), Options)
                   ?? throw new AiException("Model returned a JSON null.");
        }
        catch (JsonException ex)
        {
            throw new AiException($"Model response was not valid JSON: {ex.Message}", ex);
        }
    }

    /// <summary>Removes a ```json fence, which models add often enough to be worth handling.</summary>
    private static string StripFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0) return trimmed;

        var body = trimmed[(firstNewline + 1)..];
        var closing = body.LastIndexOf("```", StringComparison.Ordinal);
        return (closing < 0 ? body : body[..closing]).Trim();
    }
}
