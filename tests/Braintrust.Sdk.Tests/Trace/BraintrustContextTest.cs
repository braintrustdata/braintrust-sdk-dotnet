using Braintrust.Sdk.Trace;
using Xunit;

namespace Braintrust.Sdk.Tests.Trace;

public class BraintrustContextTest
{
    [Fact]
    public void OfExperiment_CreatesContextWithExperimentId()
    {
        var context = BraintrustContext.OfExperiment("exp-123");

        Assert.Null(context.ProjectId);
        Assert.Equal("exp-123", context.ExperimentId);
    }

    [Fact]
    public void OfProject_CreatesContextWithProjectId()
    {
        var context = BraintrustContext.OfProject("proj-456");

        Assert.Equal("proj-456", context.ProjectId);
        Assert.Null(context.ExperimentId);
    }

    [Fact]
    public void GetParentValue_ReturnsExperimentIdFormat()
    {
        var context = BraintrustContext.OfExperiment("exp-123");

        var parentValue = context.GetParentValue();

        Assert.Equal("experiment_id:exp-123", parentValue);
    }

    [Fact]
    public void GetParentValue_ReturnsProjectIdFormat()
    {
        var context = BraintrustContext.OfProject("proj-456");

        var parentValue = context.GetParentValue();

        Assert.Equal("project_id:proj-456", parentValue);
    }

    [Fact]
    public void MakeCurrent_SetsCurrent()
    {
        var context = BraintrustContext.OfProject("proj-789");

        using (context.MakeCurrent())
        {
            Assert.Equal(context, BraintrustContext.Current);
        }

        // Context should be restored after dispose
        Assert.Null(BraintrustContext.Current);
    }

    [Fact]
    public void MakeCurrent_RestoresPreviousContext()
    {
        var context1 = BraintrustContext.OfProject("proj-1");
        var context2 = BraintrustContext.OfProject("proj-2");

        using (context1.MakeCurrent())
        {
            Assert.Equal(context1, BraintrustContext.Current);

            using (context2.MakeCurrent())
            {
                Assert.Equal(context2, BraintrustContext.Current);
            }

            // Should restore to context1
            Assert.Equal(context1, BraintrustContext.Current);
        }

        // Should restore to null
        Assert.Null(BraintrustContext.Current);
    }
}
