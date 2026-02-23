using Braintrust.Sdk.Eval;
using Braintrust.Sdk.OpenAI;
using OpenAI;
using OpenAI.Chat;

namespace Braintrust.Sdk.Examples.EvalExample;

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
        OpenAIClient openAIClient = BraintrustOpenAI.WrapOpenAI(activitySource, openAIApiKey);

        // Define the task function that uses OpenAI to classify food
        async Task<string> GetFoodType(string food)
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

            var response = await chatClient.CompleteChatAsync(messages, options);
            return response.Value.Content[0].Text.ToLower();
        }

        // Create and run the evaluation
        var eval = await braintrust
            .EvalBuilder<string, string>()
            .Name($"dotnet-eval-x-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}")
            // Experiment-level tags and metadata (shown in the Braintrust UI for the experiment)
            .Tags("food-classifier", "dotnet-sdk", "gpt-4o-mini")
            .Metadata(new Dictionary<string, object>
            {
                { "model", "gpt-4o-mini" },
                { "description", "Classifies food items as fruit or vegetable" }
            })
            .Cases(
                DatasetCase.Of("strawberry", "fruit"),
                DatasetCase.Of("asparagus", "vegetable"),
                DatasetCase.Of("apple", "fruit"),
                // Case-level tags and metadata (shown for individual eval cases)
                DatasetCase.Of(
                    "banana",
                    "fruit",
                    new List<string> { "tropical", "yellow" },
                    new Dictionary<string, object> { { "category", "tropical-fruit" }, { "ripeness", "ripe" } }
                )
            )
            .TaskFunction(GetFoodType)
            .Scorers(
              new FunctionScorer<string, string>("exact_match", (expected, actual) => expected == actual ? 1.0 : 0.0),
              new FunctionScorer<string, string>("close_enough_match", (expected, actual) => expected.Trim().ToLowerInvariant() == actual.Trim().ToLowerInvariant() ? 1.0 : 0.0)
            )
            .BuildAsync();

        var result = await eval.RunAsync();
        Console.WriteLine($"\n\n{result.CreateReportString()}");
    }
}
