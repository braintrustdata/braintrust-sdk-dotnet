# Braintrust C# Tracing & Eval SDK

[![CI](https://github.com/braintrustdata/braintrust-sdk-java/actions/workflows/ci.yml/badge.svg)](https://github.com/braintrustdata/braintrust-sdk-dotnet/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Braintrust.Sdk.svg)](https://www.nuget.org/packages/Braintrust.Sdk)

## Overview

This library provides tools for **evaluating** and **tracing** AI applications in [Braintrust](https://www.braintrust.dev). Use it to:

- **Evaluate** your AI models with custom test cases and scoring functions
- **Trace** LLM calls and monitor AI application performance with OpenTelemetry
- **Integrate** seamlessly with OpenAI and other LLM providers

This SDK is currently in BETA status and APIs may change.

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
 EvalExample/
 OpenAIInstrumentation/
 SimpleOpenTelemetry/
 ... # rest of the examples
```

### Run An Example

```bash
dotnet run --project examples/SimpleOpenTelemetry
```
