namespace TransactionWorker.Application;

/// <summary>
/// Central orchestration for the claim-check-with-lease flow (KEDA.md §5).
/// Split into 2 steps for fresh messages (rather than one "handle everything"
/// call) specifically so the caller can delete the SQS message in between -
/// the whole point of the design is that SQS holds nothing once claimed, so
/// DynamoDB is what survives a crash, not SQS's own redelivery.
/// </summary>
public interface IMessageProcessor
{
    /// <summary>
    /// Step 2 of KEDA.md §5: conditional PutItem. Safe to delete the SQS
    /// message immediately after this returns, regardless of outcome -
    /// Claimed means this instance now owns the work; AlreadyClaimed means
    /// another instance already does, so this delivery is a harmless duplicate.
    /// </summary>
    Task<ClaimOutcome> ClaimNewMessageAsync(string transactionNo, string payloadJson, CancellationToken cancellationToken);

    /// <summary>
    /// Steps 4-5 of KEDA.md §5: resolve shard, generate id, insert, mark
    /// COMPLETED. If this throws, the DynamoDB item is left CLAIMED with its
    /// lease still running - the stale-claim scan will pick it up later,
    /// which is the crash-recovery path working as designed, not a bug.
    /// </summary>
    Task CompleteProcessingAsync(string transactionNo, string payloadJson, CancellationToken cancellationToken);

    /// <summary>
    /// Attempts to win a stale claim's reclaim race (conditional update on the
    /// old lease_expiry); if won, resumes directly from the claim's stored
    /// payload - no re-read from SQS is possible or needed, since SQS deleted
    /// this message back when it was first claimed. Returns whether this
    /// instance won the race.
    /// </summary>
    Task<bool> TryResumeStaleClaimAsync(StaleClaim claim, CancellationToken cancellationToken);
}
