using Xunit;

namespace Braintrust.Sdk.Tests;

/// <summary>
/// Test collection for tests that use shared global Braintrust state.
///
/// Any test class that uses global state like:
/// - BraintrustTracing.GetActivitySource() (shared ActivitySource singleton)
/// - ActivitySource.AddActivityListener() (global registration)
/// - Any other shared static/singleton state in the Braintrust SDK
///
/// Should be decorated with [Collection("BraintrustGlobals")] to prevent
/// race conditions from parallel test execution.
///
/// Usage:
/// <code>
/// [Collection("BraintrustGlobals")]
/// public class MyBraintrustTest { ... }
/// </code>
/// </summary>
[CollectionDefinition("BraintrustGlobals")]
public class BraintrustGlobalsCollection
{
    // This class is never instantiated. It exists only to define the collection
    // and provide documentation for developers adding new tests.
}
