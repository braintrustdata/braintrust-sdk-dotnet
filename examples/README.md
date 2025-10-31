# Braintrust SDK Examples

This directory contains example applications demonstrating how to use the Braintrust .NET SDK.

## Prerequisites

Before running these examples, make sure you have:

1. .NET 8.0 SDK installed
2. A Braintrust account and API key
3. Environment variables configured (see below)

## Configuration

Set the following environment variables:

```bash
export BRAINTRUST_API_KEY="your-api-key"
export BRAINTRUST_DEFAULT_PROJECT_NAME="your-project-name"
# Or use project ID:
# export BRAINTRUST_DEFAULT_PROJECT_ID="your-project-id"
```

## Running the Examples

### Simple OpenTelemetry Example

This example demonstrates basic OpenTelemetry tracing with Braintrust:

```bash
cd examples
dotnet run
```

The example:
- Initializes the Braintrust SDK
- Sets up OpenTelemetry tracing
- Creates a simple span with attributes
- Prints a URL to view the trace in Braintrust

## Example Code

The main example is in `Program.cs`:

```csharp
var braintrust = Braintrust.Get();
var tracerProvider = braintrust.OpenTelemetryCreate();
var activitySource = BraintrustTracing.GetActivitySource();

using (var activity = activitySource.StartActivity("hello-dotnet"))
{
    activity?.SetTag("some boolean attribute", true);
    // Your code here...
}
```

## Next Steps

- Check out the [Braintrust documentation](https://www.braintrust.dev/docs) for more advanced usage
- Explore the SDK source code in `src/Braintrust.Sdk/`
- Look at the test files in `tests/Braintrust.Sdk.Tests/` for more examples
