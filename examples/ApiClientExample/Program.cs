using System.Text.Json;
using Braintrust.Sdk.Api;
using Braintrust.Sdk.Config;

// The generated types live in their own namespace and are aliased rather than imported:
// it declares a Project, Experiment and Dataset of its own, which would collide with the
// SDK's records of the same name in Braintrust.Sdk.Api.
using Generated = Braintrust.Sdk.Api.Generated;

/// <summary>
/// Demonstrates the low-level, OpenAPI-generated Braintrust API client, for raw REST
/// access beyond what the Eval and tracing helpers cover. See docs/api-client.md for the
/// full walkthrough.
///
/// Run with:
///
///   BRAINTRUST_API_KEY=sk-... dotnet run --project examples/ApiClientExample
///
/// NOTE: this example is safe to run. It only reads resources by default. Methods which
/// mutate state are included as an example, but they are not invoked.
/// </summary>
namespace Braintrust.Sdk.Examples.ApiClientExample;

class Program
{
    // Cap each listing so the example prints a manageable amount.
    private const int Limit = 5;

    // Prefixes for the uniquely named resources the lifecycle methods create and delete.
    private const string AutomationNamePrefix = "dotnet-example-low-factuality-alert";
    private const string ExperimentNamePrefix = "dotnet-example-experiment";

    static async Task Main(string[] args)
    {
        // BraintrustOpenApiClient is the SDK's own client; its Api property is the
        // generated client, already wired up with the base URL, bearer auth and timeout
        // from the config.
        using var client = BraintrustOpenApiClient.Of(BraintrustConfig.FromEnvironment());
        Generated.IBraintrustGeneratedApiClient api = client.Api;

        // Pick the project to read from, and resolve its org for the banner.
        var project = await ResolveProject(api);
        var org = await api.GetOrganizationIdAsync(project.Org_id);
        Console.WriteLine($"Reading project {project.Name} from org {org.Name}");

        // List endpoints share a set of leading pagination/filter parameters and return a
        // page wrapper whose Objects holds the results. The generated methods take every
        // filter positionally with no defaults, so pass null for the ones you don't need -
        // named arguments keep that readable. Here each list is scoped to project.Id.

        // -- Experiments -------------------------------------------------------------
        var experiments = await api.GetExperimentAsync(
            limit: Limit,
            starting_after: null,
            ending_before: null,
            ids: null,
            experiment_name: null,
            project_name: null,
            project_id: project.Id,
            org_name: null);

        Console.WriteLine("\nExperiments:");
        foreach (var experiment in experiments.Objects)
        {
            Console.WriteLine($"  {experiment.Name} ({experiment.Id})");
        }

        // -- Prompts -----------------------------------------------------------------
        var prompts = await api.GetPromptAsync(
            limit: Limit,
            starting_after: null,
            ending_before: null,
            ids: null,
            prompt_name: null,
            project_name: null,
            project_id: project.Id,
            slug: null,
            version: null,
            environment: null,
            org_name: null);

        Console.WriteLine("\nPrompts:");
        foreach (var prompt in prompts.Objects)
        {
            Console.WriteLine($"  {prompt.Name} ({prompt.Id})");
        }

        // -- Datasets ----------------------------------------------------------------
        var datasets = await api.GetDatasetAsync(
            limit: Limit,
            starting_after: null,
            ending_before: null,
            ids: null,
            dataset_name: null,
            project_name: null,
            project_id: project.Id,
            org_name: null);

        Console.WriteLine("\nDatasets:");
        foreach (var dataset in datasets.Objects)
        {
            Console.WriteLine($"  {dataset.Name} ({dataset.Id})");
        }

        // -- Project automations -----------------------------------------------------
        // Automations are the alert and export rules attached to a project.
        await ListAutomations(api);

        // Uncomment to exercise the write paths; each deletes everything it creates.
        // await AutomationLifecycle(api, project.Id);
        // await ExperimentLifecycle(api, project.Id);
    }

    /// <summary>
    /// Picks the project to read from, preferring explicit configuration over whatever
    /// happens to be first in the org: BRAINTRUST_DEFAULT_PROJECT_NAME, then
    /// BRAINTRUST_PROJECT, then the org's first project.
    ///
    /// Read straight from the environment rather than via BraintrustConfig, because
    /// DefaultProjectName falls back to a built-in default, so it cannot express "unset"
    /// and would never let the later options apply.
    /// </summary>
    private static async Task<Generated.Project> ResolveProject(
        Generated.IBraintrustGeneratedApiClient api)
    {
        foreach (var envVar in new[] { "BRAINTRUST_DEFAULT_PROJECT_NAME", "BRAINTRUST_PROJECT" })
        {
            var name = Environment.GetEnvironmentVariable(envVar);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            // project_name is a server-side filter, so a match comes back as the only object.
            var matches = await api.GetProjectAsync(
                limit: 1,
                starting_after: null,
                ending_before: null,
                ids: null,
                project_name: name,
                org_name: null);

            if (matches.Objects.Count == 0)
            {
                throw new InvalidOperationException(
                    $"{envVar} is set to \"{name}\" but no project by that name exists");
            }

            Console.WriteLine($"Selected project via {envVar}");
            return matches.Objects.First();
        }

        var first = await api.GetProjectAsync(
            limit: 1,
            starting_after: null,
            ending_before: null,
            ids: null,
            project_name: null,
            org_name: null);

        return first.Objects.FirstOrDefault()
            ?? throw new InvalidOperationException("this org has no projects to read from");
    }

