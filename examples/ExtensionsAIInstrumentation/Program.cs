using Braintrust.Sdk;
using Braintrust.Sdk.Extensions.AI;
using Microsoft.Extensions.AI;

namespace Braintrust.Sdk.Examples.ExtensionsAIInstrumentation;

/// <summary>
/// Example demonstrating Braintrust instrumentation via Microsoft.Extensions.AI IChatClient.
/// Works with any provider: OpenAI, Azure OpenAI, Ollama, etc.
///
/// Spans emitted include both braintrust.* (for Braintrust dashboard) and gen_ai.*
/// (OTEL GenAI semantic conventions) attributes.
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

        // Create an IChatClient from any provider — here using OpenAI via M.E.AI adapter
        var openAIClient = new OpenAI.OpenAIClient(openAIApiKey);
        IChatClient chatClient = openAIClient.GetChatClient("gpt-4o-mini").AsIChatClient();

        // Add Braintrust tracing at both LLM and function levels
        var tracedClient = new ChatClientBuilder(chatClient)
            .UseAllBraintrustTracing(activitySource)
            .Build();

        // Define a tool
        var getWeather = AIFunctionFactory.Create(
            (string city) => $"The weather in {city} is sunny, 72°F.",
            "GetWeather",
            "Gets the current weather for a city.");

        using (var rootActivity = activitySource.StartActivity("extensions-ai-instrumentation-example"))
        {
            if (rootActivity != null)
            {
                Console.WriteLine("~~~ EXTENSIONS.AI INSTRUMENTATION EXAMPLE\n");

                // Non-streaming call with tool use
                var response = await tracedClient.GetResponseAsync(
                    [new ChatMessage(ChatRole.User, "What's the weather like in Seattle?")],
                    new ChatOptions { Tools = [getWeather] });

                Console.WriteLine($"Response: {response.Text}");

                // Streaming call
                Console.Write("\nStreaming: ");
                await foreach (var update in tracedClient.GetStreamingResponseAsync(
                    [new ChatMessage(ChatRole.User, "Tell me a joke.")]))
                {
                    Console.Write(update.Text);
                }
                Console.WriteLine();

                // Print Braintrust link
                var url = await braintrust.GetProjectUriAsync()
                    + $"/logs?r={rootActivity.TraceId}&s={rootActivity.SpanId}";
                Console.WriteLine($"\n  View your trace in Braintrust: {url}\n");
            }
        }
    }
}
