using System.Net;
using System.Text;

namespace Braintrust.Sdk.Tests;

/// <summary>
/// Serves queued responses in order and records what was asked for. Call order here is
/// deterministic, so a queue is enough - the assertions check the paths.
/// </summary>
internal sealed class QueuedHttpHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();

    public List<(HttpMethod Method, string Path, string Query, string Body)> Requests { get; } = [];

    public void Enqueue(string body, HttpStatusCode status = HttpStatusCode.OK)
        => _responses.Enqueue((status, body));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);

        Requests.Add((request.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query, body));

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException(
                $"No response configured for {request.Method} {request.RequestUri.AbsolutePath}");
        }

        var (status, responseBody) = _responses.Dequeue();
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        };
    }
}