    /// <summary>
    /// Lists the org's project automations, printing each one's config.
    /// </summary>
    private static async Task ListAutomations(Generated.IBraintrustGeneratedApiClient api)
    {
        var page = await api.GetProjectAutomationAsync(
            limit: Limit,
            starting_after: null,
            ending_before: null,
            ids: null,
            project_automation_name: null,
            org_name: null);

        Console.WriteLine("\nAutomations:");
        foreach (var automation in page.Objects)
        {
            Console.WriteLine($"  {automation.Name} ({automation.Id}): {Describe(automation.Config)}");
        }
    }

    /// <summary>
    /// Config is a union over the automation kinds, generated as a base class plus one
    /// subclass per kind, so a type switch is how you read it.
    /// </summary>
    private static string Describe(Generated.ProjectAutomationConfig config) => config switch
    {
        Generated.LogsProjectAutomationConfig logs =>
            $"logs, filter '{logs.Btql_filter}' every {logs.Interval_seconds}s -> {Describe(logs.Action)}",
        Generated.BtqlExportProjectAutomationConfig export =>
            $"btql_export -> {export.Export_path}",
        Generated.RetentionProjectAutomationConfig retention =>
            $"retention, {retention.Retention_days} days",
        Generated.EnvironmentUpdateProjectAutomationConfig update =>
            $"environment_update -> {Describe(update.Action)}",

        // Every other kind, of which there are a few more - this example just does not
        // spell them out. A kind added to the API *after* the pinned spec ref has no
        // subclass to land in, so it arrives as the base type with its fields in
        // AdditionalProperties, rather than failing the whole page.
        _ when config.GetType() == typeof(Generated.ProjectAutomationConfig) =>
            $"{Kind(config)} (unknown to the pinned spec, kept as the base type)",
        _ => $"{Kind(config)} ({config.GetType().Name})",
    };

    private static string Describe(Generated.ProjectAutomationConfigAction action) => action switch
    {
        Generated.WebhookProjectAutomationConfigAction webhook => $"webhook {webhook.Url}",
        Generated.SlackProjectAutomationConfigAction slack => $"slack {slack.Channel}",
        _ => $"{Kind(action, discriminator: "type")} action",
    };

    /// <summary>
    /// The discriminator belongs to the base schema, so it is not a declared property on
    /// either the base or its subclasses - it lands in the inherited extension data.
    /// </summary>
    private static string Kind(object config, string discriminator = "event_type")
    {
        var extensionData = config switch
        {
            Generated.ProjectAutomationConfig c => c.AdditionalProperties,
            Generated.ProjectAutomationConfigAction a => a.AdditionalProperties,
            _ => null,
        };

        return extensionData?.TryGetValue(discriminator, out var value) == true
            ? value?.ToString() ?? "unknown"
            : "unknown";
    }

