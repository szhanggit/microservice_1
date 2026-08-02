using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TransactionGateway.Application;

/// <summary>
/// Named ActivitySource/Meter shared by the Application layer and registered
/// with OpenTelemetry in Program.cs (TransactionGateway.md §8). Instrumented
/// unconditionally - only the exporter differs between "wired up to a real
/// backend" and "console/no-op for this demo".
/// </summary>
public static class TransactionGatewayTelemetry
{
    public const string SourceName = "TransactionGateway";

    public static readonly ActivitySource ActivitySource = new(SourceName);
    public static readonly Meter Meter = new(SourceName);

    public static readonly Counter<long> RequestsReceived =
        Meter.CreateCounter<long>("gateway.requests_received", description: "Total transaction submissions received");

    public static readonly Counter<long> DedupHits =
        Meter.CreateCounter<long>("gateway.dedup_hits", description: "Submissions short-circuited by the idempotency cache");

    public static readonly Counter<long> GrpcFailures =
        Meter.CreateCounter<long>("gateway.grpc_failures", description: "Forwarding attempts that failed after retries");

    public static readonly Counter<long> CacheFailOpens =
        Meter.CreateCounter<long>("gateway.cache_fail_opens", description: "Idempotency checks that failed open due to a cache error");

    public static readonly Counter<long> SearchRequestsReceived =
        Meter.CreateCounter<long>("gateway.search_requests_received", description: "Total search requests received, tagged by endpoint");

    public static readonly Counter<long> SearchMisses =
        Meter.CreateCounter<long>("gateway.search_misses", description: "transaction_no searches that found no matching row");
}
