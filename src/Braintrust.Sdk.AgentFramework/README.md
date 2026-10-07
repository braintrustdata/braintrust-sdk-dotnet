# Braintrust.Sdk.AgentFramework

Braintrust instrumentation for the [Microsoft Agent Framework](https://github.com/microsoft/agent-framework).

Provides tracing at three pipeline levels:

- **Agent middleware** - wraps `RunAsync`/`RunStreamingAsync` to capture full agent invocations
- **Chat client middleware** - wraps `IChatClient` calls to capture LLM prompts, completions, and token usage
- **Function middleware** - wraps tool/function invocations to capture arguments, results, and timing

## Usage

```csharp
using Braintrust.Sdk.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

// Agent-level tracing
var tracedAgent = agent.WithBraintrustAgentTracing();

// Chat client-level (LLM) tracing
var chatClient = new ChatClientBuilder(innerClient)
    .UseBraintrustLLMTracing()
    .Build();

// Function call tracing
var tracedAgent = agent.AsBuilder()
    .UseBraintrustFunctionTracing()
    .Build();

// All three combined
var chatClient = new ChatClientBuilder(innerClient)
    .UseBraintrustLLMTracing()
    .Build();
var agent = new ChatClientAgent(chatClient, name: "MyAgent", tools: tools)
    .AsBuilder()
    .UseBraintrustFunctionTracing()
    .Build()
    .WithBraintrustAgentTracing();
```

Each extension also accepts an explicit `ActivitySource`. Use
`captureMessageContent: false` on agent/LLM tracing and
`captureToolArguments: false` on function tracing to omit content from spans.

## Migrating from `UseBraintrustTracing`

**Breaking change:** `ChatClientBuilder.UseBraintrustTracing` has been removed,
and `UseBraintrustFunctionTracing` now extends `AIAgentBuilder`, not
`ChatClientBuilder`. Replace the old combined call with
`UseBraintrustLLMTracing` on the model client, then add
`UseBraintrustFunctionTracing` to the agent builder as shown above.

Function tracing uses Microsoft Agent Framework's function-invocation middleware
and calls its continuation. It does not install a `FunctionInvokingChatClient`,
enable automatic tool execution, or change tool-execution settings. The agent
remains responsible for composing its tool loop and history-persistence pipeline.

This supports local `InMemoryChatHistoryProvider` history with
`RequirePerServiceCallChatHistoryPersistence = true`, including streaming tool
calls. Previously, the extra tool loop installed by Braintrust could bypass
MAF's history-persistence decorator and forward the internal
`_agent_local_chat_history` sentinel to the model provider
([#69](https://github.com/braintrustdata/braintrust-sdk-dotnet/issues/69)).

For a custom pipeline using `UseProvidedChatClientAsIs`, the application remains
responsible for supplying function invocation and placing MAF's history
persistence decorator inside that loop. Tracing does not repair custom pipeline
ordering.
