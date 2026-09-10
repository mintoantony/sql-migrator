namespace SqlMigrator.Ai.Tests;

public class AiOptionsTests
{
    [Theory]
    [InlineData(null, AiProvider.OpenAi)]
    [InlineData("", AiProvider.OpenAi)]
    [InlineData("OpenAi", AiProvider.OpenAi)]
    [InlineData("openai", AiProvider.OpenAi)]
    [InlineData("Ollama", AiProvider.Ollama)]
    [InlineData(" ollama ", AiProvider.Ollama)]
    public void Parses_the_provider_name(string? value, AiProvider expected)
    {
        Assert.Equal(expected, AiOptions.ParseProvider(value));
    }

    [Theory]
    [InlineData("Olama")]
    [InlineData("1")]
    [InlineData("7")]
    public void Refuses_an_unrecognised_provider_rather_than_guessing(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => AiOptions.ParseProvider(value));

        Assert.Contains("Ai:Provider", ex.Message);
    }

    [Fact]
    public void Each_provider_gets_the_client_that_speaks_its_protocol()
    {
        using var http = new HttpClient();

        Assert.IsType<OllamaChatClient>(new AiOptions { Provider = AiProvider.Ollama }.CreateClient(http));
        Assert.IsType<OpenAiChatClient>(new AiOptions { Provider = AiProvider.OpenAi }.CreateClient(http));
    }

    [Fact]
    public void Ollama_defaults_to_the_local_server_and_a_longer_timeout()
    {
        Assert.Equal("http://localhost:11434", AiOptions.DefaultBaseUrl(AiProvider.Ollama));
        Assert.Equal("https://integrate.api.nvidia.com/v1", AiOptions.DefaultBaseUrl(AiProvider.OpenAi));
        Assert.True(AiOptions.DefaultTimeoutSeconds(AiProvider.Ollama) > AiOptions.DefaultTimeoutSeconds(AiProvider.OpenAi));
    }
}
