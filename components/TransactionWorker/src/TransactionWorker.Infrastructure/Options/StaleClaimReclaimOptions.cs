namespace TransactionWorker.Infrastructure.Options;

public sealed class StaleClaimReclaimOptions
{
    /// <summary>Period between GSI scans for stale CLAIMED items (KEDA.md §5).</summary>
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromSeconds(30);
}
