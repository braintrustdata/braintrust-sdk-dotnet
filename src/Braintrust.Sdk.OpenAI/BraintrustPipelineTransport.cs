using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;

namespace Braintrust.Sdk.OpenAI;

/// <summary>
/// Pipeline transport wrapper that captures request and response bodies for telemetry.
///
/// This transport wraps another transport (user-provided or default) and intercepts
/// the request/response data at the PipelineMessage level.
/// </summary>
internal sealed class BraintrustPipelineTransport : PipelineTransport
{
    private readonly PipelineTransport _innerTransport;

    public BraintrustPipelineTransport(PipelineTransport innerTransport)
    {
        _innerTransport = innerTransport ?? throw new ArgumentNullException(nameof(innerTransport));
    }

    protected override PipelineMessage CreateMessageCore()
    {
        // Delegate to inner transport to create the message
        return _innerTransport.CreateMessage();
    }

    protected override void ProcessCore(PipelineMessage message)
    {
        // Capture request
        string? requestBody = CaptureRequest(message.Request);

        // Delegate to inner transport to process
        _innerTransport.Process(message);

        // Capture response
        string? responseBody = CaptureResponse(message.Response);

        // Store in Activity baggage
        StoreInBaggage(requestBody, responseBody);
    }

    protected override async ValueTask ProcessCoreAsync(PipelineMessage message)
    {
        // Capture request
        string? requestBody = await CaptureRequestAsync(message.Request, CancellationToken.None).ConfigureAwait(false);

        // Delegate to inner transport to process
        await _innerTransport.ProcessAsync(message).ConfigureAwait(false);

        // Capture response
        string? responseBody = await CaptureResponseAsync(message.Response, CancellationToken.None).ConfigureAwait(false);

        // Store in Activity baggage
        StoreInBaggage(requestBody, responseBody);
    }

    private string? CaptureRequest(PipelineRequest request)
    {
        if (request?.Content == null) return null;

        try
        {
            // Capture the content to a buffer so we can both read it and replay it
            var memoryStream = new MemoryStream();
            request.Content.WriteTo(memoryStream, cancellationToken: default);
            var capturedBytes = memoryStream.ToArray();

            // Replace the request content with a replayable version from the captured bytes
            request.Content = BinaryContent.Create(new BinaryData(capturedBytes));

            // Return the captured content as a string for telemetry
            using var reader = new StreamReader(new MemoryStream(capturedBytes));
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> CaptureRequestAsync(PipelineRequest request, CancellationToken cancellationToken)
    {
        if (request?.Content == null) return null;

        try
        {
            // Capture the content to a buffer so we can both read it and replay it
            var memoryStream = new MemoryStream();
            await request.Content.WriteToAsync(memoryStream, cancellationToken).ConfigureAwait(false);
            var capturedBytes = memoryStream.ToArray();

            // Replace the request content with a replayable version from the captured bytes
            request.Content = BinaryContent.Create(new BinaryData(capturedBytes));

            // Return the captured content as a string for telemetry
            using var reader = new StreamReader(new MemoryStream(capturedBytes));
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private string? CaptureResponse(PipelineResponse? response)
    {
        if (response?.Content == null) return null;

        try
        {
            using var stream = response.Content.ToStream();
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> CaptureResponseAsync(PipelineResponse? response, CancellationToken cancellationToken)
    {
        if (response?.Content == null) return null;

        try
        {
            using var stream = response.Content.ToStream();
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private void StoreInBaggage(string? requestBody, string? responseBody)
    {
        var currentActivity = Activity.Current;
        if (currentActivity != null)
        {
            if (requestBody != null)
            {
                currentActivity.SetBaggage("braintrust.http.request", requestBody);
            }
            if (responseBody != null)
            {
                currentActivity.SetBaggage("braintrust.http.response", responseBody);
            }
        }
    }
}
