namespace Braintrust.Sdk.Examples.SimpleOpenTelemetry;

class Program
{
    static async Task Main(string[] args)
    {
        var braintrust = Braintrust.Get();
        var activitySource = braintrust.GetActivitySource();

        var url = "";
        using (var activity = activitySource.StartActivity("hello-dotnet"))
        {
            ArgumentNullException.ThrowIfNull(activity);
            Console.WriteLine("Performing simple operation...");
            activity.SetTag("some boolean attribute", true);
            url = await braintrust.GetProjectUriAsync() + $"/logs?r={activity.TraceId}&s={activity.SpanId}";
        }
        Console.WriteLine($"\n\n  Example complete! View your data in Braintrust: {url}");
    }
}
