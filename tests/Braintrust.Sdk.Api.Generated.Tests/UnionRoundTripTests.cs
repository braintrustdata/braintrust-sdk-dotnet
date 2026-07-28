using System.Net;
using System.Text;
using System.Text.Json;
using Braintrust.Sdk.Api.Generated;
using Xunit;

namespace Braintrust.Sdk.Api.Generated.Tests;

/// <summary>
/// Regression guard for the spec normalizer's union handling (see
/// src/Braintrust.Sdk.Api.Generated/build/SpecNormalizer.cs). The spec is
/// anyOf/oneOf-heavy and undiscriminated in the OpenAPI sense, so these assert that
/// the normalizer still turns those unions into real C# class hierarchies and that
/// the patched NSwag converter round-trips them. A spec bump that regresses the
/// shape detection shows up here rather than at runtime.
/// </summary>
public class UnionRoundTripTests
{
    static readonly JsonSerializerOptions Json = new();

    [Fact]
    public void Automation_listing_page_deserializes_mixed_variants()
    {
        // A single unmatched variant used to fail the whole response.
        var json = """
            [ {"event_type":"logs","btql_filter":"x","interval_seconds":60},
              {"event_type":"topic","scope":{"type":"span"}},
              {"event_type":"windowed"} ]
            """;

        var configs = JsonSerializer.Deserialize<List<ProjectAutomationConfig>>(json, Json)!;

        Assert.Collection(configs,
            c => Assert.Equal("x", Assert.IsType<LogsProjectAutomationConfig>(c).Btql_filter),
            c => Assert.IsType<TopicAutomationConfig>(c),
            c => Assert.IsType<WindowedAutomationConfig>(c));
    }

    [Fact]
    public void SavedFunctionId_variants_are_typed()
    {
        var json = """[{"type":"function","id":"abc","version":"v1"},{"type":"global","name":"nm"}]""";

        var ids = JsonSerializer.Deserialize<List<SavedFunctionId>>(json, Json)!;

        Assert.Equal("abc", Assert.IsType<FunctionSavedFunctionId>(ids[0]).Id);
        Assert.Equal("nm", Assert.IsType<GlobalSavedFunctionId>(ids[1]).Name);
    }

    [Fact]
    public void ChatCompletionMessageParam_discriminates_on_role()
    {
        var json = """
            [ {"role":"system","content":"s"},
              {"role":"user","content":[{"type":"text","text":"u"}]},
              {"role":"assistant","content":"a"},
              {"role":"tool","content":"t","tool_call_id":"tc"},
              {"role":"function","name":"f","content":"c"},
              {"role":"developer","content":"d"} ]
            """;

        var messages = JsonSerializer.Deserialize<List<ChatCompletionMessageParam>>(json, Json)!;

        Assert.Collection(messages,
            m => Assert.IsType<SystemChatCompletionMessageParam>(m),
            m => Assert.IsType<UserChatCompletionMessageParam>(m),
            m => Assert.IsType<AssistantChatCompletionMessageParam>(m),
            m => Assert.Equal("tc", Assert.IsType<ToolChatCompletionMessageParam>(m).Tool_call_id),
            m => Assert.Equal("f", Assert.IsType<FunctionChatCompletionMessageParam>(m).Name),
            m => Assert.IsType<DeveloperChatCompletionMessageParam>(m));
    }

    [Fact]
    public void ChatCompletionContentPart_discriminates_all_ref_arms()
    {
        var json = """[{"type":"text","text":"hi"},{"type":"image_url","image_url":{"url":"u"}}]""";

        var parts = JsonSerializer.Deserialize<List<ChatCompletionContentPart>>(json, Json)!;

        Assert.IsType<ChatCompletionContentPartTextWithTitle>(parts[0]);
        Assert.IsType<ChatCompletionContentPartImageWithTitle>(parts[1]);
    }

    [Fact]
    public void Unknown_discriminator_falls_back_to_the_base_type()
    {
        // A variant added server-side must not break an already-shipped client, and
        // must not take the rest of the page down with it.
        var json = """[{"event_type":"logs"},{"event_type":"brand_new_kind","extra":1}]""";

        var configs = JsonSerializer.Deserialize<List<ProjectAutomationConfig>>(json, Json)!;

        Assert.IsType<LogsProjectAutomationConfig>(configs[0]);
        Assert.Equal(typeof(ProjectAutomationConfig), configs[1].GetType());
        Assert.True(configs[1].AdditionalProperties.ContainsKey("extra"));
    }

    [Fact]
    public void Unknown_discriminator_survives_a_round_trip()
    {
        // The read path keeps an unmatched variant intact, so the write path must not
        // then stamp the base's type name over the server's value.
        var json = """{"event_type":"brand_new_kind","extra":1}""";

        var config = JsonSerializer.Deserialize<ProjectAutomationConfig>(json, Json)!;
        var written = JsonSerializer.Serialize(config, Json);

        using var doc = JsonDocument.Parse(written);
        Assert.Equal("brand_new_kind", doc.RootElement.GetProperty("event_type").GetString());
        Assert.Equal(1, doc.RootElement.EnumerateObject().Count(p => p.NameEquals("event_type")));
        Assert.Equal(1, doc.RootElement.GetProperty("extra").GetInt32());
    }

