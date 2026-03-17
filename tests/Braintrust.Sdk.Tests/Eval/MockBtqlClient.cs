using System.Text.Json;
using Braintrust.Sdk.Api.Internal;

namespace Braintrust.Sdk.Tests.Eval;

/// <summary>
/// Mock BTQL client for testing that returns pre-configured span data.
/// </summary>
internal class MockBtqlClient : IBtqlClient
{
    private readonly IReadOnlyList<IReadOnlyDictionary<string, JsonElement>> _spans;
    public int QueryCount { get; private set; }
    public string? LastExperimentId { get; private set; }
    public string? LastRootSpanId { get; private set; }

    public MockBtqlClient(IReadOnlyList<IReadOnlyDictionary<string, JsonElement>>? spans = null)
    {
        _spans = spans ?? Array.Empty<IReadOnlyDictionary<string, JsonElement>>();
    }

    public Task<IReadOnlyList<IReadOnlyDictionary<string, JsonElement>>> QuerySpansAsync(
        string experimentId, string rootSpanId, CancellationToken cancellationToken = default)
    {
        QueryCount++;
        LastExperimentId = experimentId;
        LastRootSpanId = rootSpanId;
        return Task.FromResult(_spans);
    }

    /// <summary>
    /// Helper to build a span dictionary from JSON for tests.
    /// </summary>
    public static IReadOnlyDictionary<string, JsonElement> MakeSpan(string type, object? input = null, object? output = null)
    {
        var obj = new Dictionary<string, object?>
        {
            ["span_id"] = Guid.NewGuid().ToString(),
            ["span_attributes"] = new { type }
        };
        if (input != null) obj["input"] = input;
        if (output != null) obj["output"] = output;

        var json = System.Text.Json.JsonSerializer.Serialize(obj);
        var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone());
    }
}
