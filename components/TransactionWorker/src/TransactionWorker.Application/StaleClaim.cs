namespace TransactionWorker.Application;

/// <summary>
/// A CLAIMED transaction-claims item whose lease has expired (KEDA.md §5) -
/// OldLeaseExpiry is carried along so the reclaim's conditional update can
/// require it still matches, ensuring only one instance wins the race.
/// </summary>
public sealed record StaleClaim(string TransactionNo, string Payload, long OldLeaseExpiry, int AttemptCount);
