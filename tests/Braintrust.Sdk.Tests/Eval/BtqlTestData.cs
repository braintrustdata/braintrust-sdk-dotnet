using System.Text.Json;

namespace Braintrust.Sdk.Tests.Eval;

internal static class BtqlTestData
{
    internal static IReadOnlyDictionary<string, JsonElement> MakeSpan(
        string type,
        object? input = null,
        object? output = null)
    {
        var span = new Dictionary<string, object?>
        {
            ["span_id"] = Guid.NewGuid().ToString(),
            ["span_attributes"] = new { type },
        };
        if (input != null) span["input"] = input;
        if (output != null) span["output"] = output;

        return JsonSerializer.SerializeToElement(span)
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone());
    }
}
