using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Braintrust.Sdk.Api.Generated;

/// <summary>
/// Serializes a union whose arms have pairwise-distinct JSON kinds - for example
/// <c>string | object</c> or <c>string | array</c>. The spec normalizer turns such a
/// union into a wrapper class with one optional property per arm, named
/// <c>AsString</c>, <c>AsObject</c>, <c>AsArray</c>, <c>AsNumber</c> or
/// <c>AsBoolean</c>, and attaches this converter to it.
///
/// Reading looks at the token kind and fills the one matching property, so there is no
/// guessing between arms. Writing emits whichever property is set, unwrapped - the
/// wrapper never appears on the wire.
///
/// The arms are discovered by reflection over the wrapper's properties, so a new union
/// in the spec needs no changes here.
/// </summary>
public sealed class JsonKindUnionConverter<T> : JsonConverter<T> where T : class, new()
{
    static readonly ConcurrentDictionary<string, PropertyInfo?> Arms = new();

    static PropertyInfo? Arm(string kind) => Arms.GetOrAdd(kind, static k =>
        typeof(T).GetProperty("As" + k, BindingFlags.Public | BindingFlags.Instance));

    static string? KindOf(JsonTokenType token) => token switch
    {
        JsonTokenType.String => "String",
        JsonTokenType.Number => "Number",
        JsonTokenType.True or JsonTokenType.False => "Boolean",
        JsonTokenType.StartArray => "Array",
        JsonTokenType.StartObject => "Object",
        _ => null,
    };

    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;

        var kind = KindOf(reader.TokenType);
        var arm = kind is null ? null : Arm(kind);
        if (arm is null)
        {
            // No arm accepts this kind. Skipping the value keeps one unexpected field
            // from failing an entire response.
            reader.Skip();
            return new T();
        }

        var value = JsonSerializer.Deserialize(ref reader, arm.PropertyType, options);
        var result = new T();
        arm.SetValue(result, value);
        return result;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        // Structured arms first: if the generator ever emits an initializer for a scalar
        // arm, that arm reads as non-null and would otherwise win over the real value.
        foreach (var kind in new[] { "Object", "Array", "String", "Number", "Boolean" })
        {
            var arm = Arm(kind);
            var held = arm?.GetValue(value);
            if (held is null) continue;

            JsonSerializer.Serialize(writer, held, arm!.PropertyType, options);
            return;
        }

        writer.WriteNullValue();
    }
}
