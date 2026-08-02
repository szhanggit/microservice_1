namespace TransactionWorker.Application;

/// <summary>
/// DynamoDB-backed claim-check with lease (KEDA.md §5) - after a successful
/// claim, DynamoDB holds the only surviving copy of the message once it's
/// deleted from SQS.
/// </summary>
public interface IClaimStore
{
    Task<ClaimOutcome> TryClaimAsync(string transactionNo, string workerId, string payloadJson, CancellationToken cancellationToken);

    Task CompleteAsync(string transactionNo, CancellationToken cancellationToken);

    /// <summary>Scans the gsi_status_lease GSI for CLAIMED items whose lease_expiry has passed.</summary>
    Task<IReadOnlyList<StaleClaim>> FindStaleClaimsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Conditional update requiring lease_expiry still equals claim.OldLeaseExpiry -
    /// only one instance wins if several race to reclaim the same stale item.
    /// </summary>
    Task<bool> TryReclaimAsync(StaleClaim claim, string workerId, CancellationToken cancellationToken);
}
