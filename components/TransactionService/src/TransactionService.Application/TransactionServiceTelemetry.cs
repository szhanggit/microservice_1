using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TransactionService.Application;

/// <summary>
/// Named ActivitySource/Meter registered with OpenTelemetry in Program.cs
/// (TransactionService.md §8). Instrumented unconditionally - only the
/// exporter differs between "shipped to a real backend" and "console/no-op
/// for this demo".
/// </summary>
public static class TransactionServiceTelemetry
{
    public const string SourceName = "TransactionService";

    public static readonly ActivitySource ActivitySource = new(SourceName);
    public static readonly Meter Meter = new(SourceName);

    public static readonly Counter<long> RequestsReceived =
        Meter.CreateCounter<long>("service.requests_received", description: "Total gRPC submissions received");

    public static readonly Counter<long> ValidationFailures =
        Meter.CreateCounter<long>("service.validation_failures", description: "Submissions rejected by field validation");

    public static readonly Counter<long> DedupHits =
        Meter.CreateCounter<long>("service.dedup_hits", description: "Submissions short-circuited by the idempotency cache");

    public static readonly Counter<long> SendFailures =
        Meter.CreateCounter<long>("service.send_failures", description: "SendMessage attempts that failed");

    public static readonly Counter<long> CacheFailOpens =
        Meter.CreateCounter<long>("service.cache_fail_opens", description: "Idempotency checks that failed open due to a cache error");

    public static readonly Counter<long> SearchRequestsReceived =
        Meter.CreateCounter<long>("service.search_requests_received", description: "Total search RPCs received, tagged by rpc name");

    public static readonly Counter<long> SearchMisses =
        Meter.CreateCounter<long>("service.search_misses", description: "SearchByTransactionNo calls that found no matching row");

    /// <summary>Per-shard search counter - same "is the hash routing even" signal as TransactionWorker's per-shard insert counter (KEDA.md §7).</summary>
    public static readonly Counter<long> SearchByShard =
        Meter.CreateCounter<long>("service.search_by_shard", description: "SearchByTransactionNo lookups per shard_id");
}
