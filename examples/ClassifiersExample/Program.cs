using Braintrust.Sdk.Eval;

namespace Braintrust.Sdk.Examples.ClassifiersExample;

// Example: Classifiers
//
// Classifiers categorize and label eval outputs. Unlike scorers (which return
// numeric 0-1 values), classifiers return structured Classification items —
// each with an Id, an optional Label, and optional Metadata.
//
// Results are stored as a dictionary keyed by classifier name:
//
//   { "sentiment": [{ id: "positive", label: "Positive" }] }
//
// Three patterns are shown:
//
//   1. Inline single-label FunctionClassifier
//   2. Inline multi-label FunctionClassifier (returns IReadOnlyList<Classification>)
//   3. Class-based classifier implementing IClassifier<TInput, TOutput>
//
// Classifiers and scorers run independently. You can use both together, or
// use only classifiers when you don't need numeric scores.

sealed class ResponseQualityClassifier : IClassifier<string, string>
{
    public string Name => "response_quality";

    public Task<IReadOnlyList<Classification>> Classify(TaskResult<string, string> taskResult)
    {
        var output = taskResult.Result;
        var wordCount = output.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        string id;
        if (string.IsNullOrWhiteSpace(output))
        {
            id = "no_response";
        }
        else if (wordCount < 5)
        {
            id = "too_short";
        }
        else if (output.Contains("immediately", StringComparison.OrdinalIgnoreCase)
            || output.Contains("right away", StringComparison.OrdinalIgnoreCase)
            || output.Contains("look into", StringComparison.OrdinalIgnoreCase))
        {
            id = "action_oriented";
        }
        else
        {
            id = "informational";
        }

        var label = char.ToUpperInvariant(id[0]) + id[1..].Replace('_', ' ');

        IReadOnlyList<Classification> results = new[]
        {
            new Classification(
                id,
                Label: label,
                Metadata: new Dictionary<string, object> { ["word_count"] = wordCount })
        };
        return Task.FromResult(results);
    }
}

class Program
{
    private static readonly (string Input, string Expected)[] Messages =
    {
        ("Hi! I just wanted to say thank you, the product is amazing!", "praise"),
        ("I've been waiting 2 weeks for my order. This is unacceptable!", "follow_up"),
        ("How do I reset my password? I can't find the option anywhere.", "how_to"),
        ("The item arrived damaged. I need a refund immediately.", "complaint"),
        ("Just checking in — any update on my ticket #4821?", "follow_up")
    };

    static string GenerateResponse(string message)
    {
        if (Regex("thank").IsMatch(message))
            return "You're welcome! So glad you're enjoying it.";
        if (Regex("waiting|order").IsMatch(message))
            return "I sincerely apologise for the delay. Let me look into this right away.";
        if (Regex("password|reset").IsMatch(message))
            return "To reset your password, go to Settings > Account > Reset Password.";
        if (Regex("damaged|refund").IsMatch(message))
            return "I'm sorry to hear that. I'll process your refund immediately.";
        return "Thanks for reaching out! Let me check on that for you.";
    }

    static System.Text.RegularExpressions.Regex Regex(string pattern)
        => new(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    static async Task Main()
    {
        var braintrust = Braintrust.Get();

        // Pattern 1: inline single-label classifier
        var intentClassifier = new FunctionClassifier<string, string>(
            "intent",
            taskResult =>
            {
                var input = taskResult.DatasetCase.Input;
                string id =
                    Regex("thank").IsMatch(input) ? "praise" :
                    Regex("waiting|order|update").IsMatch(input) ? "follow_up" :
                    Regex("password|reset|find").IsMatch(input) ? "how_to" :
                    Regex("damaged|refund").IsMatch(input) ? "complaint" :
                    "other";

                return new Classification(
                    id,
                    Label: char.ToUpperInvariant(id[0]) + id[1..].Replace('_', ' '));
            });

        // Pattern 2: inline multi-label classifier — returns a list
        var toneClassifier = new FunctionClassifier<string, string>(
            "tone",
            taskResult =>
            {
                var input = taskResult.DatasetCase.Input;
                var labels = new List<Classification>();
                if (Regex("immediately|unacceptable|waiting").IsMatch(input))
                    labels.Add(new Classification("urgent", Label: "Urgent"));
                if (Regex("please|thank|just checking").IsMatch(input))
                    labels.Add(new Classification("polite", Label: "Polite"));
                if (Regex("unacceptable|damaged|waiting").IsMatch(input))
                    labels.Add(new Classification("frustrated", Label: "Frustrated"));
                if (labels.Count == 0)
                    labels.Add(new Classification("neutral", Label: "Neutral"));
                return (IReadOnlyList<Classification>)labels;
            });

        // Pattern 3: class-based classifier (see ResponseQualityClassifier above)
        var qualityClassifier = new ResponseQualityClassifier();

        var cases = Messages
            .Select(m => DatasetCase.Of(m.Input, m.Expected))
            .ToArray();

        var eval = await braintrust
            .EvalBuilder<string, string>()
            .Name($"dotnet-classifiers-example-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}")
            .Tags("classifiers-example", "dotnet-sdk")
            .Cases(cases)
            .TaskFunction(GenerateResponse)
            .Classifiers(intentClassifier, toneClassifier, qualityClassifier)
            .BuildAsync();

        var result = await eval.RunAsync();
        Console.WriteLine($"\n\n{result.CreateReportString()}");
    }
}
