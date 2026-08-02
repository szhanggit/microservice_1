namespace TransactionWorker.Domain;

/// <summary>
/// Generates/decodes the 64-bit Snowflake-style id from database.md §5 -
/// 1 reserved bit + 41-bit timestamp + 5-bit shard_id + 5-bit node_id +
/// 12-bit sequence. Generated in the application, not by a Postgres
/// SERIAL/IDENTITY sequence (which is only unique per-instance).
/// </summary>
public interface ISnowflakeIdGenerator
{
    long NextId(int shardId);

    SnowflakeIdParts Decode(long id);
}
