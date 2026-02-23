using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Braintrust.Sdk.Anthropic;

namespace Braintrust.Sdk.Examples.AnthropicInstrumentation;

/// <summary>
/// Basic example demonstrating Anthropic instrumentation with Braintrust.
/// </summary>
class Program
{
    static async Task Main()
    {
        var braintrust = Braintrust.Get();
        var activitySource = braintrust.GetActivitySource();

        // Create an Anthropic client as you normally would
        var anthropicClient = new AnthropicClient();

        // Wrap the client with instrumentation using the Braintrust ActivitySource
        // The instrumented client disposes the underlying client when disposed
        using var instrumentedClient = anthropicClient.WithBraintrust(activitySource);

        // Alternatively, you can let Braintrust manage the ActivitySource for you when creating the client,
        // but make sure Brainstrust is properly configured first
        // var instrumentedClient = anthropicClient.WithBraintrust();

        using (var rootActivity = activitySource.StartActivity("anthropic-dotnet-instrumentation-example"))
        {
            if (rootActivity != null)
            {
                await MessageCompletionExample(instrumentedClient);
                await MessageStreamingExample(instrumentedClient);
                var url = await braintrust.GetProjectUriAsync()
                    + $"/logs?r={rootActivity.TraceId}&s={rootActivity.SpanId}";
                Console.WriteLine($"\n\n  Example complete! View your data in Braintrust: {url}\n");
            }
        }
    }

    private static async Task MessageCompletionExample(IAnthropicClient anthropicClient)
    {
        Console.WriteLine("\n~~~ MESSAGE COMPLETION EXAMPLE\n");

        var request = new MessageCreateParams
        {
            Model = "claude-sonnet-4-20250514",
            MaxTokens = 1024,
            Messages = new List<MessageParam>
            {
                new MessageParam
                {
                    Role = "user",
                    Content = "What is the capital of France? Please answer in one sentence."
                }
            }
        };

        var response = await anthropicClient.Messages.Create(request);

        // Display response (Content is a list of ContentBlocks)
        Console.WriteLine($"Response Content: {JsonSerializer.Serialize(response.Content)}");
        Console.WriteLine($"Model: {response.Model}");
        Console.WriteLine($"Stop Reason: {response.StopReason}");
        Console.WriteLine($"Input Tokens: {response.Usage.InputTokens}");
        Console.WriteLine($"Output Tokens: {response.Usage.OutputTokens}");
    }

    private static async Task MessageStreamingExample(IAnthropicClient anthropicClient)
    {
        Console.WriteLine("\n~~~ MESSAGE STREAMING EXAMPLE\n");

        var request = new MessageCreateParams
        {
            Model = "claude-sonnet-4-20250514",
            MaxTokens = 1024,
            Messages = new List<MessageParam>
            {
                new MessageParam
                {
                    Role = "user",
                    Content = "Write a haiku about programming."
                }
            }
        };

        Console.WriteLine("Streaming response:");
        var startTime = DateTime.UtcNow;
        int eventCount = 0;

        await foreach (var rawEvent in anthropicClient.Messages.CreateStreaming(request))
        {
            eventCount++;

            if (rawEvent.TryPickContentBlockDelta(out var delta))
            {
                if (delta.Delta.TryPickText(out var textDelta))
                {
                    Console.Write(textDelta.Text);
                }
            }
        }

        Console.WriteLine();

        var totalTime = DateTime.UtcNow - startTime;
        Console.WriteLine($"Total streaming time: {totalTime.TotalMilliseconds:F2}ms");
        Console.WriteLine($"Total events received: {eventCount}");
    }
}
