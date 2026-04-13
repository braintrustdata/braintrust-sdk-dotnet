# Braintrust.Sdk.Extensions.AI

Braintrust tracing instrumentation for any `IChatClient` implementation via Microsoft.Extensions.AI.

## Usage

```csharp
using Braintrust.Sdk.Extensions.AI;

var chatClient = new ChatClientBuilder(innerClient)
    .UseBraintrustTracing(activitySource)
    .UseBraintrustFunctionTracing(activitySource)
    .Build();
```

Works with any provider that implements `IChatClient`: OpenAI, Azure OpenAI, Ollama, etc.
