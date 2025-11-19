using System;
using System.Linq;
using Braintrust.Sdk;
using Braintrust.Sdk.Eval;
using Braintrust.Sdk.Instrumentation.OpenAI;
using OpenAI;
using OpenAI.Chat;

namespace Braintrust.Sdk.Examples.EvalExample;

class Program
{
    static void Main(string[] args)
    {
        var openAIApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrEmpty(openAIApiKey))
        {
            Console.WriteLine("ERROR: OPENAI_API_KEY environment variable not set. Bailing.");
            return;
        }

        var braintrust = Braintrust.Get();
        var activitySource = braintrust.GetActivitySource();
        OpenAIClient openAIClient = BraintrustOpenAI.WrapOpenAI(activitySource, openAIApiKey);

        // Define the task function that uses OpenAI to classify food
        string GetFoodType(string food)
        {
            var chatClient = openAIClient.GetChatClient("gpt-4o-mini");
            var messages = new ChatMessage[]
            {
                new SystemChatMessage("Return a one word answer"),
                new UserChatMessage($"What kind of food is {food}?")
            };

            var options = new ChatCompletionOptions
            {
                MaxOutputTokenCount = 50,
                Temperature = 0.0f
            };

            var response = chatClient.CompleteChat(messages, options);
            return response.Value.Content[0].Text.ToLower();
        }

        // Create and run the evaluation
        var eval = braintrust
            .EvalBuilder<string, string>()
            .Name($"dotnet-eval-x-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}")
            .Cases(
                DatasetCase<string, string>.Of("strawberry", "fruit"),
                DatasetCase<string, string>.Of("asparagus", "vegetable"),
                DatasetCase<string, string>.Of("apple", "fruit"),
                DatasetCase<string, string>.Of("banana", "fruit")
            )
            .TaskFunction(GetFoodType)
            .Scorers(
                Scorer<string, string>.Of("fruit_scorer", result => result == "fruit" ? 1.0 : 0.0),
                Scorer<string, string>.Of("vegetable_scorer", result => result == "vegetable" ? 1.0 : 0.0)
            )
            .Build();

        var result = eval.Run();
        Console.WriteLine($"\n\n{result.CreateReportString()}");
    }
}
