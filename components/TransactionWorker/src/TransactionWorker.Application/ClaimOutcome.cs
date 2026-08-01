namespace TransactionWorker.Application;

public enum ClaimOutcome
{
    /// <summary>Fresh claim - this instance now owns processing this transaction_no.</summary>
    Claimed,

    /// <summary>
    /// Someone already claimed (or completed) this transaction_no - a duplicate
    /// SQS delivery (KEDA.md §5). Discard; the original claim owns the work.
    /// </summary>
    AlreadyClaimed,
}