    [Fact]
    public void Round_trip_writes_the_discriminator_exactly_once()
    {
        var json = """{"type":"function","id":"abc","version":"v1"}""";

        var value = JsonSerializer.Deserialize<SavedFunctionId>(json, Json)!;
        var written = JsonSerializer.Serialize(value, Json);

        using var doc = JsonDocument.Parse(written);
        Assert.Equal(1, doc.RootElement.EnumerateObject().Count(p => p.NameEquals("type")));
        Assert.Equal("function", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("abc", doc.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Scalar_or_array_query_param_is_sent_as_a_repeated_param()
    {
        // `ids` is anyOf:[uuid, array<uuid>] in the spec - a repeatable query param.
        var handler = new StubHandler("""{"objects":[]}""");
        var client = new BraintrustGeneratedApiClient(new HttpClient(handler));
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await client.GetProjectAsync(null, null, null, new[] { first, second }, null, null);

        var uri = handler.LastRequestUri!.ToString();
        Assert.Contains($"ids={first}", uri);
        Assert.Contains($"ids={second}", uri);
    }

    [Fact]
    public void Kind_dispatch_wrapper_types_the_string_arm()
    {
        // `content` is string | array<ChatCompletionContentPart>. Not discriminated and
        // not T | array<T>, but the arms have distinct JSON kinds, so the normalizer
        // builds a wrapper with one accessor per kind.
        var json = """{"role":"user","content":"plain text"}""";

        var message = Assert.IsType<UserChatCompletionMessageParam>(
            JsonSerializer.Deserialize<ChatCompletionMessageParam>(json, Json));

        Assert.Equal("plain text", message.Content.AsString);
        Assert.Null(message.Content.AsArray);
    }

    [Fact]
    public void Kind_dispatch_wrapper_types_the_array_arm()
    {
        var json = """{"role":"user","content":[{"type":"text","text":"structured"}]}""";

        var message = Assert.IsType<UserChatCompletionMessageParam>(
            JsonSerializer.Deserialize<ChatCompletionMessageParam>(json, Json));

        Assert.Null(message.Content.AsString);
        var part = Assert.IsType<ChatCompletionContentPartTextWithTitle>(
            Assert.Single(message.Content.AsArray));
        Assert.Equal("structured", part.Text);
    }

    [Theory]
    [InlineData(""""{"role":"user","content":"plain text"}"""", JsonValueKind.String)]
    [InlineData(""""{"role":"user","content":[{"type":"text","text":"t"}]}"""", JsonValueKind.Array)]
    public void Kind_dispatch_wrapper_round_trips_unwrapped(string json, JsonValueKind kind)
    {
        // The wrapper must never appear on the wire: `content` goes back out as the bare
        // string or the bare array, never as {"asString":...}. (Optional members the
        // generator writes as explicit nulls are not this test's concern.)
        var message = JsonSerializer.Deserialize<ChatCompletionMessageParam>(json, Json)!;

        var written = JsonSerializer.Serialize(message, Json);

        using var actual = JsonDocument.Parse(written);
        var content = actual.RootElement.GetProperty("content");
        Assert.Equal(kind, content.ValueKind);
        if (kind == JsonValueKind.String) Assert.Equal("plain text", content.GetString());
        else Assert.Equal("t", content[0].GetProperty("text").GetString());
    }

    [Fact]
    public void Kind_dispatch_wrapper_accepts_a_bare_string()
    {
        // Assigning the shorthand form is the common case, so the wrapper converts from
        // string implicitly rather than making callers name it.
        var message = new UserChatCompletionMessageParam { Content = "hello" };

        var written = JsonSerializer.Serialize<ChatCompletionMessageParam>(message, Json);

        using var doc = JsonDocument.Parse(written);
        Assert.Equal("hello", doc.RootElement.GetProperty("content").GetString());
        Assert.Equal("user", doc.RootElement.GetProperty("role").GetString());
    }

    [Fact]
    public void Undiscriminable_union_stays_free_form_but_lossless()
    {
        // ModelParams is five object arms with no discriminator and no distinguishing
        // JSON kind - nothing can type it, so it stays a free-form object. Untyped, but
        // it still round-trips rather than failing to deserialize.
        var json = """{"model":"gpt-4o","temperature":0.5,"max_tokens":128}""";

        var value = JsonSerializer.Deserialize<object>(json, Json);
        var written = JsonSerializer.Serialize(value, Json);

        Assert.Equal(
            JsonDocument.Parse(json).RootElement.GetRawText(),
            JsonDocument.Parse(written).RootElement.GetRawText());
    }

    sealed class StubHandler : HttpMessageHandler
    {
        readonly string _body;

        public Uri? LastRequestUri { get; private set; }

        public StubHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
