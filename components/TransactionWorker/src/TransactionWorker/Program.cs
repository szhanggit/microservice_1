using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TransactionWorker.Application;
using TransactionWorker.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddTransactionWorkerInfrastructure(builder.Configuration);

const string serviceName = "TransactionWorker";

// KEDA.md §7: instrumentation exists unconditionally; only the exporter would
// change (AddConsoleExporter -> AddOtlpExporter) if this ever points at a
// real Grafana Cloud stack - no other code changes needed.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(serviceName))
    .WithTracing(t => t
        .AddSource(TransactionWorkerTelemetry.SourceName)
        .AddConsoleExporter())
    .WithMetrics(m => m
        .AddMeter(TransactionWorkerTelemetry.SourceName)
        .AddConsoleExporter());

builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    o.IncludeScopes = true;
    o.AddConsoleExporter();
});

var host = builder.Build();
host.Run();
