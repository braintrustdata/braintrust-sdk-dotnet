using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
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
    public static OpenAIClient WrapOpenAI(
        ActivitySource activitySource,
        string openAIApiKey,
        OpenAIClientOptions? options = null)
    {
        if (activitySource == null)
            throw new ArgumentNullException(nameof(activitySource));
        if (string.IsNullOrEmpty(openAIApiKey))
            throw new ArgumentNullException(nameof(openAIApiKey));

        options ??= new OpenAIClientOptions();
        var innerTransport = options.Transport ?? new HttpClientPipelineTransport(new HttpClient());
        options.Transport = new BraintrustPipelineTransport(innerTransport);
        var credential = new ApiKeyCredential(openAIApiKey);
        var openAIClient = new OpenAIClient(credential, options);
        return OpenAITelemetry.Builder(activitySource)
            .SetCaptureMessageContent(true)
            .Build()
            .Wrap(openAIClient);
    }
}
