namespace Braintrust.Sdk.Eval;

/// <summary>
/// Results of all eval cases of an experiment.
/// </summary>
public class EvalResult
{
    /// <summary>
    /// URL to view the experiment results in Braintrust.
    /// </summary>
    public string ExperimentUrl { get; }

    internal EvalResult(string experimentUrl)
    {
        ExperimentUrl = experimentUrl;
    }

    /// <summary>
    /// Creates a formatted report string with the experiment URL.
    /// </summary>
    public string CreateReportString()
    {
        return $"Experiment complete. View results in braintrust: {ExperimentUrl}";
    }
}
