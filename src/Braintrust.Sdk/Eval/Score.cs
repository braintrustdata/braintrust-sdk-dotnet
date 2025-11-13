namespace Braintrust.Sdk.Eval;

/// <summary>
/// Individual metric value assigned by a scorer.
/// </summary>
/// <param name="Name">Name of the metric being scored. This does not have to be the same as the scorer name, but it often will be.</param>
/// <param name="Value">Numeric representation of how well the task performed. Must be between 0.0 (inclusive) and 1.0 (inclusive). 0 is completely incorrect. 1 is completely correct.</param>
public record Score(string Name, double Value);
