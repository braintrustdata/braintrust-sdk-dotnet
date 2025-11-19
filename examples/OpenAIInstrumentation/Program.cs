using System;
using System.Threading.Tasks;
using Braintrust.Sdk;
using Braintrust.Sdk.Instrumentation.OpenAI;
using OpenAI;
using OpenAI.Chat;

namespace Braintrust.Sdk.Examples.OpenAIInstrumentation;

/// <summary>
/// Basic example demonstrating OpenAI instrumentation with Braintrust.
/// </summary>
class Program
{
    static async Task Main(string[] args)
    {
        // Check for API key
        var openAIApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrEmpty(openAIApiKey))
        {
            Console.WriteLine("ERROR: OPENAI_API_KEY environment variable not set. Bailing.");
            return;
        }

        var braintrust = Braintrust.Get();
        var activitySource = braintrust.GetActivitySource();
        var instrumentedClient = BraintrustOpenAI.WrapOpenAI(activitySource, openAIApiKey);

        using (var rootActivity = activitySource.StartActivity("openai-dotnet-instrumentation-example"))
        {
            if (rootActivity != null)
            {
                await ChatCompletionsExample(instrumentedClient);
                var url = braintrust.ProjectUri()
                    + $"/logs?r={rootActivity.TraceId}&s={rootActivity.SpanId}";
                Console.WriteLine($"\n\n  Example complete! View your data in Braintrust: {url}\n");
            }
        }
    }

    private static async Task ChatCompletionsExample(OpenAIClient openAIClient)
    {
        Console.WriteLine("\n~~~ CHAT COMPLETIONS EXAMPLE\n");

        var chatClient = openAIClient.GetChatClient("gpt-4o-mini");

        var messages = new ChatMessage[]
        {
            new SystemChatMessage("You are a helpful assistant"),
            new UserChatMessage("What is the capital of France?")
        };

        var response = await chatClient.CompleteChatAsync(messages);

        Console.WriteLine($"Response: {response.Value.Content[0].Text}");
        Console.WriteLine($"Model: {response.Value.Model}");
        Console.WriteLine($"Finish Reason: {response.Value.FinishReason}");

        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < 0) // change to true if you wish to demo images
        {
            Console.WriteLine("\n~~~ MULTI-MODAL PROMPT WITH INLINE IMAGE\n");
            var demoImageBytes = Convert.FromBase64String(
              // 1x1 reg pixel PNG
              "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="
              );

            var multimodalMessages = new ChatMessage[]
            {
            new UserChatMessage(new[]
            {
                ChatMessageContentPart.CreateTextPart("Summarize what you see in this image."),
                ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(demoImageBytes), "image/png")
            })
            };

            var multimodalResponse = await chatClient.CompleteChatAsync(multimodalMessages);
            Console.WriteLine($"Vision Response: {multimodalResponse.Value.Content[0].Text}");
        }
    }
}
