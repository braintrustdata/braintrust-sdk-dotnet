namespace Braintrust.Sdk.Eval;

/// <summary>
/// A cursor for iterating through dataset cases.
/// Not thread-safe.
/// </summary>
/// <typeparam name="TCase">The type of case being iterated</typeparam>
public interface ICursor<TCase> : IDisposable
{
    /// <summary>
    /// Fetch the next case. Returns null if there are no more cases to fetch.
    ///
    /// Implementations may make external requests to fetch data.
    ///
    /// If this method is invoked after Close() or Dispose(), an InvalidOperationException will be thrown.
    /// </summary>
    TCase? Next();

    /// <summary>
    /// Close the cursor and release all resources.
    /// </summary>
    void Close();
}