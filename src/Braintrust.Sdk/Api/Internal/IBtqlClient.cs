using System.Text.Json;

namespace Braintrust.Sdk.Api.Internal;

internal interface IBtqlClient : IDisposable
{
    Task<IReadOnlyList<IReadOnlyDictionary<string, JsonElement>>> QuerySpansAsync(
        string experimentId, string rootSpanId, CancellationToken cancellationToken = default);
}
