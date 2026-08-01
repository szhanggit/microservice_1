namespace TransactionWorker.Domain;

public sealed class SnowflakeIdOptions
{
    /// <summary>
    /// Identifies this app instance/generator (0-31) so multiple concurrently
    /// running instances mint ids without colliding (database.md §5) -
    /// derived from the pod ordinal/hostname at startup (see
    /// TransactionWorker.Infrastructure's DI wiring), not set by hand.
    /// </summary>
    public int NodeId { get; set; }
}
