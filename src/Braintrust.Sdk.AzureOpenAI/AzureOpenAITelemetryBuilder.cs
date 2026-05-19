using System.Diagnostics;

namespace Braintrust.Sdk.AzureOpenAI;

/// <summary>
/// Builder for configuring Azure OpenAI telemetry instrumentation.
///
/// Provides a fluent API for configuring how Azure OpenAI clients are instrumented.
/// </summary>
internal sealed class AzureOpenAITelemetryBuilder
{
    internal const string InstrumentationName = "io.opentelemetry.azure-openai-dotnet-1.0";

    private readonly ActivitySource _activitySource;

    internal AzureOpenAITelemetryBuilder(ActivitySource activitySource)
    {
        _activitySource = activitySource ?? throw new ArgumentNullException(nameof(activitySource));
    }

    /// <summary>
    /// Builds the AzureOpenAITelemetry instance with the configured settings.
    /// </summary>
    /// <returns>A new AzureOpenAITelemetry instance</returns>
    public AzureOpenAITelemetry Build()
    {
        return new AzureOpenAITelemetry(_activitySource);
    }
}
