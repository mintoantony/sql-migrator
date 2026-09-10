using System.Net;

namespace SqlMigrator.Ai.Tests;

/// <summary>Records the request and returns a canned response, so tests never touch the network.</summary>
public sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastBody { get; private set; }
    public int CallCount { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequest = request;
        LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
    }
}

/// <summary>Never completes on its own; only returns once its cancellation token fires.</summary>
public sealed class BlockingHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("unreachable: Task.Delay should have thrown on cancellation.");
    }
}

/// <summary>Fails the way HttpClient does when nothing is listening on the port.</summary>
public sealed class RefusingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
}
