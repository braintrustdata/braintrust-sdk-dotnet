using System.Diagnostics;

namespace Braintrust.Sdk.AgentFramework;

/// <summary>
/// Entry point for instrumenting Microsoft Agent Framework agents and chat clients.
/// </summary>
internal sealed class AgentFrameworkTelemetry
{
    private readonly ActivitySource _activitySource;
    private readonly bool _captureMessageContent;
    private readonly bool _captureToolArguments;

    /// <summary>
    /// Returns a new AgentFrameworkTelemetry with default settings.
    /// </summary>
    public static AgentFrameworkTelemetry Create(ActivitySource activitySource)
    {
        return Builder(activitySource).Build();
    }

    /// <summary>
    /// Returns a builder for configuring telemetry.
    /// </summary>
    public static AgentFrameworkTelemetryBuilder Builder(ActivitySource activitySource)
    {
        return new AgentFrameworkTelemetryBuilder(activitySource);
    }

    internal AgentFrameworkTelemetry(
        ActivitySource activitySource,
        bool captureMessageContent,
        bool captureToolArguments)
    {
        _activitySource = activitySource ?? throw new ArgumentNullException(nameof(activitySource));
        _captureMessageContent = captureMessageContent;
        _captureToolArguments = captureToolArguments;
    }

    internal ActivitySource ActivitySource => _activitySource;
    internal bool CaptureMessageContent => _captureMessageContent;
    internal bool CaptureToolArguments => _captureToolArguments;
}
