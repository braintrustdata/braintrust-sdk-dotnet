using System.Net.Http.Headers;
using Braintrust.Sdk.Config;

namespace Braintrust.Sdk.Api;

/// <summary>
/// Attaches the Braintrust API key to every outgoing request.
///
/// The generated client builds its own <see cref="HttpRequestMessage"/> instances and
/// exposes only a synchronous PrepareRequest hook, so authentication is applied here
/// instead - a handler can await the key without blocking.
/// </summary>
internal sealed class BearerTokenHandler : DelegatingHandler
{
    private readonly BraintrustConfig _config;

    public BearerTokenHandler(BraintrustConfig config, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var apiKey = await _config.GetRequiredApiKeyAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        if (!request.Headers.Accept.Any())
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