    /// <summary>
    /// Full create / read / update / delete round trip for a log-alert automation, which
    /// POSTs to a webhook whenever a low-scoring row lands.
    ///
    /// Not called by default - it mutates the project. Call it from Main to try the write
    /// path; it deletes what it creates, so it leaves the project as it found it.
    /// </summary>
    private static async Task AutomationLifecycle(
        Generated.IBraintrustGeneratedApiClient api, Guid projectId)
    {
        var automationName = $"{AutomationNamePrefix}-{Guid.NewGuid():N}";

        // Each automation kind is its own generated subclass; LogsProjectAutomationConfig
        // is the "logs" kind. The event_type discriminator is written for you.
        var config = new Generated.LogsProjectAutomationConfig
        {
            // Fire at most once every 5 minutes for matching rows.
            Btql_filter = "scores.Factuality < 0.5",
            Interval_seconds = 300,
            Action = new Generated.WebhookProjectAutomationConfigAction
            {
                Url = "https://example.com/braintrust-hook",
            },
        };

        var created = await api.PostProjectAutomationAsync(new Generated.CreateProjectAutomation
        {
            Project_id = projectId,
            Name = automationName,
            Description = "created by examples/ApiClientExample",
            Config = config,
        });

        try
        {
            Console.WriteLine($"\nCreated automation: {created.Name} ({created.Id})");

            // Read back by id.
            var fetched = await api.GetProjectAutomationIdAsync(created.Id);
            Console.WriteLine($"Fetched back: {fetched.Description} - {Describe(fetched.Config)}");

            // Update: tighten the filter and relabel.
            config.Btql_filter = "scores.Factuality < 0.25";
            var updated = await api.PatchProjectAutomationIdAsync(
                created.Id,
                new Generated.PatchProjectAutomation
                {
                    Description = "updated by examples/ApiClientExample",
                    Config = config,
                });
            Console.WriteLine($"Updated: {updated.Description} - {Describe(updated.Config)}");
        }
        finally
        {
            // Delete after every successful create, even if a later lifecycle step fails.
            await api.DeleteProjectAutomationIdAsync(created.Id);
            Console.WriteLine($"Deleted automation {created.Id}");
        }
    }
    /// <summary>
    /// Full create / read / update / delete round trip for an experiment, including logging
    /// a row into it and reading it back.
    ///
    /// Not called by default - it mutates the project. Call it from Main to try the write
    /// path; it deletes what it creates, so it leaves the project as it found it.
    ///
    /// For real evals prefer Braintrust.Sdk.Eval.Eval, which creates the experiment, runs
    /// the cases and logs the rows for you. This is the same work done a request at a time.
    /// </summary>
    private static async Task ExperimentLifecycle(
        Generated.IBraintrustGeneratedApiClient api, Guid projectId)
    {
        // POST /v1/experiment returns an existing experiment of the same name unmodified
        // rather than failing, so a unique name keeps this from adopting - and then
        // deleting - somebody else's experiment.
        var experimentName = $"{ExperimentNamePrefix}-{Guid.NewGuid():N}";

        var created = await api.PostExperimentAsync(new Generated.CreateExperiment
        {
            Project_id = projectId,
            Name = experimentName,
            Description = "created by examples/ApiClientExample",
            Tags = ["dotnet-example"],
            Metadata = new Dictionary<string, object> { ["source"] = "api-client-example" },
        });

        try
        {
            Console.WriteLine($"\nCreated experiment: {created.Name} ({created.Id})");

            // Read back by id.
            var fetched = await api.GetExperimentIdAsync(created.Id);
            Console.WriteLine($"Fetched back: {fetched.Description} tags=[{string.Join(", ", fetched.Tags)}]");

            // The same experiment is also reachable through the list endpoint, which does
            // honour its experiment_name filter (unlike project_automation_name above).
            var byName = await api.GetExperimentAsync(
                limit: 1,
                starting_after: null,
                ending_before: null,
                ids: null,
                experiment_name: experimentName,
                project_name: null,
                project_id: projectId,
                org_name: null);
            Console.WriteLine($"Found by name: {byName.Objects.Count == 1}");

            // Log a row. input/output/expected are free-form JSON in the spec, so they are
            // typed as object: pass anything serializable.
            var inserted = await api.PostExperimentIdInsertAsync(
                created.Id,
                new Generated.InsertExperimentEventRequest
                {
                    Events =
                    [
                        new Generated.InsertExperimentEvent
                        {
                            Input = new Dictionary<string, object> { ["question"] = "What is 2 + 2?" },
                            Output = "4",
                            Expected = "4",
                            Scores = new Dictionary<string, double?> { ["exact_match"] = 1.0 },
                            Metadata = new Generated.Metadata { Model = "example-model" },
                        },
                    ],
                });
            Console.WriteLine($"Inserted {inserted.Row_ids.Count} row(s)");

            // Read the rows back. Fetch is a POST with the query in the body, and the
            // free-form fields come back as JsonElement.
            var events = await api.PostExperimentIdFetchAsync(
                created.Id,
                new Generated.FetchEventsRequest { Limit = Limit });

            foreach (var row in events.Events)
            {
                var input = ((JsonElement)row.Input).GetRawText();
                var output = ((JsonElement)row.Output).GetRawText();
                var scores = string.Join(", ", row.Scores.Select(s => $"{s.Key}={s.Value}"));
                Console.WriteLine($"  {input} -> {output} ({scores})");
            }

            // Update: relabel and retag.
            var updated = await api.PatchExperimentIdAsync(
                created.Id,
                new Generated.PatchExperiment
                {
                    Description = "updated by examples/ApiClientExample",
                    Tags = ["dotnet-example", "updated"],
                });
            Console.WriteLine($"Updated: {updated.Description} tags=[{string.Join(", ", updated.Tags)}]");
        }
        finally
        {
            // Delete after every successful create, even if a later lifecycle step fails.
            await api.DeleteExperimentIdAsync(created.Id);
            Console.WriteLine($"Deleted experiment {created.Id}");
        }
    }
}
