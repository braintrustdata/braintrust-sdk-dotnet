using System.Text.Json;
using System.Text.Json.Serialization;

namespace Braintrust.Sdk.Api.Generated;

/// <summary>
/// Serializer configuration for the generated client, supplied through the partial hook
/// the generator leaves for exactly this purpose. Kept out of the generated file so it
/// survives regeneration.
/// </summary>
public partial class BraintrustGeneratedApiClient
{
    static partial void UpdateJsonSerializerSettings(JsonSerializerOptions settings)
    {
        // The generator writes every unset optional member as an explicit null, and the API
        // validates nullability strictly: creating an automation whose action never mentions
        // formatting_prompt was rejected with "Expected string, received null", so unset
        // members are omitted instead.
        //
        // The trade-off is that an explicit null can no longer be sent to clear a field on
        // PATCH. Braintrust's PATCH endpoints treat an absent field as "leave alone" and
        // mostly accept a sentinel (an empty value) for "clear", so this is the better
        // default; a caller that truly needs to write null can serialize that field itself.
        settings.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    }
}
