using System.Net;
using System.Text;
using System.Text.Json;
using Braintrust.Sdk.Api.Generated;
using Xunit;

namespace Braintrust.Sdk.Api.Generated.Tests;

/// <summary>
/// Guards how the generated client writes request bodies. Both assertions here cover a
/// failure the API rejected outright, so a regression is a broken write path rather than
/// a cosmetic diff:
///
/// - string enums must go out with their spec values, which needs the
///   [JsonStringEnumMemberName] annotations that /jsonLibraryVersion:9.0 emits;
/// - unset optional members must be omitted, which is what
///   SerializerSettings.cs configures.
/// </summary>
public class RequestSerializationTests
{
    [Fact]
    public async Task String_enums_are_written_with_their_spec_values()
    {
        var body = await Capture(client => client.PostProjectAutomationAsync(Automation(
            new LogsProjectAutomationConfig
            {
                Status = AutomationStatus.Paused,
                Btql_filter = "scores.Factuality < 0.5",
                Interval_seconds = 300,
                Action = new WebhookProjectAutomationConfigAction { Url = "https://example.com/hook" },
            })));

        var config = body.GetProperty("config");

        // "paused", not the C# member name "Paused".
        Assert.Equal("paused", config.GetProperty("status").GetString());

        // The discriminators the patched inheritance converter writes.
        Assert.Equal("logs", config.GetProperty("event_type").GetString());
        Assert.Equal("webhook", config.GetProperty("action").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Unset_optional_members_are_omitted_rather_than_written_as_null()
    {
        var body = await Capture(client => client.PostProjectAutomationAsync(Automation(
            new LogsProjectAutomationConfig
            {
                Btql_filter = "true",
                Interval_seconds = 60,
                Action = new WebhookProjectAutomationConfigAction { Url = "https://example.com/hook" },
            })));

        Assert.False(body.TryGetProperty("description", out _));
        Assert.False(body.GetProperty("config").GetProperty("action")
            .TryGetProperty("formatting_prompt", out _));
    }

    static CreateProjectAutomation Automation(ProjectAutomationConfig config) => new()
    {
        Project_id = Guid.NewGuid(),
        Name = "test-automation",
        Config = config,
    };

    /// <summary>
    /// Runs a call against a stub transport and returns the request body it sent. The
    /// response is deliberately minimal - only the request matters here.
    /// </summary>
    static async Task<JsonElement> Capture(Func<BraintrustGeneratedApiClient, Task> call)
    {
        var handler = new CapturingHandler("""
            {"id":"3f2504e0-4f89-11d3-9a0c-0305e82c3301",
             "project_id":"6ba7b810-9dad-11d1-80b4-00c04fd430c8",
             "name":"test-automation",
             "config":{"event_type":"logs","btql_filter":"true","interval_seconds":60,
                       "action":{"type":"webhook","url":"https://example.com/hook"}}}
            """);

        await call(new BraintrustGeneratedApiClient(new HttpClient(handler)));

        return JsonDocument.Parse(handler.LastRequestBody!).RootElement;
    }

    sealed class CapturingHandler : HttpMessageHandler
    {
        readonly string _response;

        public string? LastRequestBody { get; private set; }

        public CapturingHandler(string response) => _response = response;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_response, Encoding.UTF8, "application/json"),
            };
        }
    }
}
