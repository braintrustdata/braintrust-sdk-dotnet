using Braintrust.Sdk.Trace.Protos.Collector.Trace.V1;
using Braintrust.Sdk.Trace.Protos.Common.V1;
using Braintrust.Sdk.Trace.Protos.Resource.V1;
using Braintrust.Sdk.Trace.Protos.Trace.V1;
using OtlpSpan = Braintrust.Sdk.Trace.Protos.Trace.V1.Span;
using Google.Protobuf;

namespace Braintrust.Sdk.Trace;

/// <summary>
/// Adapts the upstream Activity-only exporter to detached per-span OTLP JSON data.
/// Hooks run before final serialization and before auth or network transmission.
/// </summary>
internal sealed class BraintrustSpanCustomizerHandler : DelegatingHandler
{
    private readonly IReadOnlyList<ISpanCustomizer> _customizers;

    internal BraintrustSpanCustomizerHandler(IReadOnlyList<ISpanCustomizer> customizers)
    {
        _customizers = customizers;
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CustomizeAsync(request, cancellationToken).GetAwaiter().GetResult();
        return base.Send(request, cancellationToken);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await CustomizeAsync(request, cancellationToken).ConfigureAwait(false);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task CustomizeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            var original = request.Content ?? throw new InvalidOperationException("Missing OTLP request content.");
            if (original.Headers.ContentType?.MediaType != "application/x-protobuf" ||
                original.Headers.ContentEncoding.Count != 0)
            {
                throw new InvalidOperationException("Span customization requires uncompressed OTLP protobuf content.");
            }

            var bytes = await original.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var batch = ExportTraceServiceRequest.Parser.ParseFrom(bytes);
            batch = CustomizeBatch(batch);

            var replacement = new ByteArrayContent(batch.ToByteArray());
            try
            {
                foreach (var header in original.Headers)
                {
                    if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
                request.Content = replacement;
            }
            catch
            {
                replacement.Dispose();
                throw;
            }
            original.Dispose();
        }
        catch (Exception ex)
        {
            // Never log the snapshot or exception message, which may contain secrets.
            Console.Error.WriteLine($"[Braintrust] Span customization failed; no spans sent ({ex.GetType().Name}).");
            // The OTel exporter catches this and reports ExportResult.Failure. Do not
            // use HttpRequestException: customization failures are not network retries.
            throw new InvalidOperationException("Braintrust span customization failed; the batch was not sent.", ex);
        }
    }

    private ExportTraceServiceRequest CustomizeBatch(ExportTraceServiceRequest batch)
    {
        OtlpJsonPayload.ValidateKnownFields(batch);
        var result = new ExportTraceServiceRequest();
        var groups = new Dictionary<(Resource? Resource, string SchemaUrl),
            (ResourceSpans Resource, Dictionary<(InstrumentationScope? Scope, string SchemaUrl), ScopeSpans> Scopes)>();

        foreach (var resource in batch.ResourceSpans)
        {
            if (resource.ScopeSpans.Count == 0)
            {
                Add(resource);
            }
            foreach (var scope in resource.ScopeSpans)
            {
                if (scope.Spans.Count == 0)
                {
                    Add(new ResourceSpans
                    {
                        Resource = resource.Resource,
                        SchemaUrl = resource.SchemaUrl,
                        ScopeSpans = { scope }
                    });
                }
                foreach (var span in scope.Spans)
                {
                    var data = OtlpJsonPayload.ToSpanJson(resource, scope, span);
                    for (var i = 0; i < _customizers.Count; i++)
                    {
                        data = _customizers[i].OnSpanExport(data)
                            ?? throw new InvalidOperationException("A span customizer returned null.");
                        var customized = OtlpJsonPayload.FromSpanJson(data);
                        ValidateIdentity(customized.ScopeSpans[0].Spans[0], span);
                        if (i == _customizers.Count - 1)
                        {
                            // Snapshot the final result before invoking hooks on another span.
                            // Retained JsonObjects cannot mutate a previously customized span.
                            Add(customized);
                        }
                    }
                }
            }
        }
        return result;

        void Add(ResourceSpans data)
        {
            // Group only after customization. Metadata objects are private protobuf
            // snapshots, never mutated after becoming dictionary keys.
            var key = (data.Resource, data.SchemaUrl);
            if (!groups.TryGetValue(key, out var group))
            {
                group = (new ResourceSpans { Resource = data.Resource, SchemaUrl = data.SchemaUrl }, new());
                groups.Add(key, group);
                result.ResourceSpans.Add(group.Resource);
            }
            foreach (var incomingScope in data.ScopeSpans)
            {
                var scopeKey = (incomingScope.Scope, incomingScope.SchemaUrl);
                if (!group.Scopes.TryGetValue(scopeKey, out var scope))
                {
                    scope = new ScopeSpans { Scope = incomingScope.Scope, SchemaUrl = incomingScope.SchemaUrl };
                    group.Scopes.Add(scopeKey, scope);
                    group.Resource.ScopeSpans.Add(scope);
                }
                scope.Spans.Add(incomingScope.Spans);
            }
        }
    }

    private static void ValidateIdentity(OtlpSpan span, OtlpSpan original)
    {
        if (!span.TraceId.Equals(original.TraceId) ||
            !span.SpanId.Equals(original.SpanId) ||
            !span.ParentSpanId.Equals(original.ParentSpanId))
        {
            throw new InvalidOperationException("A span customizer changed the trace, span or parent span ID.");
        }
    }
}
