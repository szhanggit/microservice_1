using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TransactionWorker.Application;

/// <summary>
/// Named ActivitySource/Meter registered with OpenTelemetry in Program.cs
/// (KEDA.md §7). Instrumented unconditionally - only the exporter differs
/// between "shipped to a real backend" and "console/no-op for this demo".
/// </summary>
public static class TransactionWorkerTelemetry
{
    public const string SourceName = "TransactionWorker";

    public static readonly ActivitySource ActivitySource = new(SourceName);
    public static readonly Meter Meter = new(SourceName);

    public static readonly Counter<long> MessagesProcessed =
        Meter.CreateCounter<long>("worker.messages_processed", description: "Transactions successfully inserted (fresh or duplicate-key-as-success)");

    public static readonly Counter<long> ClaimConflicts =
        Meter.CreateCounter<long>("worker.claim_conflicts", description: "Duplicate SQS deliveries rejected by the conditional claim PutItem");

    public static readonly Counter<long> Reclaims =
        Meter.CreateCounter<long>("worker.reclaims", description: "Stale claims successfully reclaimed and resumed after a worker crash");

    /// <summary>
    /// Per-shard insert counter - the key signal for verifying the hash
    /// routing distributes evenly (database.md §10).
    /// </summary>
    public static readonly Counter<long> ShardInserts =
        Meter.CreateCounter<long>("worker.shard_inserts", description: "Inserts per shard_id");
}
