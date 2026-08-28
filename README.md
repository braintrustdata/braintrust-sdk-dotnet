[![Braintrust](./braintrust-logo.svg)](https://www.braintrust.dev/)

# Braintrust C# Tracing & Eval SDK

[![CI](https://github.com/braintrustdata/braintrust-sdk-java/actions/workflows/ci.yml/badge.svg)](https://github.com/braintrustdata/braintrust-sdk-dotnet/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Braintrust.Sdk.svg)](https://www.nuget.org/packages/Braintrust.Sdk)

## Overview

This library provides tools for **evaluating** and **tracing** AI applications in [Braintrust](https://www.braintrust.dev). Use it to:

- **Evaluate** your AI models with custom test cases and scoring functions
- **Trace** LLM calls and monitor AI application performance with OpenTelemetry
- **Integrate** seamlessly with OpenAI, Anthropic, Microsoft Agent Framework, and other LLM providers

This SDK is currently in BETA status and APIs may change.

## Installation

The SDK is split into packages by LLM provider integration. Install the core package plus any provider integrations you need.

### Core package

```bash
dotnet add package Braintrust.Sdk
```

### OpenAI integration

```bash
dotnet add package Braintrust.Sdk.OpenAI
```

### Anthropic integration

```bash
dotnet add package Braintrust.Sdk.Anthropic
```

### Microsoft Agent Framework integration

```bash
dotnet add package Braintrust.Sdk.AgentFramework
```

### Or add to your .csproj file

```xml
<ItemGroup>
  <PackageReference Include="Braintrust.Sdk" Version="version goes here" />
  <PackageReference Include="Braintrust.Sdk.OpenAI" Version="version goes here" />          <!-- optional -->
  <PackageReference Include="Braintrust.Sdk.Anthropic" Version="version goes here" />        <!-- optional -->
  <PackageReference Include="Braintrust.Sdk.AgentFramework" Version="version goes here" />   <!-- optional -->
</ItemGroup>
```

## Datasets

Evals can read their cases straight from a Braintrust dataset instead of being written out by
hand:

```csharp
using Braintrust.Sdk.Eval;

var braintrust = Braintrust.Get();

// Reads from the configured default project. Resolves the dataset id now; rows are fetched a
// page at a time as the eval reads them.
var dataset = await braintrust.FetchDatasetAsync<string, string>("my-dataset");

using var eval = await braintrust.EvalBuilder<string, string>()
    .Name("my-eval")
    .Dataset(dataset)
    .TaskFunction(input => Classify(input))
    .Scorers(new FunctionScorer<string, string>("exact", (expected, actual) => expected == actual ? 1.0 : 0.0))
    .BuildAsync();

await eval.RunAsync();
```

Disposing the eval closes the HTTP client it opened for itself. An API client you pass to the
builder yourself is left alone - it stays yours to dispose.

Each case's `input` and `expected` are deserialized into the type arguments you pick, so
`FetchDatasetAsync<Question, Answer>("my-dataset")` gives you typed cases.

`expected` is optional in a dataset, and the type arguments are not nullable, so rows the default
deserializer cannot handle need an `inputConverter`/`expectedConverter` to say what a missing or
oddly-shaped field means:

```csharp
var dataset = await braintrust.FetchDatasetAsync<string, string>(
    "my-dataset",
    expectedConverter: e => e.ValueKind == JsonValueKind.Null ? "" : e.GetString()!);
```

Passing `version` pins the read to a transaction id. Leaving it null - the default - resolves the
latest version when enumeration starts and reads every page as of that one version, so a dataset
written to mid-run still produces a consistent eval. Either way the experiment records which
dataset and version it ran against, and each eval row links back to the dataset record it came
from.

For a dataset outside the configured project, or one you already have the id of, use
`Dataset.FetchFromBraintrustAsync<...>(apiClient, projectName, datasetName)` and
`Dataset.FromId<...>(apiClient, datasetId)`. These factories require a caller-owned API client and
never dispose it.

## Low-level API client

Beyond evals and tracing, the SDK ships a client for the full [Braintrust REST API](https://api.braintrust.dev),
generated from Braintrust's public OpenAPI spec:

```csharp
using var client = BraintrustOpenApiClient.Of(BraintrustConfig.FromEnvironment());
var api = client.Api;   // every Braintrust REST endpoint
```

See [docs/api-client.md](./docs/api-client.md) for the walkthrough, and
[examples/ApiClientExample](./examples/ApiClientExample) for a runnable example.

## Running Examples

### Setup

Install the dotnet 8 framework

- Macos: `brew install dotnet-sdk@8`
- Linux: Follow [these instructions](https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-install?tabs=dotnet10&pivots=os-linux-ubuntu-2404)
- Windows: Follow [these instructions](https://learn.microsoft.com/en-us/dotnet/core/install/windows)

### List All Examples

```bash
ls -l examples/
# >>> outputs
 AgentFrameworkInstrumentation/
 EvalExample/
 OpenAIInstrumentation/
 SimpleOpenTelemetry/
 ... # rest of the examples
```

### Run An Example

```bash
dotnet run --project examples/SimpleOpenTelemetry
```
