using System.Text.Json;
using System.Text.Json.Nodes;
using Braintrust.Sdk.Trace.Protos.Collector.Trace.V1;
using Braintrust.Sdk.Trace.Protos.Trace.V1;
using Google.Protobuf;

namespace Braintrust.Sdk.Trace;

/// <summary>
/// Bridges protobuf JSON and OTLP JSON, whose span and link IDs use hex rather
/// than base64. Other bytes remain base64 and 64-bit integers remain strings.
/// </summary>
internal static class OtlpJsonPayload
{
    private static readonly JsonFormatter Formatter = new(
        JsonFormatter.Settings.Default.WithFormatEnumsAsIntegers(true));
    private static readonly JsonParser Parser = new(
        JsonParser.Settings.Default.WithIgnoreUnknownFields(false));

    internal static void ValidateKnownFields(ExportTraceServiceRequest batch)
    {
        var json = Formatter.Format(batch);
        if (!batch.Equals(Parser.Parse<ExportTraceServiceRequest>(json)))
        {
            throw new InvalidOperationException(
                "The OTLP request contains fields that cannot be represented losslessly as JSON.");
        }
    }

    internal static JsonObject ToSpanJson(ResourceSpans resource, ScopeSpans scope, Span span)
    {
        var payload = JsonNode.Parse(Formatter.Format(span))!.AsObject();
        ConvertIds(payload, toHex: true);

        if (resource.Resource is not null)
        {
            payload["resource"] = JsonNode.Parse(Formatter.Format(resource.Resource));
        }
        if (scope.Scope is not null)
        {
            payload["scope"] = JsonNode.Parse(Formatter.Format(scope.Scope));
        }
        if (resource.SchemaUrl.Length != 0)
        {
            payload["resourceSchemaUrl"] = resource.SchemaUrl;
        }
        if (scope.SchemaUrl.Length != 0)
        {
            payload["scopeSchemaUrl"] = scope.SchemaUrl;
        }

        return payload;
    }

    internal static ResourceSpans FromSpanJson(JsonObject data)
    {
        // The hook owns its tree: neither ID conversion nor regrouping may alter it.
        var span = (JsonObject)data.DeepClone();
        var resource = span["resource"];
        var scope = span["scope"];
        var resourceSchemaUrl = span["resourceSchemaUrl"];
        var scopeSchemaUrl = span["scopeSchemaUrl"];
        span.Remove("resource");
        span.Remove("scope");
        span.Remove("resourceSchemaUrl");
        span.Remove("scopeSchemaUrl");
        ConvertIds(span, toHex: false);

        // Parse metadata and span fields together so the protobuf parser enforces
        // their exact schemas, including unknown fields and explicit null values.
        var payload = new JsonObject
        {
            ["resource"] = resource,
            ["schemaUrl"] = resourceSchemaUrl,
            ["scopeSpans"] = new JsonArray(new JsonObject
            {
                ["scope"] = scope,
                ["schemaUrl"] = scopeSchemaUrl,
                ["spans"] = new JsonArray(span),
            }),
        };
        return Parser.Parse<ResourceSpans>(payload.ToJsonString());
    }

    private static void ConvertIds(JsonObject span, bool toHex)
    {
        ConvertId(span, "traceId", "trace_id", 16, toHex);
        ConvertId(span, "spanId", "span_id", 8, toHex);
        ConvertId(span, "parentSpanId", "parent_span_id", 8, toHex, optional: true);
        foreach (var link in Objects(span, "links"))
        {
            ConvertId(link, "traceId", "trace_id", 16, toHex);
            ConvertId(link, "spanId", "span_id", 8, toHex);
        }
    }

    private static IEnumerable<JsonObject> Objects(JsonObject parent, string name)
    {
        var node = parent[name];
        if (node is null)
        {
            yield break;
        }

        if (node is not JsonArray array)
        {
            throw new JsonException($"OTLP field '{name}' must be an array.");
        }

        foreach (var item in array)
        {
            yield return item as JsonObject
                ?? throw new JsonException($"OTLP field '{name}' must contain objects.");
        }
    }

    private static void ConvertId(
        JsonObject parent, string name, string protoName, int byteLength, bool toHex, bool optional = false)
    {
        name = FieldName(parent, name, protoName);
        var node = parent[name];
        if (optional && node is null)
        {
            return;
        }

        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            throw new JsonException($"OTLP field '{name}' must be a string.");
        }

        if (optional && text.Length == 0)
        {
            return;
        }

        if (!toHex && text.Length != byteLength * 2)
        {
            throw new JsonException($"OTLP field '{name}' must contain {byteLength * 2} hexadecimal characters.");
        }

        var bytes = toHex ? Convert.FromBase64String(text) : Convert.FromHexString(text);
        if (bytes.Length != byteLength)
        {
            throw new JsonException($"OTLP field '{name}' must contain exactly {byteLength} bytes.");
        }

        parent[name] = toHex ? Convert.ToHexString(bytes).ToLowerInvariant() : Convert.ToBase64String(bytes);
    }

    private static string FieldName(JsonObject parent, string name, string protoName)
    {
        // JsonParser accepts both names. Recognize aliases so they cannot bypass
        // ID conversion, but reject duplicate spellings of the same field.
        if (!parent.ContainsKey(protoName))
        {
            return name;
        }

        if (parent.ContainsKey(name))
        {
            throw new JsonException($"OTLP field '{name}' is specified more than once.");
        }

        return protoName;
    }
}
