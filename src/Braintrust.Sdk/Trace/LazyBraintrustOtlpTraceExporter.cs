using System.Diagnostics;
using System.Reflection;
using Braintrust.Sdk.Config;
using OpenTelemetry;
using OpenTelemetry.Exporter;

namespace Braintrust.Sdk.Trace;

internal sealed class LazyBraintrustOtlpTraceExporter : BaseExporter<Activity>
{
    private static readonly MethodInfo SetParentProviderMethod =
        typeof(BaseExporter<Activity>)
            .GetProperty(nameof(ParentProvider))!
            .GetSetMethod(nonPublic: true)!;

    private readonly BraintrustConfig _config;
    private readonly object _lock = new();
    private Task<OtlpTraceExporter>? _exporterTask;

    internal LazyBraintrustOtlpTraceExporter(BraintrustConfig config)
    {
        _config = config;
    }

    public override ExportResult Export(in Batch<Activity> batch)
    {
        try
        {
            return GetExporterAsync().GetAwaiter().GetResult().Export(batch);
        }
        catch
        {
            return ExportResult.Failure;
        }
    }

    protected override bool OnForceFlush(int timeoutMilliseconds)
    {
        return TryGetExporter(GetExporterAsync(), timeoutMilliseconds, out var exporter)
               && exporter.ForceFlush(timeoutMilliseconds);
    }

    protected override bool OnShutdown(int timeoutMilliseconds)
    {
        Task<OtlpTraceExporter>? exporterTask;
        lock (_lock)
        {
            exporterTask = _exporterTask;
        }

        return exporterTask == null
               || (TryGetExporter(exporterTask, timeoutMilliseconds, out var exporter)
                   && exporter.Shutdown(timeoutMilliseconds));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Task<OtlpTraceExporter>? exporterTask;
            lock (_lock)
            {
                exporterTask = _exporterTask;
            }

            if (exporterTask is { IsCompletedSuccessfully: true })
            {
                exporterTask.Result.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private Task<OtlpTraceExporter> GetExporterAsync()
    {
        lock (_lock)
        {
            _exporterTask ??= CreateExporterAsync();
            return _exporterTask;
        }
    }

    private async Task<OtlpTraceExporter> CreateExporterAsync()
    {
        var apiKey = await _config.GetRequiredApiKeyAsync().ConfigureAwait(false);
        var exporter = new OtlpTraceExporter(new OtlpExporterOptions
        {
            Protocol = OtlpExportProtocol.HttpProtobuf,
            Endpoint = new Uri($"{_config.ApiUrl}{_config.TracesPath}"),
            Headers = BraintrustTracing.BuildHeaders(_config, apiKey),
            TimeoutMilliseconds = (int)_config.RequestTimeout.TotalMilliseconds
        });
        // OpenTelemetry only assigns ParentProvider to the outer exporter, but OtlpTraceExporter
        // reads resources from its own ParentProvider during export.
        SetParentProviderMethod.Invoke(exporter, new object?[] { ParentProvider });
        return exporter;
    }

    private static bool TryGetExporter(
        Task<OtlpTraceExporter> exporterTask,
        int timeoutMilliseconds,
        out OtlpTraceExporter exporter)
    {
        try
        {
            if (timeoutMilliseconds == Timeout.Infinite)
            {
                exporter = exporterTask.GetAwaiter().GetResult();
                return true;
            }

            if (!exporterTask.Wait(timeoutMilliseconds))
            {
                exporter = null!;
                return false;
            }

            exporter = exporterTask.GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            exporter = null!;
            return false;
        }
    }
}
