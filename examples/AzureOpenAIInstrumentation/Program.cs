using System;
using System.Threading.Tasks;
using Azure.AI.OpenAI;
using Braintrust.Sdk;
using Braintrust.Sdk.AzureOpenAI;
using OpenAI.Chat;

namespace Braintrust.Sdk.Examples.AzureOpenAIInstrumentation;

/// <summary>
/// Basic example demonstrating Azure OpenAI instrumentation with Braintrust.
///
/// Required environment variables:
///   AZURE_OPENAI_ENDPOINT  - Your Azure OpenAI resource endpoint
///                            e.g. https://my-resource.openai.azure.com
///   AZURE_OPENAI_API_KEY   - Your Azure OpenAI API key
///   AZURE_OPENAI_DEPLOYMENT - The model deployment name to use
///                            e.g. gpt-4o-mini
/// </summary>
class Program
{
    static async Task Main(string[] args)
    {
        var endpointStr = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var deploymentName = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4o-mini";

        if (string.IsNullOrEmpty(endpointStr))
        {
            Console.WriteLine("ERROR: AZURE_OPENAI_ENDPOINT environment variable not set. Bailing.");
            return;
        }

        if (string.IsNullOrEmpty(apiKey))
        {
            Console.WriteLine("ERROR: AZURE_OPENAI_API_KEY environment variable not set. Bailing.");
            return;
        }

        var endpoint = new Uri(endpointStr);
        var braintrust = Braintrust.Get();
        var activitySource = braintrust.GetActivitySource();

        // Option 1: Use WrapAzureOpenAI to create a new instrumented client directly
        var instrumentedClient = BraintrustAzureOpenAI.WrapAzureOpenAI(activitySource, endpoint, apiKey);

        // Option 2: Wrap an existing AzureOpenAIClient using the extension method
        // var rawClient = new AzureOpenAIClient(endpoint, new System.ClientModel.ApiKeyCredential(apiKey));
        // var instrumentedClient = rawClient.WithBraintrust(activitySource);

        // Option 3: Use Braintrust-managed ActivitySource (requires Braintrust to be configured first)
        // var instrumentedClient = rawClient.WithBraintrust();

        using (var rootActivity = activitySource.StartActivity("azure-openai-dotnet-instrumentation-example"))
        {
            if (rootActivity != null)
            {
                await ChatCompletionsExample(instrumentedClient, deploymentName);
                var url = await braintrust.GetProjectUriAsync()
                    + $"/logs?r={rootActivity.TraceId}&s={rootActivity.SpanId}";
                Console.WriteLine($"\n\n  Example complete! View your data in Braintrust: {url}\n");
            }
        }
    }

    private static async Task ChatCompletionsExample(AzureOpenAIClient azureClient, string deploymentName)
    {
        Console.WriteLine($"\n~~~ CHAT COMPLETIONS EXAMPLE (deployment: {deploymentName})\n");

        // Use the Azure deployment name (not the model name)
        var chatClient = azureClient.GetChatClient(deploymentName);

        var messages = new ChatMessage[]
        {
            new SystemChatMessage("You are a helpful assistant"),
            new UserChatMessage("What is the capital of France?")
        };

        var response = await chatClient.CompleteChatAsync(messages);

        Console.WriteLine($"Response: {response.Value.Content[0].Text}");
        Console.WriteLine($"Model: {response.Value.Model}");
        Console.WriteLine($"Finish Reason: {response.Value.FinishReason}");
    }
}
