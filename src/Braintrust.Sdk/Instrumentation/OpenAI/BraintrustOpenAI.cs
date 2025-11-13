using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Net.Http;
using OpenAI;

namespace Braintrust.Sdk.Instrumentation.OpenAI;

/// <summary>
/// Braintrust OpenAI client instrumentation.
///
/// This class provides the main entry point for instrumenting OpenAI clients with Braintrust traces.
/// It creates instrumented OpenAI clients that automatically capture telemetry data for chat completions,
/// embeddings, and other OpenAI operations.
/// </summary>
public static class BraintrustOpenAI
{
    /// <summary>
    /// Creates an instrumented OpenAI client with Braintrust traces.
    ///
    /// This method creates a new OpenAI client with the provided configuration and injects
    /// HTTP interception to capture request/response data for telemetry.
    /// </summary>
    /// <param name="activitySource">The ActivitySource for creating spans</param>
    /// <param name="openAIApiKey">The OpenAI API key</param>
    /// <param name="options">Optional OpenAI client options for custom configuration</param>
    /// <returns>An instrumented OpenAI client that will emit telemetry</returns>
    /// <example>
    /// <code>
    /// var braintrust = Braintrust.Get();
    /// var tracerProvider = braintrust.OpenTelemetryCreate();
    /// var activitySource = BraintrustTracing.GetActivitySource();
    ///
    /// // Simple usage
    /// var client = BraintrustOpenAI.WrapOpenAI(activitySource, apiKey);
    ///
    /// // With custom options
    /// var options = new OpenAIClientOptions { /* custom config */ };
    /// var client = BraintrustOpenAI.WrapOpenAI(activitySource, apiKey, options);
    ///
    /// // Use the instrumented client normally - telemetry will be captured automatically
    /// var response = await client.GetChatClient("gpt-4").CompleteChatAsync(messages);
    /// </code>
    /// </example>
    public static OpenAIClient WrapOpenAI(
        ActivitySource activitySource,
        string openAIApiKey,
        OpenAIClientOptions? options = null)
    {
        if (activitySource == null)
            throw new ArgumentNullException(nameof(activitySource));
        if (string.IsNullOrEmpty(openAIApiKey))
            throw new ArgumentNullException(nameof(openAIApiKey));

        // Use provided options or create default ones
        options ??= new OpenAIClientOptions();

        // Wrap their transport if they have one, or create a default transport
        // This allows users to provide custom transports and we'll just intercept the data
        var innerTransport = options.Transport ?? new HttpClientPipelineTransport(new HttpClient());
        options.Transport = new BraintrustPipelineTransport(innerTransport);

        // Create API key credential
        var credential = new ApiKeyCredential(openAIApiKey);

        // Create the OpenAI client - it will now use our transport wrapper for all requests
        var openAIClient = new OpenAIClient(credential, options);

        // Wrap the client with our instrumentation
        return OpenAITelemetry.Builder(activitySource)
            .SetCaptureMessageContent(true)
            .Build()
            .Wrap(openAIClient);
    }
}
