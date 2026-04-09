using Braintrust.Sdk;
using Braintrust.Sdk.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Braintrust.Sdk.Examples.AgentFrameworkInstrumentation;

/// <summary>
/// Example demonstrating Microsoft Agent Framework instrumentation with Braintrust.
/// Shows tracing at agent-level, chat client-level, and function-level.
/// </summary>
class Program
{
    static async Task Main(string[] args)
    {
        var openAIApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrEmpty(openAIApiKey))
        {
            Console.WriteLine("ERROR: OPENAI_API_KEY environment variable not set. Bailing.");
            return;
        }

        var braintrust = Braintrust.Get();
        var activitySource = braintrust.GetActivitySource();

        // Build an IChatClient with Braintrust tracing at the LLM and function levels
        var openAIClient = new OpenAI.OpenAIClient(openAIApiKey);
        var chatClient = openAIClient.GetChatClient("gpt-4o-mini").AsIChatClient()
            .AsBuilder()
            .UseAllBraintrustTracing(activitySource)       // LLM + function-level tracing
            .Build();

        // Define a tool
        var getWeather = AIFunctionFactory.Create(
            (string city) => $"The weather in {city} is sunny, 72°F.",
            "GetWeather",
            "Gets the current weather for a city.");

        // Create an agent with Braintrust tracing
        var agent = new ChatClientAgent(
                chatClient,
                instructions: "You are a helpful assistant. Use tools when appropriate.",
                name: "WeatherAgent",
                tools: [getWeather])
            .WithBraintrustTracing(activitySource); // Agent-level tracing

        using (var rootActivity = activitySource.StartActivity("agent-framework-instrumentation-example"))
        {
            if (rootActivity != null)
            {
                // Run the agent
                Console.WriteLine("~~~ AGENT FRAMEWORK INSTRUMENTATION EXAMPLE\n");

                var session = await agent.CreateSessionAsync();
                var response = await agent.RunAsync("What's the weather like in Seattle?", session);

                Console.WriteLine($"Agent response: {response.Text}");
                Console.WriteLine($"Messages: {response.Messages.Count}");

                // Print Braintrust link
                var url = await braintrust.GetProjectUriAsync()
                    + $"/logs?r={rootActivity.TraceId}&s={rootActivity.SpanId}";
                Console.WriteLine($"\n  View your trace in Braintrust: {url}\n");
            }
        }
    }
}
