using System.Text.Json;
using Braintrust.Sdk.Eval;
using Braintrust.Sdk.Instrumentation.OpenAI;
using OpenAI;
using OpenAI.Chat;

/// <summary>
/// Demonstrates trace-aware scoring: a scorer that examines the intermediate LLM spans
/// of a multi-call task, not just the final output.
///
/// The task counts how many times each fruit appears in an input list by making one
/// LLM call per fruit. The scorer uses the trace to validate that each individual
/// LLM call returned a numeric answer.
///
/// Requirements:
///   BRAINTRUST_API_KEY  - your Braintrust API key
///   OPENAI_API_KEY      - your OpenAI API key
/// </summary>
namespace Braintrust.Sdk.Examples.TraceScoring;

class Program
{
    static async Task Main(string[] args)
    {
        var openAIApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrEmpty(openAIApiKey))
        {
            Console.WriteLine("ERROR: OPENAI_API_KEY environment variable not set.");
            return;
        }

        var braintrust = Braintrust.Get();
        var activitySource = braintrust.GetActivitySource();
        OpenAIClient openAIClient = BraintrustOpenAI.WrapOpenAI(activitySource, openAIApiKey);

        // Task: given a comma-separated list of fruits, count each type via individual LLM calls
        Dictionary<string, int> CountFruits(string fruitList)
        {
            var fruits = fruitList.Split(',').Select(f => f.Trim().ToLowerInvariant()).Distinct().ToList();
            var counts = new Dictionary<string, int>();
            var chatClient = openAIClient.GetChatClient("gpt-4o-mini");

            foreach (var fruit in fruits)
            {
                var messages = new ChatMessage[]
                {
                    new SystemChatMessage("Return only a single integer, nothing else."),
                    new UserChatMessage($"How many times does '{fruit}' appear in this list: {fruitList}?")
                };

                var options = new ChatCompletionOptions
                {
                    MaxOutputTokenCount = 10,
                    Temperature = 0.0f
                };

                var response = chatClient.CompleteChat(messages, options);
                var text = response.Value.Content[0].Text.Trim();
                counts[fruit] = int.TryParse(text, out var n) ? n : 0;
            }

            return counts;
        }

        // Scorer: uses the trace to verify each LLM call returned a numeric string
        var traceScorer = new FruitTraceScorer();

        var eval = await braintrust
            .EvalBuilder<string, Dictionary<string, int>>()
            .Name($"trace-scoring-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}")
            .Tags("trace-scoring", "dotnet-sdk", "multi-call")
            .Cases(
                new DatasetCase<string, Dictionary<string, int>>(
                    "apple, banana, apple, orange, apple",
                    new Dictionary<string, int> { ["apple"] = 3, ["banana"] = 1, ["orange"] = 1 }
                ),
                new DatasetCase<string, Dictionary<string, int>>(
                    "cherry, mango, cherry",
                    new Dictionary<string, int> { ["cherry"] = 2, ["mango"] = 1 }
                )
            )
            .TaskFunction(CountFruits)
            .Scorers(
                new FunctionScorer<string, Dictionary<string, int>>("exact_match", (expected, actual) =>
                {
                    if (expected.Count != actual.Count) return 0.0;
                    foreach (var (fruit, count) in expected)
                    {
                        if (!actual.TryGetValue(fruit, out var actualCount) || actualCount != count)
                            return 0.0;
                    }
                    return 1.0;
                }),
                traceScorer
            )
            .BuildAsync();

        var result = await eval.RunAsync();
        Console.WriteLine($"\n\n{result.CreateReportString()}");
    }
}

/// <summary>
/// A traced scorer that examines intermediate LLM call spans to verify each call
/// returned a numeric response. Demonstrates ITracedScorer usage.
/// </summary>
class FruitTraceScorer : ITracedScorer<string, Dictionary<string, int>>
{
    public string Name => "llm_calls_valid";

    public IReadOnlyList<Score> Score(TaskResult<string, Dictionary<string, int>> taskResult)
    {
        // Fallback when no trace is available
        return [new Score(Name, 0.5)];
    }

    public async Task<IReadOnlyList<Score>> ScoreAsync(
        TaskResult<string, Dictionary<string, int>> taskResult,
        EvalTrace trace)
    {
        var llmSpans = await trace.GetSpansAsync("llm");

        if (llmSpans.Count == 0)
        {
            // No LLM spans found — cannot validate
            return [new Score(Name, 0.0)];
        }

        int validCalls = 0;
        foreach (var span in llmSpans)
        {
            // Check output: look for a numeric response in choices[0].message.content
            if (span.TryGetValue("output", out var outputEl)
                && outputEl.ValueKind == JsonValueKind.Object
                && outputEl.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.TryGetProperty("message", out var msg)
                        && msg.TryGetProperty("content", out var content)
                        && int.TryParse(content.GetString()?.Trim(), out _))
                    {
                        validCalls++;
                        break;
                    }
                }
            }
        }

        var score = llmSpans.Count > 0 ? (double)validCalls / llmSpans.Count : 0.0;
        return [new Score(Name, score)];
    }
}
