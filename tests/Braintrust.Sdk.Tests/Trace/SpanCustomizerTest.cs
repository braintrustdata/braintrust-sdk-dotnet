using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Braintrust.Sdk.Config;
using Braintrust.Sdk.Trace;
using Braintrust.Sdk.Trace.Protos.Collector.Trace.V1;
using Braintrust.Sdk.Trace.Protos.Common.V1;
using Braintrust.Sdk.Trace.Protos.Trace.V1;
using Google.Protobuf;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OtlpSpan = Braintrust.Sdk.Trace.Protos.Trace.V1.Span;
using Resource = Braintrust.Sdk.Trace.Protos.Resource.V1.Resource;
using Status = Braintrust.Sdk.Trace.Protos.Trace.V1.Status;

namespace Braintrust.Sdk.Tests.Trace;

public class SpanCustomizerTest : IDisposable
{
    private readonly ActivitySource _source = new($"secret-source-{Guid.NewGuid()}", "secret-version");
    private readonly ActivityListener _listener;

    public SpanCustomizerTest()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source == _source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(_listener);
    }

    [Fact]
    public void Export_ScrubsEachSpanInRegistrationOrderWithoutMutatingActivityOrOtherExports()
    {
        var calls = new List<string>();
        var originals = new List<(JsonObject Payload, string Json)>();
        var results = new List<(JsonObject Payload, string Json)>();
        JsonObject? replacement = null;
        var registrations = new List<ISpanCustomizer>
        {
            new OmittedHook(),
            new Customizer(payload =>
            {
                calls.Add($"first({payload["name"]!.GetValue<string>()})");
                Assert.False(payload.ContainsKey("resourceSpans"));
                originals.Add((payload, payload.ToJsonString()));
                replacement = payload.DeepClone().AsObject();
                Scrub(replacement);
                return replacement;
            }),
            new Customizer(payload =>
            {
                calls.Add($"second({payload["name"]!.GetValue<string>()})");
                Assert.Same(replacement, payload);
                Assert.StartsWith("safe-", payload["name"]!.GetValue<string>());
                payload["name"] = payload["name"]!.GetValue<string>() + "-final";
                results.Add((payload, payload.ToJsonString()));
                return payload;
            })
        };
        var config = Config(registrations);
        registrations.Clear();
        registrations.Add(new Customizer(_ => throw new InvalidOperationException("must not be registered")));
        using var first = CompletedSpan("secret-first");
        using var second = CompletedSpan("secret-second");
        var baseline = new CaptureHandler();
        Assert.Equal(ExportResult.Success, Export(Config(), baseline, first, second));
        var transport = new CaptureHandler();

        Assert.Equal(ExportResult.Success, Export(config, transport, first, second));

        Assert.Equal(new[] { "first(secret-first)", "second(safe-first)", "first(secret-second)", "second(safe-second)" }, calls);
        Assert.All(originals, original => Assert.Equal(original.Json, original.Payload.ToJsonString()));
        Assert.All(results, result => Assert.Equal(result.Json, result.Payload.ToJsonString()));
        var originalBatch = Assert.Single(baseline.Batches);
        var batch = Assert.Single(transport.Batches);
        var resource = Assert.Single(batch.ResourceSpans);
        Assert.Equal("safe-service", resource.Resource.Attributes.Single(a => a.Key == "service.name").Value.StringValue);
        Assert.Equal("safe-resource-value", resource.Resource.Attributes.Single(a => a.Key == "safe-resource-key").Value.StringValue);
        var scope = Assert.Single(resource.ScopeSpans);
        Assert.Equal(_source.Name.Replace("secret", "safe"), scope.Scope.Name);
        Assert.Equal("safe-version", scope.Scope.Version);
        var originalSpans = originalBatch.ResourceSpans[0].ScopeSpans[0].Spans;
        for (var i = 0; i < scope.Spans.Count; i++)
        {
            var exported = scope.Spans[i];
            var original = originalSpans[i];
            Assert.Equal(original.Name.Replace("secret", "safe") + "-final", exported.Name);
            Assert.Equal("safe-input", exported.Attributes.Single(a => a.Key == "safe-tag-key").Value.StringValue);
            Assert.Equal("safe-event", exported.Events[0].Name);
            Assert.Equal("safe-detail", exported.Events[0].Attributes.Single(a => a.Key == "safe-event-key").Value.StringValue);
            Assert.Equal("safe-status", exported.Status.Message);
            Assert.Equal("safe-link-value", exported.Links[0].Attributes.Single(a => a.Key == "safe-link-key").Value.StringValue);
            Assert.Equal(original.TraceId, exported.TraceId);
            Assert.Equal(original.SpanId, exported.SpanId);
            Assert.Equal(original.ParentSpanId, exported.ParentSpanId);
            Assert.Equal(original.StartTimeUnixNano, exported.StartTimeUnixNano);
            Assert.Equal(original.EndTimeUnixNano, exported.EndTimeUnixNano);
            Assert.Equal(original.Kind, exported.Kind);
            Assert.Equal(original.Flags, exported.Flags);
            Assert.Equal(original.TraceState, exported.TraceState);
            Assert.Equal(original.Status.Code, exported.Status.Code);
            Assert.Equal(original.Links[0].TraceId, exported.Links[0].TraceId);
            Assert.Equal(original.Links[0].SpanId, exported.Links[0].SpanId);
        }
        Assert.Equal("Bearer test-key", transport.Authorization);
        Assert.Equal("project_id:default-project", transport.Parent);
        Assert.Equal("secret-first", first.DisplayName);
        Assert.Equal("secret-input", first.GetTagItem("secret-tag-key"));
        Assert.Equal("secret-event", first.Events.Single().Name);
        Assert.Equal("secret-detail", first.Events.Single().Tags.Single().Value);
        Assert.Equal("secret-status", first.StatusDescription);
        Assert.Equal("secret-link-value", first.Links.Single().Tags!.Single().Value);
        var laterBaseline = new CaptureHandler();
        Assert.Equal(ExportResult.Success, Export(Config(), laterBaseline, first, second));
        Assert.Equal(originalBatch, Assert.Single(laterBaseline.Batches));
    }

    [Fact]
    public async Task Handler_PreservesHexIdsAndIntegerPrecisionWhileScrubbingNestedValues()
    {
        var batch = WireBatch();
        var original = batch.Clone();
        var span = batch.ResourceSpans[0].ScopeSpans[0].Spans[0];
        var calls = 0;
        var config = Config(new[]
        {
            new Customizer(payload =>
            {
                calls++;
                var jsonSpan = payload;
                Assert.Equal(Convert.ToHexString(span.TraceId.ToByteArray()).ToLowerInvariant(), jsonSpan["traceId"]!.GetValue<string>());
                Assert.Equal(Convert.ToHexString(span.SpanId.ToByteArray()).ToLowerInvariant(), jsonSpan["spanId"]!.GetValue<string>());
                Assert.Equal(Convert.ToHexString(span.ParentSpanId.ToByteArray()).ToLowerInvariant(), jsonSpan["parentSpanId"]!.GetValue<string>());
                Assert.Equal("18446744073709551615", jsonSpan["startTimeUnixNano"]!.GetValue<string>());
                Assert.Equal("9007199254740993", jsonSpan["endTimeUnixNano"]!.GetValue<string>());
                Assert.Equal((int)span.Kind, jsonSpan["kind"]!.GetValue<int>());
                Assert.Equal((int)span.Status.Code, jsonSpan["status"]!["code"]!.GetValue<int>());
                var values = jsonSpan["attributes"]!.AsArray();
                Assert.Equal("9223372036854775807", values[0]!["value"]!["intValue"]!.GetValue<string>());
                Assert.Equal("-9223372036854775808", values[1]!["value"]!["intValue"]!.GetValue<string>());
                Assert.Equal("AAH+/w==", values[2]!["value"]!["bytesValue"]!.GetValue<string>());
                Assert.Equal(Convert.ToHexString(span.Links[0].TraceId.ToByteArray()).ToLowerInvariant(), jsonSpan["links"]![0]!["traceId"]!.GetValue<string>());
                Assert.Equal(Convert.ToHexString(span.Links[0].SpanId.ToByteArray()).ToLowerInvariant(), jsonSpan["links"]![0]!["spanId"]!.GetValue<string>());
                Scrub(payload);
                return payload;
            })
        });
        var transport = new CaptureHandler();

        await SendAsync(config, transport, batch.ToByteArray());

        Assert.Equal(1, calls);
        Assert.Equal(original, batch);
        var result = Assert.Single(transport.Batches);
        var scope = result.ResourceSpans[0].ScopeSpans[0];
        Assert.Equal("safe-scope-value", scope.Scope.Attributes[0].Value.StringValue);
        Assert.Equal("safe-scope-key", scope.Scope.Attributes[0].Key);
        var nested = scope.Spans[0].Attributes[3].Value.ArrayValue.Values[0].KvlistValue.Values[0];
        Assert.Equal("safe-nested-key", nested.Key);
        Assert.Equal("safe-nested-value", nested.Value.StringValue);
        Assert.Equal(span.Attributes.Take(3), scope.Spans[0].Attributes.Take(3));
        Assert.Equal(span.StartTimeUnixNano, scope.Spans[0].StartTimeUnixNano);
        Assert.Equal(span.EndTimeUnixNano, scope.Spans[0].EndTimeUnixNano);
        Assert.Equal(span.Links, scope.Spans[0].Links);
    }

    [Theory]
    [InlineData("resource")]
    [InlineData("scope")]
    [InlineData("resourceSchemaUrl")]
    [InlineData("scopeSchemaUrl")]
    public async Task Handler_IsolatesSiblingMetadataAndSplitsOnlyChangedGroups(string field)
    {
        var batch = WireBatch();
        var resource = batch.ResourceSpans[0];
        var scope = resource.ScopeSpans[0];
        var first = scope.Spans[0];
        first.Name = "first";
        var second = first.Clone();
        second.Name = "second";
        second.SpanId = ByteString.CopyFrom(Convert.FromHexString("1123456789abcdef"));
        var third = first.Clone();
        third.Name = "third";
        third.SpanId = ByteString.CopyFrom(Convert.FromHexString("2123456789abcdef"));
        scope.Spans.Add(second);
        scope.Spans.Add(third);
        var original = batch.Clone();
        JsonObject? changedPayload = null;
        JsonNode? originalMetadata = null;
        var config = Config(new[]
        {
            new Customizer(payload =>
            {
                if (payload["name"]!.GetValue<string>() == "first")
                {
                    changedPayload = payload;
                    originalMetadata = payload[field]?.DeepClone();
                    switch (field)
                    {
                        case "resource":
                            payload["resource"]!["attributes"]![0]!["value"]!["stringValue"] = "changed-resource";
                            break;
                        case "scope": payload["scope"]!["name"] = "changed-scope"; break;
                        default: payload[field] = "https://changed.example/schema"; break;
                    }
                }
                else
                {
                    Assert.True(JsonNode.DeepEquals(originalMetadata, payload[field]));
                    Assert.NotSame(changedPayload!["resource"], payload["resource"]);
                    Assert.NotSame(changedPayload["scope"], payload["scope"]);
                }
                return payload;
            })
        });
        var transport = new CaptureHandler();

        await SendAsync(config, transport, batch.ToByteArray());

        Assert.Equal(original, batch);
        var exported = Assert.Single(transport.Batches);
        var changedResource = exported.ResourceSpans.Single(r => r.ScopeSpans.Any(s => s.Spans.Any(p => p.Name == "first")));
        var unchangedResource = exported.ResourceSpans.Single(r => r.ScopeSpans.Any(s => s.Spans.Any(p => p.Name == "second")));
        var changedScope = changedResource.ScopeSpans.Single(s => s.Spans.Any(p => p.Name == "first"));
        var unchangedScope = unchangedResource.ScopeSpans.Single(s => s.Spans.Any(p => p.Name == "second"));
        Assert.Equal(first, Assert.Single(changedScope.Spans));
        Assert.Equal(new[] { second, third }, unchangedScope.Spans);
        Assert.Equal(resource.Resource, unchangedResource.Resource);
        Assert.Equal(resource.SchemaUrl, unchangedResource.SchemaUrl);
        Assert.Equal(scope.Scope, unchangedScope.Scope);
        Assert.Equal(scope.SchemaUrl, unchangedScope.SchemaUrl);
        if (field is "resource" or "resourceSchemaUrl")
        {
            Assert.Equal(2, exported.ResourceSpans.Count);
            Assert.Single(changedResource.ScopeSpans);
            Assert.Single(unchangedResource.ScopeSpans);
        }
        else
        {
            Assert.Single(exported.ResourceSpans);
            Assert.Equal(2, changedResource.ScopeSpans.Count);
        }
        var expectedResource = resource.Resource.Clone();
        var expectedScope = scope.Scope.Clone();
        if (field == "resource") expectedResource.Attributes[0].Value.StringValue = "changed-resource";
        if (field == "scope") expectedScope.Name = "changed-scope";
        Assert.Equal(expectedResource, changedResource.Resource);
        Assert.Equal(expectedScope, changedScope.Scope);
        Assert.Equal(field == "resourceSchemaUrl" ? "https://changed.example/schema" : resource.SchemaUrl, changedResource.SchemaUrl);
        Assert.Equal(field == "scopeSchemaUrl" ? "https://changed.example/schema" : scope.SchemaUrl, changedScope.SchemaUrl);
    }

    [Theory]
    [InlineData("traceId")]
    [InlineData("spanId")]
    [InlineData("parentSpanId")]
    [InlineData("root-parent")]
    public void Export_RejectsLaterHookIdentityChangesBeforeTransmitting(string field)
    {
        var calls = 0;
        var config = Config(new ISpanCustomizer[]
        {
            new OmittedHook(),
            new Customizer(payload =>
            {
                calls++;
                payload[field == "root-parent" ? "parentSpanId" : field] = new string('1', field == "traceId" ? 32 : 16);
                return payload;
            })
        });
        using var activity = CompletedSpan("unchanged", root: field == "root-parent");
        var transport = new CaptureHandler();

        Assert.Equal(ExportResult.Failure, Export(config, transport, activity));

        Assert.Equal(1, calls);
        Assert.Equal(0, transport.Requests);
        Assert.Empty(transport.Batches);
    }

    [Fact]
    public void Export_RejectsSwappingSiblingIdentitiesEvenThoughBatchIdentitySetWouldBeUnchanged()
    {
        using var first = CompletedSpan("first");
        using var second = CompletedSpan("second");
        var calls = 0;
        var config = Config(new[]
        {
            new Customizer(payload =>
            {
                calls++;
                var other = payload["name"]!.GetValue<string>() == "first" ? second : first;
                payload["traceId"] = other.TraceId.ToHexString();
                payload["spanId"] = other.SpanId.ToHexString();
                payload["parentSpanId"] = other.ParentSpanId.ToHexString();
                return payload;
            })
        });
        var transport = new CaptureHandler();

        Assert.Equal(ExportResult.Failure, Export(config, transport, first, second));

        Assert.Equal(1, calls);
        Assert.Equal(0, transport.Requests);
        Assert.Empty(transport.Batches);
    }

    [Fact]
    public void Export_ValidatesEachHookBeforeLaterHookCanRestoreIdentity()
    {
        string? originalId = null;
        var laterCalled = false;
        var config = Config(new[]
        {
            new Customizer(payload =>
            {
                originalId = payload["spanId"]!.GetValue<string>();
                payload["spanId"] = new string('1', 16);
                return payload;
            }),
            new Customizer(payload =>
            {
                laterCalled = true;
                payload["spanId"] = originalId;
                return payload;
            })
        });
        using var activity = CompletedSpan("unchanged");
        var transport = new CaptureHandler();

        Assert.Equal(ExportResult.Failure, Export(config, transport, activity));

        Assert.False(laterCalled);
        Assert.Equal(0, transport.Requests);
    }

    [Fact]
    public void Export_OmittedHookPreservesPayloadAndExplicitResubmissionRunsHooksAgain()
    {
        using var activity = CompletedSpan("unchanged", root: true);
        var baseline = new CaptureHandler();
        Assert.Equal(ExportResult.Success, Export(Config(), baseline, activity));
        var calls = 0;
        var config = Config(new ISpanCustomizer[] { new OmittedHook(), new Customizer(payload => { calls++; return payload; }) });
        var customized = new CaptureHandler();
        Assert.Equal(ExportResult.Success, Export(config, customized, activity));
        Assert.Equal(Assert.Single(baseline.Batches), Assert.Single(customized.Batches));
        Assert.Equal(ExportResult.Success, Export(config, new CaptureHandler(), activity));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Export_LaterSpanCannotMutateAnEarlierAcceptedResultThroughRetainedJson()
    {
        using var first = CompletedSpan("first");
        using var second = CompletedSpan("second");
        JsonObject? retained = null;
        var config = Config(new[]
        {
            new Customizer(payload =>
            {
                if (payload["name"]!.GetValue<string>() == "first")
                {
                    payload["name"] = "accepted-first";
                    retained = payload;
                }
                else
                {
                    retained!["name"] = "corrupted";
                    retained["spanId"] = new string('1', 16);
                    retained["scope"]!["name"] = "corrupted-scope";
                    retained["resource"] = null;
                }
                return payload;
            })
        });
        var transport = new CaptureHandler();

        Assert.Equal(ExportResult.Success, Export(config, transport, first, second));

        var resource = Assert.Single(Assert.Single(transport.Batches).ResourceSpans);
        Assert.Equal("secret-service", resource.Resource.Attributes.Single(a => a.Key == "service.name").Value.StringValue);
        var scope = Assert.Single(resource.ScopeSpans);
        Assert.Equal(_source.Name, scope.Scope.Name);
        Assert.Equal(new[] { "accepted-first", "second" }, scope.Spans.Select(span => span.Name));
        Assert.Equal(first.SpanId.ToHexString(), Convert.ToHexString(scope.Spans[0].SpanId.ToByteArray()).ToLowerInvariant());
        Assert.Equal("corrupted", retained!["name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("null")]
    [InlineData("unknown-field")]
    [InlineData("unknown-nested-field")]
    [InlineData("wrong-type")]
    [InlineData("invalid-hex")]
    [InlineData("invalid-shape")]
    [InlineData("unknown-resource-field")]
    [InlineData("unknown-scope-field")]
    public async Task Handler_LaterSpanHookFailurePreventsSendingEarlierCustomizedSpans(string failure)
    {
        var batch = WireBatch();
        var spans = batch.ResourceSpans[0].ScopeSpans[0].Spans;
        spans[0].Name = "first";
        var second = spans[0].Clone();
        second.Name = "second";
        second.SpanId = ByteString.CopyFrom(Convert.FromHexString("1123456789abcdef"));
        spans.Add(second);
        var calls = new List<string>();
        var config = Config(new[]
        {
            new Customizer(payload =>
            {
                calls.Add($"first({payload["name"]!.GetValue<string>()})");
                payload["name"] = payload["name"]!.GetValue<string>() + "-customized";
                return payload;
            }),
            new Customizer(payload =>
            {
                calls.Add($"second({payload["name"]!.GetValue<string>()})");
                if (payload["name"]!.GetValue<string>() != "second-customized") return payload;
                switch (failure)
                {
                    case "throw": throw new InvalidOperationException("scrubbing failed");
                    case "null": return null!;
                    case "unknown-field": payload["unexpected"] = true; break;
                    case "unknown-nested-field": payload["status"]!["unexpected"] = true; break;
                    case "wrong-type": payload["name"] = 17; break;
                    case "invalid-hex": payload["traceId"] = new string('z', 32); break;
                    case "invalid-shape": payload["scope"] = "not an object"; break;
                    case "unknown-resource-field": payload["resource"]!["unexpected"] = true; break;
                    case "unknown-scope-field": payload["scope"]!["unexpected"] = true; break;
                }
                return payload;
            })
        });
        var transport = new CaptureHandler();

        await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync(config, transport, batch.ToByteArray()));

        Assert.Equal(new[] { "first(first)", "second(first-customized)", "first(second)", "second(second-customized)" }, calls);
        Assert.Equal(0, transport.Requests);
        Assert.Empty(transport.Batches);
    }

    [Fact]
    public async Task Handler_RejectsInvalidIntermediateResultBeforeLaterHookCanRepairIt()
    {
        var laterCalled = false;
        var config = Config(new[]
        {
            new Customizer(payload =>
            {
                payload["unexpected"] = true;
                return payload;
            }),
            new Customizer(payload =>
            {
                laterCalled = true;
                payload.Remove("unexpected");
                return payload;
            })
        });
        var transport = new CaptureHandler();

        await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync(config, transport, WireBatch().ToByteArray()));

        Assert.False(laterCalled);
        Assert.Equal(0, transport.Requests);
        Assert.Empty(transport.Batches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handler_RejectsMalformedOrCompressedPayloadBeforeTransport(bool compressed)
    {
        var transport = new CaptureHandler();
        var bytes = compressed ? new ExportTraceServiceRequest().ToByteArray() : new byte[] { 0xff };

        await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync(Config(new[] { new OmittedHook() }), transport, bytes, compressed));

        Assert.Equal(0, transport.Requests);
    }

    [Theory]
    [InlineData("batch")]
    [InlineData("span")]
    [InlineData("attribute-value")]
    public async Task Handler_RejectsUnknownProtobufFieldsBeforeCallingHooks(string location)
    {
        var batch = WireBatch();
        var spans = batch.ResourceSpans[0].ScopeSpans[0].Spans;
        switch (location)
        {
            case "batch": batch = ExportTraceServiceRequest.Parser.ParseFrom(WithUnknownField(batch)); break;
            case "span":
                var later = OtlpSpan.Parser.ParseFrom(WithUnknownField(spans[0]));
                later.SpanId = ByteString.CopyFrom(Convert.FromHexString("1123456789abcdef"));
                spans.Add(later);
                break;
            case "attribute-value":
                spans[0].Attributes[0].Value = AnyValue.Parser.ParseFrom(WithUnknownField(spans[0].Attributes[0].Value));
                break;
        }
        var called = false;
        var config = Config(new[] { new Customizer(payload => { called = true; return payload; }) });
        var transport = new CaptureHandler();

        await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync(config, transport, batch.ToByteArray()));

        Assert.False(called);
        Assert.Equal(0, transport.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handler_RemovedOrNullMetadataDoesNotRestoreOriginalContent(bool nullMetadata)
    {
        var batch = WireBatch();
        var config = Config(new[]
        {
            new Customizer(payload =>
            {
                foreach (var field in new[] { "resource", "scope", "resourceSchemaUrl", "scopeSchemaUrl" })
                {
                    if (nullMetadata) payload[field] = null;
                    else payload.Remove(field);
                }
                foreach (var field in new[] { "attributes", "events", "links", "status" })
                    payload.Remove(field);
                payload["kind"] = (int)OtlpSpan.Types.SpanKind.Server;
                payload["traceState"] = "sanitized=state";
                payload["startTimeUnixNano"] = "9007199254740993";
                payload["endTimeUnixNano"] = "9007199254740994";
                return payload;
            })
        });
        var transport = new CaptureHandler();

        await SendAsync(config, transport, batch.ToByteArray());

        var resource = Assert.Single(Assert.Single(transport.Batches).ResourceSpans);
        Assert.Null(resource.Resource);
        Assert.Equal("", resource.SchemaUrl);
        var scope = Assert.Single(resource.ScopeSpans);
        Assert.Null(scope.Scope);
        Assert.Equal("", scope.SchemaUrl);
        var exported = Assert.Single(scope.Spans);
        Assert.Empty(exported.Attributes);
        Assert.Empty(exported.Events);
        Assert.Empty(exported.Links);
        Assert.Null(exported.Status);
        Assert.Equal(OtlpSpan.Types.SpanKind.Server, exported.Kind);
        Assert.Equal("sanitized=state", exported.TraceState);
        Assert.Equal(9007199254740993UL, exported.StartTimeUnixNano);
        Assert.Equal(9007199254740994UL, exported.EndTimeUnixNano);
    }

    private static void Scrub(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToArray())
                {
                    if (key is "traceId" or "spanId" or "parentSpanId" || value == null) continue;
                    if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                        obj[key] = text.Replace("secret", "safe", StringComparison.Ordinal);
                    else
                        Scrub(value);
                }
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                        array[i] = text.Replace("secret", "safe", StringComparison.Ordinal);
                    else if (array[i] is { } child)
                        Scrub(child);
                }
                break;
        }
    }

    private Activity CompletedSpan(string name, bool root = false)
    {
        var parent = root ? default : new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded, "vendor=value", true);
        var linkContext = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
        var activity = _source.StartActivity(name, ActivityKind.Client, parent,
            links: new[] { new ActivityLink(linkContext, new ActivityTagsCollection { { "secret-link-key", "secret-link-value" } }) })!;
        activity.SetTag("secret-tag-key", "secret-input");
        activity.SetTag("braintrust.parent", "project_id:original");
        activity.AddEvent(new ActivityEvent("secret-event", tags: new ActivityTagsCollection { { "secret-event-key", "secret-detail" } }));
        activity.SetStatus(ActivityStatusCode.Error, "secret-status");
        activity.Stop();
        return activity;
    }

    private static ExportTraceServiceRequest WireBatch()
    {
        var span = new OtlpSpan
        {
            TraceId = ByteString.CopyFrom(Convert.FromHexString("00112233445566778899aabbccddeeff")),
            SpanId = ByteString.CopyFrom(Convert.FromHexString("0123456789abcdef")),
            ParentSpanId = ByteString.CopyFrom(Convert.FromHexString("fedcba9876543210")),
            Name = "secret-span",
            Kind = OtlpSpan.Types.SpanKind.Client,
            StartTimeUnixNano = ulong.MaxValue,
            EndTimeUnixNano = 9007199254740993UL,
            Status = new Status { Code = Status.Types.StatusCode.Error, Message = "secret-status" },
            Attributes =
            {
                new KeyValue { Key = "maximum", Value = new AnyValue { IntValue = long.MaxValue } },
                new KeyValue { Key = "minimum", Value = new AnyValue { IntValue = long.MinValue } },
                new KeyValue { Key = "bytes", Value = new AnyValue { BytesValue = ByteString.CopyFrom(new byte[] { 0, 1, 254, 255 }) } },
                new KeyValue
                {
                    Key = "nested",
                    Value = new AnyValue
                    {
                        ArrayValue = new ArrayValue
                        {
                            Values = { new AnyValue { KvlistValue = new KeyValueList { Values = { new KeyValue { Key = "secret-nested-key", Value = new AnyValue { StringValue = "secret-nested-value" } } } } } }
                        }
                    }
                }
            },
            Events =
            {
                new OtlpSpan.Types.Event
                {
                    Name = "secret-event",
                    TimeUnixNano = 9007199254740993UL,
                    Attributes = { new KeyValue { Key = "secret-event-key", Value = new AnyValue { StringValue = "secret-event-value" } } }
                }
            },
            Links =
            {
                new OtlpSpan.Types.Link
                {
                    TraceId = ByteString.CopyFrom(Convert.FromHexString("ffeeddccbbaa99887766554433221100")),
                    SpanId = ByteString.CopyFrom(Convert.FromHexString("8877665544332211"))
                }
            }
        };
        return new ExportTraceServiceRequest
        {
            ResourceSpans =
            {
                new ResourceSpans
                {
                    Resource = new Resource
                    {
                        Attributes = { new KeyValue { Key = "secret-resource-key", Value = new AnyValue { StringValue = "secret-resource-value" } } }
                    },
                    SchemaUrl = "https://secret.example/resource",
                    ScopeSpans =
                    {
                        new ScopeSpans
                        {
                            SchemaUrl = "https://secret.example/scope",
                            Scope = new InstrumentationScope
                            {
                                Name = "secret-scope",
                                Attributes = { new KeyValue { Key = "secret-scope-key", Value = new AnyValue { StringValue = "secret-scope-value" } } }
                            },
                            Spans = { span }
                        }
                    }
                }
            }
        };
    }

    private static byte[] WithUnknownField(IMessage message)
    {
        using var stream = new MemoryStream();
        stream.Write(message.ToByteArray());
        using (var output = new CodedOutputStream(stream, leaveOpen: true))
        {
            output.WriteTag(127, WireFormat.WireType.Varint);
            output.WriteUInt32(1);
            output.Flush();
        }
        return stream.ToArray();
    }

    private static BraintrustConfig Config(IEnumerable<ISpanCustomizer>? customizers = null) => BraintrustConfig.Of(
        customizers ?? Array.Empty<ISpanCustomizer>(),
        ("BRAINTRUST_API_KEY", "test-key"),
        ("BRAINTRUST_DEFAULT_PROJECT_ID", "default-project"));

    private static ExportResult Export(BraintrustConfig config, CaptureHandler transport, params Activity[] activities)
    {
        using var client = BraintrustTracing.CreateHttpClient(config, transport);
        var exporter = new OtlpTraceExporter(new OtlpExporterOptions
        {
            Endpoint = new Uri("https://example.invalid/otel/v1/traces"),
            Protocol = OtlpExportProtocol.HttpProtobuf,
            HttpClientFactory = () => client
        });
        using var provider = OpenTelemetry.Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateEmpty()
                .AddService("secret-service", serviceInstanceId: "test-instance")
                .AddAttributes(new[] { new KeyValuePair<string, object>("secret-resource-key", "secret-resource-value") }))
            .AddProcessor(new SimpleActivityExportProcessor(exporter))
            .Build();
        using var batch = new Batch<Activity>(activities, activities.Length);
        return exporter.Export(in batch);
    }

    private static async Task SendAsync(BraintrustConfig config, CaptureHandler transport, byte[] bytes, bool compressed = false)
    {
        using var client = BraintrustTracing.CreateHttpClient(config, transport);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/otel/v1/traces")
        {
            Content = new ByteArrayContent(bytes)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        if (compressed) request.Content.Headers.ContentEncoding.Add("gzip");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class OmittedHook : ISpanCustomizer { }

    private sealed class Customizer(Func<JsonObject, JsonObject> hook) : ISpanCustomizer
    {
        public JsonObject OnSpanExport(JsonObject payload) => hook(payload);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        internal List<ExportTraceServiceRequest> Batches { get; } = new();
        internal int Requests { get; private set; }
        internal string? Authorization { get; private set; }
        internal string? Parent { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Authorization = request.Headers.Authorization?.ToString();
            Parent = request.Headers.GetValues("x-bt-parent").Single();
            Batches.Add(ExportTraceServiceRequest.Parser.ParseFrom(request.Content!.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult()));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));
    }

    public void Dispose()
    {
        _listener.Dispose();
        _source.Dispose();
    }
}
