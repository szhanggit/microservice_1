using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TransactionService.Application;
using TransactionService.Grpc.Services;
using TransactionService.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.AddTransactionServiceInfrastructure(builder.Configuration);

const string serviceName = "TransactionService";

// TransactionService.md §8: instrumentation exists unconditionally; only the
// exporter would change (AddConsoleExporter -> AddOtlpExporter) if this ever
// points at a real Grafana Cloud stack.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(serviceName))
    .WithTracing(t => t
        .AddSource(TransactionServiceTelemetry.SourceName)
        .AddAspNetCoreInstrumentation()
        .AddConsoleExporter())
    .WithMetrics(m => m
        .AddMeter(TransactionServiceTelemetry.SourceName)
        .AddAspNetCoreInstrumentation()
        .AddConsoleExporter());

builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    o.IncludeScopes = true;
    o.AddConsoleExporter();
});

var app = builder.Build();

app.MapGrpcService<TransactionGrpcService>();
app.MapGet("/", () => "TransactionService gRPC endpoint - use a gRPC client (e.g. grpcurl) to call SubmitTransaction.");

app.Run();
