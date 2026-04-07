using System.Diagnostics;

namespace Braintrust.Sdk.AgentFramework;

/// <summary>
/// Builder for configuring Microsoft Agent Framework telemetry instrumentation.
/// </summary>
internal sealed class AgentFrameworkTelemetryBuilder
{
    private readonly ActivitySource _activitySource;
    private bool _captureMessageContent = true;
    private bool _captureToolArguments = true;

    internal AgentFrameworkTelemetryBuilder(ActivitySource activitySource)
    {
        _activitySource = activitySource ?? throw new ArgumentNullException(nameof(activitySource));
    }

    /// <summary>
    /// Sets whether to capture the full content of user and assistant messages.
    /// </summary>
    public AgentFrameworkTelemetryBuilder SetCaptureMessageContent(bool captureMessageContent)
    {
        _captureMessageContent = captureMessageContent;
        return this;
    }

    /// <summary>
    /// Sets whether to capture function/tool call arguments and results.
    /// </summary>
    public AgentFrameworkTelemetryBuilder SetCaptureToolArguments(bool captureToolArguments)
    {
        _captureToolArguments = captureToolArguments;
        return this;
    }

    /// <summary>
    /// Builds the AgentFrameworkTelemetry instance with the configured settings.
    /// </summary>
    public AgentFrameworkTelemetry Build()
    {
        return new AgentFrameworkTelemetry(
            _activitySource,
            _captureMessageContent,
            _captureToolArguments);
    }
}
