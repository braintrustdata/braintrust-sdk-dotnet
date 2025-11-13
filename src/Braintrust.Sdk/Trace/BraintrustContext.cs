using System;
using System.Diagnostics;
using System.Threading;

namespace Braintrust.Sdk.Trace;

/// <summary>
/// Context carrier for Braintrust parent relationships (project/experiment IDs).
/// This is stored in AsyncLocal to propagate parent information through the trace.
/// </summary>
public sealed class BraintrustContext
{
    private static readonly AsyncLocal<BraintrustContext?> _current = new AsyncLocal<BraintrustContext?>();

    public string? ProjectId { get; }
    public string? ExperimentId { get; }

    private BraintrustContext(string? projectId, string? experimentId)
    {
        ProjectId = projectId;
        ExperimentId = experimentId;
    }

    /// <summary>
    /// Create a Braintrust context for an experiment.
    /// </summary>
    public static BraintrustContext OfExperiment(string experimentId)
    {
        return new BraintrustContext(null, experimentId);
    }

    /// <summary>
    /// Create a Braintrust context for a project.
    /// </summary>
    public static BraintrustContext OfProject(string projectId)
    {
        return new BraintrustContext(projectId, null);
    }

    /// <summary>
    /// Get the current Braintrust context.
    /// </summary>
    public static BraintrustContext? Current => _current.Value;

    /// <summary>
    /// Get the Braintrust context from the current Activity (span).
    /// </summary>
    public static BraintrustContext? FromContext(Activity? activity = null)
    {
        // Return the current async local value
        return _current.Value;
    }

    /// <summary>
    /// Set this context as the current Braintrust context.
    /// Returns a disposable scope that will restore the previous context.
    /// </summary>
    public IDisposable MakeCurrent()
    {
        var previous = _current.Value;
        _current.Value = this;
        return new ContextScope(previous);
    }

    private class ContextScope : IDisposable
    {
        private readonly BraintrustContext? _previous;

        public ContextScope(BraintrustContext? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            _current.Value = _previous;
        }
    }

    /// <summary>
    /// Get the parent value string for this context (format: "project_id:X" or "experiment_id:Y").
    /// </summary>
    public string? GetParentValue()
    {
        if (ExperimentId != null)
        {
            return $"experiment_id:{ExperimentId}";
        }
        if (ProjectId != null)
        {
            return $"project_id:{ProjectId}";
        }
        return null;
    }
}
