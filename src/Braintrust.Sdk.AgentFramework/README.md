# Braintrust.Sdk.AgentFramework

Braintrust instrumentation for the [Microsoft Agent Framework](https://github.com/microsoft/agent-framework).

Provides tracing at three pipeline levels:

- **Agent middleware** - wraps `RunAsync`/`RunStreamingAsync` to capture full agent invocations
- **Chat client middleware** - wraps `IChatClient` calls to capture LLM prompts, completions, and token usage
- **Function middleware** - wraps tool/function invocations to capture arguments, results, and timing

## Usage

```csharp
using Braintrust.Sdk.AgentFramework;

// Agent-level tracing
var tracedAgent = agent.WithBraintrustTracing();

// Chat client-level tracing
var chatClient = new ChatClientBuilder(innerClient)
    .UseBraintrustTracing()
    .Build();

// Function call tracing
var chatClient = new ChatClientBuilder(innerClient)
    .UseBraintrustFunctionTracing()
    .Build();

// All three combined
var chatClient = new ChatClientBuilder(innerClient)
    .UseBraintrustTracing()
    .UseBraintrustFunctionTracing()
    .Build();
var agent = new ChatClientAgent(chatClient, "MyAgent")
    .WithBraintrustTracing();
```
