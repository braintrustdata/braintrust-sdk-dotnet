using System.Net.Http.Headers;
using Braintrust.Sdk.Config;

namespace Braintrust.Sdk.Trace;

internal sealed class BraintrustOtlpAuthHandler : DelegatingHandler
{
    private readonly BraintrustConfig _config;

    internal BraintrustOtlpAuthHandler(BraintrustConfig config)
    {
        _config = config;
    }

    protected override HttpResponseMessage Send(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ApplyBraintrustHeadersAsync(request, cancellationToken).GetAwaiter().GetResult();
        return base.Send(request, cancellationToken);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        await ApplyBraintrustHeadersAsync(request, cancellationToken).ConfigureAwait(false);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyBraintrustHeadersAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var apiKey = await _config.GetRequiredApiKeyAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var parentValue = _config.GetBraintrustParentValue();
        if (parentValue != null)
        {
            request.Headers.Remove("x-bt-parent");
            request.Headers.TryAddWithoutValidation("x-bt-parent", parentValue);
        }
    }
}
