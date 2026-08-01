using Microsoft.Extensions.Options;

namespace TransactionWorker.Domain;

/// <summary>
/// Thread-safe Snowflake-style id generator matching database.md §5's bit
/// layout exactly:
/// <code>
///  1 bit   41 bits              5 bits     5 bits      12 bits
/// ┌───┬─────────────────────┬──────────┬──────────┬──────────────┐
/// │ 0 │ ms since custom epoch│ shard_id │ node_id  │  sequence    │
/// └───┴─────────────────────┴──────────┴──────────┴──────────────┘
/// </code>
/// The leading bit is always 0 so the value fits in a signed 64-bit integer
/// (Postgres has no unsigned integer type - the `id` column is a plain BIGINT).
/// </summary>
public sealed class SnowflakeIdGenerator : ISnowflakeIdGenerator
{
    private const int SequenceBits = 12;
    private const int NodeIdBits = 5;
    private const int ShardIdBits = 5;

    private const int SequenceMask = (1 << SequenceBits) - 1; // 4095
    private const int NodeIdMask = (1 << NodeIdBits) - 1; // 31
    private const int ShardIdMask = (1 << ShardIdBits) - 1; // 31

    private const int NodeIdShift = SequenceBits; // 12
    private const int ShardIdShift = SequenceBits + NodeIdBits; // 17
    private const int TimestampShift = SequenceBits + NodeIdBits + ShardIdBits; // 22

    // Custom epoch (database.md §5: "e.g. 2025-01-01"), good for ~69 years from here.
    private static readonly DateTimeOffset Epoch = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly int _nodeId;
    private readonly object _lock = new();
    private long _lastTimestampMs = -1;
    private int _sequence;

    public SnowflakeIdGenerator(IOptions<SnowflakeIdOptions> options)
    {
        _nodeId = options.Value.NodeId & NodeIdMask;
    }

    public long NextId(int shardId)
    {
        if (shardId is < 0 or > ShardIdMask)
        {
            throw new ArgumentOutOfRangeException(nameof(shardId), shardId, $"shard_id must fit in {ShardIdBits} bits (0-{ShardIdMask}).");
        }

        lock (_lock)
        {
            var timestampMs = CurrentTimestampMs();

            if (timestampMs < _lastTimestampMs)
            {
                throw new InvalidOperationException(
                    $"Clock moved backwards: last={_lastTimestampMs}ms, now={timestampMs}ms. Refusing to generate a duplicate id.");
            }

            if (timestampMs == _lastTimestampMs)
            {
                _sequence = (_sequence + 1) & SequenceMask;
                if (_sequence == 0)
                {
                    // Sequence exhausted within this millisecond - spin until the clock ticks over.
                    timestampMs = WaitForNextMillisecond(_lastTimestampMs);
                }
            }
            else
            {
                _sequence = 0;
            }

            _lastTimestampMs = timestampMs;

            return (timestampMs << TimestampShift)
                   | ((long)shardId << ShardIdShift)
                   | ((long)_nodeId << NodeIdShift)
                   | (uint)_sequence;
        }
    }

    public SnowflakeIdParts Decode(long id)
    {
        var sequence = (int)(id & SequenceMask);
        var nodeId = (int)((id >> NodeIdShift) & NodeIdMask);
        var shardId = (int)((id >> ShardIdShift) & ShardIdMask);
        var timestampMs = id >> TimestampShift;
        return new SnowflakeIdParts(timestampMs, shardId, nodeId, sequence);
    }

    private static long CurrentTimestampMs() => (long)(DateTimeOffset.UtcNow - Epoch).TotalMilliseconds;

    private static long WaitForNextMillisecond(long lastTimestampMs)
    {
        long now;
        do
        {
            now = CurrentTimestampMs();
        } while (now <= lastTimestampMs);

        return now;
    }
}
