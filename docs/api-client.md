# Braintrust API client

The SDK ships a low-level HTTP client for the [Braintrust REST API](https://api.braintrust.dev).

> If you just want to run evals or trace AI calls, prefer `Braintrust.Sdk.Eval.Eval` and
> `Braintrust.Get()`. Reach for the API client only when you need raw REST access.

The client is **generated code**. Every operation and model comes from Braintrust's public
OpenAPI spec:

- Spec repo: <https://github.com/braintrustdata/braintrust-openapi>
- The exact commit we generate against is pinned as `BraintrustOpenApiRef` in
  [`src/Braintrust.Sdk.Api.Generated/Braintrust.Sdk.Api.Generated.csproj`](../src/Braintrust.Sdk.Api.Generated/Braintrust.Sdk.Api.Generated.csproj).

Nothing is committed: the build downloads the pinned spec, normalizes it, runs NSwag, and
compiles the result. The generated assembly ships *inside* the `Braintrust.Sdk` package, so
there is no second package to install. (Working in this repo is the exception: a project that
touches generated types needs its own `ProjectReference` to `Braintrust.Sdk.Api.Generated`,
because `Braintrust.Sdk` references it with `PrivateAssets="all"`. See
[`examples/ApiClientExample`](../examples/ApiClientExample/ApiClientExample.csproj).)

## Basic usage

`DefaultBraintrustApiClient` is the SDK's own client; its `Api` property is the generated
client, already wired up with the base URL, bearer auth and timeout from your config.

```csharp
using Braintrust.Sdk.Api;
using Braintrust.Sdk.Config;
using Generated = Braintrust.Sdk.Api.Generated;

using var client = DefaultBraintrustApiClient.Of(BraintrustConfig.FromEnvironment());
Generated.IBraintrustGeneratedApiClient api = client.Api;

// Create a project (POST /v1/project upserts by name).
Generated.Project project = await api.PostProjectAsync(new Generated.CreateProject
{
    Name = "my-project",
    Description = "created from dotnet",
});

Console.WriteLine($"{project.Id} {project.Name}");
```

The generated client borrows the `HttpClient` owned by `DefaultBraintrustApiClient`, so keep
that instance alive for as long as you use `Api`.

### Runnable example

A complete, runnable example is in [`examples/ApiClientExample`](../examples/ApiClientExample).
It reads a project's experiments, prompts, datasets and automations, and includes - but does
not invoke - two full create/read/update/delete round trips: one for an automation, one for
an experiment (which also logs a row and reads it back).

```bash
BRAINTRUST_API_KEY=sk-... dotnet run --project examples/ApiClientExample
```

## What generated code looks like

It is shaped by the spec rather than by the SDK, so it does not follow .NET naming
conventions:

- **Properties are spec-cased**: `project.Org_id`, `experiment.Base_exp_id`,
  `config.Btql_filter`.
- **Identifiers are `Guid`**, not `string`.
- **List endpoints take every filter positionally, with no defaults.** Pass `null` for the
  ones you don't need; named arguments keep the call readable.
- **List endpoints return a page wrapper** whose `Objects` property holds the results.

```csharp
var page = await api.GetExperimentAsync(
    limit: 5,
    starting_after: null,
    ending_before: null,
    ids: null,
    experiment_name: null,
    project_name: null,
    project_id: project.Id,
    org_name: null);

foreach (var experiment in page.Objects)
{
    Console.WriteLine($"{experiment.Name} ({experiment.Id})");
}
```

Pass the last id you saw as `starting_after` to walk further pages.

### Unions

Most Braintrust unions are tagged with a discriminator field (`type`, `event_type`, `role`,
`provider`). Those generate as a base class plus one subclass per variant, so you write them
by picking a subclass and read them with a type switch:

```csharp
// Writing: pick the variant. The event_type/type discriminators are written for you.
var config = new Generated.LogsProjectAutomationConfig
{
    Btql_filter = "scores.Factuality < 0.5",
    Interval_seconds = 300,
    Action = new Generated.WebhookProjectAutomationConfigAction
    {
        Url = "https://example.com/braintrust-hook",
    },
};

var automation = await api.PostProjectAutomationAsync(new Generated.CreateProjectAutomation
{
    Project_id = project.Id,
    Name = "low-factuality-alert",
    Config = config,
});

// Reading: switch on the concrete type.
string description = automation.Config switch
{
    Generated.LogsProjectAutomationConfig logs => $"logs: {logs.Btql_filter}",
    Generated.RetentionProjectAutomationConfig r => $"retention: {r.Retention_days} days",
    _ => "some other kind",
};
```

Two details worth knowing:

- **A variant the pinned spec has never heard of does not throw.** It deserializes as the
  base class with its fields in `AdditionalProperties`, so one unrecognized row cannot fail
  a whole page. Bump `BraintrustOpenApiRef` to get a typed subclass for it.
- **The discriminator is not a declared property.** It belongs to the base schema, so it
  arrives in the inherited `AdditionalProperties` bag: `config.AdditionalProperties["event_type"]`.

A union whose arms have distinct JSON kinds (`string | object`, `string | array`) generates
as a wrapper class with one property per arm - `AsString`, `AsObject`, `AsArray`, `AsNumber`,
`AsBoolean`. Only the arm you set is written to the wire, and the wrapper itself never
appears there. Wrappers with a string arm also convert implicitly from `string`:

```csharp
Generated.ChatCompletionContentPartContent content = "hello";   // fills AsString
var parts = new Generated.ChatCompletionContentPartContent
{
    AsArray = [ /* ... */ ],
};
```

Some schemas are genuinely untypeable - free-form JSON, or a union of same-shaped object
arms with nothing to tell them apart. Those land as `object`, which `System.Text.Json`
materializes as `JsonElement`. Row payloads (`input`, `expected`, `output`) and a few
function fields (`params`, `options`) are the ones you are most likely to hit:

```csharp
var input = (JsonElement)row.Input;
Console.WriteLine(input.GetProperty("question").GetString());
```

### Enums and nulls

String enum members are named in C# style but carry their spec value, so
`AutomationStatus.Paused` goes out as `"paused"`. Use the enum; the casing difference is
handled for you.

Unset optional members are **omitted** from request bodies rather than written as
`null` - the API validates nullability strictly and rejects an explicit null for a field
you never set. The flip side is that assigning `null` cannot clear a field on a PATCH;
serialize that field yourself if you need to.

### Errors

Generated calls throw `Generated.ApiException` (or `Generated.ApiException<T>`), not the
SDK's `Braintrust.Sdk.Api.ApiException` - only the SDK's own wrapper methods translate. The
server's message is in `ex.Response`:

```csharp
try
{
    await api.GetProjectIdAsync(id);
}
catch (Generated.ApiException ex)
{
    Console.WriteLine($"{ex.StatusCode}: {ex.Response}");
}
```

The spec declares error bodies as JSON strings, but the API returns plain text, so a 4xx
usually surfaces as `ApiException: Could not deserialize the response body string as
System.String`. That message is about the *error* body, not your request - the real
diagnostic is still in `ex.Response`.

### Where the spec and the server disagree

The spec is generated from the API's own types, but a few declared filters are rejected at
runtime - `project_automation_name` on `GET /v1/project_automation`, for instance, comes back
`400 Extraneous key`. Filter client-side when that happens.

`/api/apikey/login` is not in the spec at all. `DefaultBraintrustApiClient` issues it by hand
and exposes the result through `GetProjectAndOrgInfo`.

## Compatibility

The generated surface tracks whatever spec ref the build pinned, so it is **not** covered by
the SDK's own compatibility promises: bumping the ref can rename a class or change a
signature. Prefer `IBraintrustApiClient`'s methods where they already cover what you need.

## Bumping the spec

Edit `BraintrustOpenApiRef` in
[`Braintrust.Sdk.Api.Generated.csproj`](../src/Braintrust.Sdk.Api.Generated/Braintrust.Sdk.Api.Generated.csproj)
and rebuild - the ref is part of the spec cache path, so a bump is a cache miss. To generate
against a local checkout of the spec instead:

```bash
BRAINTRUST_OPENAPI_ROOT=/path/to/braintrust-openapi dotnet build
```

Then run the tests. `tests/Braintrust.Sdk.Api.Generated.Tests` is the regression guard:
`GeneratedShapeInvariantTests` asserts the shape of the output by reflection (no empty
pass-through classes, no `Name`/`Name2` pairs, every union base memberless with variants),
`UnionRoundTripTests` covers the discriminator handling described above, and
`RequestSerializationTests` covers what goes out on the wire - enum values, and optional
members being omitted rather than nulled.

Normalization of the spec happens in
[`build/SpecNormalizer.cs`](../src/Braintrust.Sdk.Api.Generated/build/SpecNormalizer.cs); every
rule there matches on schema *shape*, never on a schema name, so new unions in the spec are
handled without editing it. The NSwag template patches that make unions round-trip are
documented in
[`build/templates/PATCHES.md`](../src/Braintrust.Sdk.Api.Generated/build/templates/PATCHES.md).
