namespace TransactionWorker.Application;

/// <summary>
/// This app instance's identity, derived from the pod ordinal/hostname
/// (database.md §5, KEDA.md §5) - used both as DynamoDB's worker_id
/// attribute and (hashed) as the Snowflake id generator's node_id.
/// </summary>
public interface IWorkerIdentity
{
    string WorkerId { get; }
}
