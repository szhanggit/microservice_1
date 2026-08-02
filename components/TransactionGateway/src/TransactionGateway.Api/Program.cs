using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TransactionGateway.Api.Contracts;
using TransactionGateway.Application;
using TransactionGateway.Infrastructure;

// Required for the gRPC client to call TransactionService over plain HTTP/2
// ("h2c") - e.g. http://transaction-service:8080 inside the Docker Compose
// network. TLS isn't needed for container-to-container traffic on a private
// Compose network, but .NET's SocketsHttpHandler blocks HTTP/2 over
// cleartext unless this switch is set.
AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTransactionGatewayInfrastructure(builder.Configuration);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

const string serviceName = "TransactionGateway";

// TransactionGateway.md §8: instrumentation exists unconditionally; only the
// exporter would change (AddConsoleExporter -> AddOtlpExporter) if this ever
// points at a real Grafana Cloud stack.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(serviceName))
    .WithTracing(t => t
        .AddSource(TransactionGatewayTelemetry.SourceName)
        .AddAspNetCoreInstrumentation()
        .AddGrpcClientInstrumentation()
        .AddConsoleExporter())
    .WithMetrics(m => m
        .AddMeter(TransactionGatewayTelemetry.SourceName)
        .AddAspNetCoreInstrumentation()
        .AddConsoleExporter());

builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    o.IncludeScopes = true;
    o.AddConsoleExporter();
});

var app = builder.Build();

// Exposed unconditionally per this project's requirement - a real production
// deployment would typically gate this behind Development/an internal route.
app.UseSwagger();
app.UseSwaggerUI();

// ALB target group health check (kubernetes/transaction-gateway/ingress.yaml)
// and Kubernetes probes (kubernetes/transaction-gateway/deployment.yaml) - no
// downstream dependency checks, just confirms the process is up and Kestrel
// is accepting connections.
app.MapGet("/health", () => Results.Ok());
app.MapGet("/ready", () => Results.Ok());
app.MapGet("/live", () => Results.Ok());

app.MapPost("/api/v1/transactions", async (
    SubmitTransactionHttpRequest request,
    ITransactionSubmissionHandler handler,
    CancellationToken cancellationToken) =>
{
    var validationError = Validate(request);
    if (validationError is not null)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["request"] = [validationError],
        });
    }

    var submission = new TransactionSubmission(
        request.TransactionNo,
        request.TransactionDatetime,
        request.Amount,
        request.Type,
        request.Status,
        request.Currency);

    var accepted = await handler.HandleAsync(submission, cancellationToken);

    return accepted
        ? Results.Accepted()
        : Results.Problem(title: "Failed to forward transaction to TransactionService", statusCode: StatusCodes.Status502BadGateway);
})
.WithName("SubmitTransaction");

app.MapGet("/api/v1/transactions/{transactionNo}", async (
    string transactionNo,
    ITransactionSearchHandler handler,
    CancellationToken cancellationToken) =>
{
    var result = await handler.SearchByTransactionNoAsync(transactionNo, cancellationToken);

    return result.Outcome switch
    {
        TransactionNoSearchOutcome.Found => Results.Ok(ToHttpResponse(result.Transaction!)),
        TransactionNoSearchOutcome.NotFound => Results.NotFound(),
        TransactionNoSearchOutcome.InvalidArgument => Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["request"] = [result.ErrorMessage ?? "Invalid request"],
        }),
        TransactionNoSearchOutcome.ForwardingFailed => Results.Problem(title: "Failed to search TransactionService", statusCode: StatusCodes.Status502BadGateway),
        _ => throw new InvalidOperationException($"Unhandled outcome: {result.Outcome}"),
    };
})
.WithName("SearchTransactionByTransactionNo");

app.MapGet("/api/v1/transactions", async (
    string from,
    string to,
    int? limit,
    int? offset,
    ITransactionSearchHandler handler,
    CancellationToken cancellationToken) =>
{
    var result = await handler.SearchByDateRangeAsync(from, to, limit, offset, cancellationToken);

    return result.Outcome switch
    {
        DateRangeSearchOutcome.Success => Results.Ok(new TransactionSearchPageHttpResponse(
            result.Transactions!.Select(ToHttpResponse).ToList(),
            result.HasMore)),
        DateRangeSearchOutcome.InvalidArgument => Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["request"] = [result.ErrorMessage ?? "Invalid request"],
        }),
        DateRangeSearchOutcome.ForwardingFailed => Results.Problem(title: "Failed to search TransactionService", statusCode: StatusCodes.Status502BadGateway),
        _ => throw new InvalidOperationException($"Unhandled outcome: {result.Outcome}"),
    };
})
.WithName("SearchTransactionsByDateRange");

app.Run();

static string? Validate(SubmitTransactionHttpRequest request)
{
    if (string.IsNullOrWhiteSpace(request.TransactionNo)) return "transaction_no is required";
    if (string.IsNullOrWhiteSpace(request.TransactionDatetime)) return "transaction_datetime is required";
    if (string.IsNullOrWhiteSpace(request.Amount)) return "amount is required";
    if (string.IsNullOrWhiteSpace(request.Type)) return "type is required";
    if (string.IsNullOrWhiteSpace(request.Status)) return "status is required";
    if (string.IsNullOrWhiteSpace(request.Currency)) return "currency is required";
    return null;
}

static TransactionHttpResponse ToHttpResponse(TransactionRecord record) => new(
    record.Id,
    record.TransactionNo,
    record.TransactionDatetime,
    record.Amount,
    record.Type,
    record.Status,
    record.Currency,
    record.SystemDatetime);
