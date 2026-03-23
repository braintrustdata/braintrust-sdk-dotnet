using System.Text.Json;
using Braintrust.Sdk.Eval;

namespace Braintrust.Sdk.Tests.Eval;

public class EvalTraceTest
{
    [Fact]
    public async Task GetThreadAsync_ReturnsChronologicalMessageThread()
    {
        // Two sequential LLM calls: first call has a system + user message and an assistant reply;
        // second call extends the conversation with a new user message and another assistant reply.
        var span1 = MakeLlmSpan(
            startTime: 1.0,
            messages: [
                new { role = "system", content = "You are helpful." },
                new { role = "user",   content = "What is 2+2?" }
            ],
            reply: new { role = "assistant", content = "4" }
        );

        var span2 = MakeLlmSpan(
            startTime: 2.0,
            messages: [
                new { role = "system",    content = "You are helpful." },
                new { role = "user",      content = "What is 2+2?" },
                new { role = "assistant", content = "4" },
                new { role = "user",      content = "And 3+3?" }
            ],
            reply: new { role = "assistant", content = "6" }
        );

        var trace = new EvalTrace(_ => Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, JsonElement>>>(
            [span1, span2]));

        var thread = await trace.GetThreadAsync();

        // Expected reconstruction:
        //   system "You are helpful."
        //   user   "What is 2+2?"
        //   assistant "4"          (output of span1)
        //   user   "And 3+3?"      (new input in span2)
        //   assistant "6"          (output of span2)
        Assert.Equal(5, thread.Count);
        Assert.Equal("system",    thread[0]["role"]);
        Assert.Equal("user",      thread[1]["role"]);
        Assert.Equal("assistant", thread[2]["role"]);
        Assert.Equal("4",         thread[2]["content"]);
        Assert.Equal("user",      thread[3]["role"]);
        Assert.Equal("And 3+3?",  thread[3]["content"]);
        Assert.Equal("assistant", thread[4]["role"]);
        Assert.Equal("6",         thread[4]["content"]);
    }

    [Fact]
    public async Task GetThreadAsync_ReturnsEmptyForNoLlmSpans()
    {
        var span = MockBtqlClient.MakeSpan("task");
        var trace = new EvalTrace(_ => Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, JsonElement>>>(
            [span]));

        var thread = await trace.GetThreadAsync();

        Assert.Empty(thread);
    }

    // Builds a span dictionary representing an LLM call with the given input messages and a reply.
    private static IReadOnlyDictionary<string, JsonElement> MakeLlmSpan(
        double startTime, object[] messages, object reply)
    {
        var obj = new Dictionary<string, object?>
        {
            ["span_attributes"] = new { type = "llm" },
            ["start_time"] = startTime,
            ["input"]  = new { messages },
            ["output"] = new { choices = new[] { new { message = reply } } }
        };
        var json = JsonSerializer.Serialize(obj);
        var doc  = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone());
    }
}
