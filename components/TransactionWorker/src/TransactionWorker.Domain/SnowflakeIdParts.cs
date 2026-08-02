namespace TransactionWorker.Domain;

/// <summary>Decoded fields of a Snowflake-style id (database.md §5).</summary>
public readonly record struct SnowflakeIdParts(long TimestampMs, int ShardId, int NodeId, int Sequence);
