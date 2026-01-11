using System.Diagnostics;
using Braintrust.Sdk.Config;
using Braintrust.Sdk.Trace;

namespace Braintrust.Sdk.Tests.Trace;

public class BraintrustSpanProcessorTest : IDisposable
{
    private readonly ActivitySource _activitySource;
    private readonly ActivityListener _activityListener;

    public BraintrustSpanProcessorTest()
    {
        _activitySource = new ActivitySource("test-source");

        // Set up activity listener so activities are actually created
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "test-source",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(_activityListener);
    }

    public void Dispose()
    {
        _activityListener?.Dispose();
        _activitySource?.Dispose();
    }

    [Fact]
    public void OnStart_AddsParentFromContext()
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key")
        );
        var processor = new BraintrustSpanProcessor(config);

        var context = BraintrustContext.OfProject("proj-123");
        using (context.MakeCurrent())
        using (var activity = _activitySource.StartActivity("test-span"))
        {
            Assert.NotNull(activity);
            processor.OnStart(activity);

            var parent = activity.GetTagItem(BraintrustSpanProcessor.ParentAttributeKey);
            Assert.Equal("project_id:proj-123", parent);
        }
    }

    [Fact]
    public void OnStart_AddsExperimentParentFromContext()
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key")
        );
        var processor = new BraintrustSpanProcessor(config);

        var context = BraintrustContext.OfExperiment("exp-456");
        using (context.MakeCurrent())
        using (var activity = _activitySource.StartActivity("test-span"))
        {
            Assert.NotNull(activity);
            processor.OnStart(activity);

            var parent = activity.GetTagItem(BraintrustSpanProcessor.ParentAttributeKey);
            Assert.Equal("experiment_id:exp-456", parent);
        }
    }

    [Fact]
    public void OnStart_AddsParentFromConfig()
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_DEFAULT_PROJECT_ID", "proj-config")
        );
        var processor = new BraintrustSpanProcessor(config);

        using (var activity = _activitySource.StartActivity("test-span"))
        {
            Assert.NotNull(activity);
            processor.OnStart(activity);

            var parent = activity.GetTagItem(BraintrustSpanProcessor.ParentAttributeKey);
            Assert.Equal("project_id:proj-config", parent);
        }
    }

    [Fact]
    public void OnStart_ContextOverridesConfig()
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_DEFAULT_PROJECT_ID", "proj-config")
        );
        var processor = new BraintrustSpanProcessor(config);

        var context = BraintrustContext.OfProject("proj-context");
        using (context.MakeCurrent())
        using (var activity = _activitySource.StartActivity("test-span"))
        {
            Assert.NotNull(activity);
            processor.OnStart(activity);

            var parent = activity.GetTagItem(BraintrustSpanProcessor.ParentAttributeKey);
            Assert.Equal("project_id:proj-context", parent);
        }
    }

    [Fact]
    public void OnStart_DoesNotOverrideExistingParent()
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_DEFAULT_PROJECT_ID", "proj-config")
        );
        var processor = new BraintrustSpanProcessor(config);

        using (var activity = _activitySource.StartActivity("test-span"))
        {
            Assert.NotNull(activity);
            // Set parent before processor runs
            activity.SetTag(BraintrustSpanProcessor.ParentAttributeKey, "project_id:existing");

            processor.OnStart(activity);

            var parent = activity.GetTagItem(BraintrustSpanProcessor.ParentAttributeKey);
            Assert.Equal("project_id:existing", parent);
        }
    }
}
