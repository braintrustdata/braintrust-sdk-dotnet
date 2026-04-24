using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using Azure.AI.OpenAI;
using Braintrust.Sdk.OpenAI;

namespace Braintrust.Sdk.AzureOpenAI;

/// <summary>
/// Braintrust Azure OpenAI client instrumentation.
///
/// This class provides the main entry point for instrumenting Azure OpenAI clients with Braintrust traces.
/// It creates instrumented Azure OpenAI clients that automatically capture telemetry data for chat completions,
/// embeddings, and other Azure OpenAI operations.
///
/// Azure.AI.OpenAI's <see cref="AzureOpenAIClient"/> inherits from OpenAI's <c>OpenAIClient</c>, so the
/// same telemetry pipeline from <c>Braintrust.Sdk.OpenAI</c> is reused here.
/// </summary>
public static class BraintrustAzureOpenAI
{
    /// <summary>
    /// Instrument an Azure OpenAI client with Braintrust traces.
    ///
    /// This extension method wraps an existing <see cref="AzureOpenAIClient"/> with Braintrust telemetry,
    /// capturing request/response data for chat completions and other operations.
    /// </summary>
    /// <param name="client">The Azure OpenAI client to instrument</param>
    /// <param name="captureMessageContent">Whether to capture message content in telemetry (default: true)</param>
    /// <returns>An instrumented Azure OpenAI client that will emit telemetry</returns>
    public static AzureOpenAIClient WithBraintrust(
        this AzureOpenAIClient client,
        bool captureMessageContent = true)
    {
        var braintrust = Braintrust.Get();
        var activitySource = braintrust.GetActivitySource();
        return client.WithBraintrust(activitySource, captureMessageContent);
    }

    /// <summary>
    /// Instrument an Azure OpenAI client with Braintrust traces.
    ///
    /// This extension method wraps an existing <see cref="AzureOpenAIClient"/> with Braintrust telemetry,
    /// capturing request/response data for chat completions and other operations.
    /// </summary>
    /// <param name="client">The Azure OpenAI client to instrument</param>
    /// <param name="activitySource">The ActivitySource for creating spans</param>
    /// <param name="captureMessageContent">Whether to capture message content in telemetry (default: true)</param>
    /// <returns>An instrumented Azure OpenAI client that will emit telemetry</returns>
    public static AzureOpenAIClient WithBraintrust(
        this AzureOpenAIClient client,
        ActivitySource activitySource,
        bool captureMessageContent = true)
    {
        if (client == null)
            throw new ArgumentNullException(nameof(client));
        if (activitySource == null)
            throw new ArgumentNullException(nameof(activitySource));

        return AzureOpenAITelemetry.Builder(activitySource)
            .SetCaptureMessageContent(captureMessageContent)
            .Build()
            .Wrap(client);
    }

    /// <summary>
    /// Creates an instrumented Azure OpenAI client authenticated with an API key.
    ///
    /// This method creates a new <see cref="AzureOpenAIClient"/> with the provided configuration and injects
    /// HTTP interception to capture request/response data for telemetry.
    /// </summary>
    /// <param name="activitySource">The ActivitySource for creating spans</param>
    /// <param name="endpoint">The Azure OpenAI endpoint URI</param>
    /// <param name="apiKey">The Azure OpenAI API key</param>
    /// <param name="options">Optional Azure OpenAI client options for custom configuration</param>
    /// <returns>An instrumented Azure OpenAI client that will emit telemetry</returns>
    public static AzureOpenAIClient WrapAzureOpenAI(
        ActivitySource activitySource,
        Uri endpoint,
        string apiKey,
        AzureOpenAIClientOptions? options = null)
    {
        if (activitySource == null)
            throw new ArgumentNullException(nameof(activitySource));
        if (endpoint == null)
            throw new ArgumentNullException(nameof(endpoint));
        if (string.IsNullOrEmpty(apiKey))
            throw new ArgumentNullException(nameof(apiKey));

        options ??= new AzureOpenAIClientOptions();
        var innerTransport = options.Transport ?? new HttpClientPipelineTransport(new HttpClient());
        options.Transport = new BraintrustPipelineTransport(innerTransport);
        var credential = new ApiKeyCredential(apiKey);
        var azureClient = new AzureOpenAIClient(endpoint, credential, options);
        return AzureOpenAITelemetry.Builder(activitySource)
            .SetCaptureMessageContent(true)
            .Build()
            .Wrap(azureClient);
    }

    /// <summary>
    /// Creates an instrumented Azure OpenAI client authenticated with a <see cref="TokenCredential"/>.
    ///
    /// This overload supports Microsoft Entra ID (AAD) authentication, which is the recommended approach
    /// for enterprise scenarios.
    /// </summary>
    /// <param name="activitySource">The ActivitySource for creating spans</param>
    /// <param name="endpoint">The Azure OpenAI endpoint URI</param>
    /// <param name="credential">The token credential for Entra ID authentication</param>
    /// <param name="options">Optional Azure OpenAI client options for custom configuration</param>
    /// <returns>An instrumented Azure OpenAI client that will emit telemetry</returns>
    public static AzureOpenAIClient WrapAzureOpenAI(
        ActivitySource activitySource,
        Uri endpoint,
        Azure.Core.TokenCredential credential,
        AzureOpenAIClientOptions? options = null)
    {
        if (activitySource == null)
            throw new ArgumentNullException(nameof(activitySource));
        if (endpoint == null)
            throw new ArgumentNullException(nameof(endpoint));
        if (credential == null)
            throw new ArgumentNullException(nameof(credential));

        options ??= new AzureOpenAIClientOptions();
        var innerTransport = options.Transport ?? new HttpClientPipelineTransport(new HttpClient());
        options.Transport = new BraintrustPipelineTransport(innerTransport);
        var azureClient = new AzureOpenAIClient(endpoint, credential, options);
        return AzureOpenAITelemetry.Builder(activitySource)
            .SetCaptureMessageContent(true)
            .Build()
            .Wrap(azureClient);
    }
}
