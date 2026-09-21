using System.Text.Json.Nodes;

namespace Braintrust.Sdk.Trace;

/// <summary>
/// Transforms detached outgoing span data, never the application's Activity.
/// Hooks run synchronously in registration order for each completed span.
/// </summary>
public interface ISpanCustomizer
{
    /// <summary>
    /// Returns the supplied span or a replacement, for example from <see cref="JsonNode.DeepClone"/>.
    /// The object contains OTLP span fields, plus optional resource, scope, resourceSchemaUrl
    /// and scopeSchemaUrl metadata. Metadata changes apply only to this span.
    /// traceId, spanId and parentSpanId must remain unchanged. IDs are hexadecimal strings,
    /// enums are integers and 64-bit integers are decimal strings. Null, exceptions, invalid
    /// data or changed IDs fail the entire batch before transmission. Do not retain and
    /// mutate the object after returning.
    /// </summary>
    JsonObject OnSpanExport(JsonObject span) => span;
}
