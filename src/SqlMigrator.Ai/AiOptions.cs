namespace SqlMigrator.Ai;

/// <summary>Configuration for the model provider. Populated from environment or appsettings.Local.json.</summary>
public sealed class AiOptions
{
    /// <summary>OpenAI-compatible base URL. Point this at a self-hosted NIM container to keep everything local.</summary>
    public string BaseUrl { get; init; } = "https://integrate.api.nvidia.com/v1";

    public string ApiKey { get; init; } = "";

    /// <summary>Model identifier from the NIM catalogue. Must support JSON-mode output.</summary>
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
}
