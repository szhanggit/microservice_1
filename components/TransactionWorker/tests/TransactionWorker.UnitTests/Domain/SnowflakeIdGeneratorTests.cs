using Microsoft.Extensions.Options;
using TransactionWorker.Domain;

namespace TransactionWorker.UnitTests.Domain;

public class SnowflakeIdGeneratorTests
{
    private static SnowflakeIdGenerator CreateGenerator(int nodeId = 1) =>
        new(Options.Create(new SnowflakeIdOptions { NodeId = nodeId }));

    [Fact]
    public void NextId_embeds_the_requested_shard_id_and_configured_node_id()
    {
        var generator = CreateGenerator(nodeId: 7);

        var id = generator.NextId(shardId: 2);
        var decoded = generator.Decode(id);

        Assert.Equal(2, decoded.ShardId);
        Assert.Equal(7, decoded.NodeId);
    }

    [Fact]
    public void NextId_produces_unique_ids_under_sequential_generation_on_the_same_shard()
    {
        var generator = CreateGenerator();
        var ids = new HashSet<long>();

        for (var i = 0; i < 5000; i++)
        {
            Assert.True(ids.Add(generator.NextId(shardId: 0)));
        }
    }

    [Fact]
    public void NextId_produces_unique_ids_under_concurrent_generation()
    {
        var generator = CreateGenerator();
        var bag = new System.Collections.Concurrent.ConcurrentBag<long>();

        Parallel.For(0, 5000, _ => bag.Add(generator.NextId(shardId: 1)));

        Assert.Equal(bag.Count, bag.Distinct().Count());
    }

    [Fact]
    public void Decode_is_the_inverse_of_NextId_for_every_component()
    {
        var generator = CreateGenerator(nodeId: 15);

        var id = generator.NextId(shardId: 2);
        var decoded = generator.Decode(id);

        Assert.Equal(2, decoded.ShardId);
        Assert.Equal(15, decoded.NodeId);
        Assert.InRange(decoded.TimestampMs, 0, long.MaxValue);
        Assert.InRange(decoded.Sequence, 0, 4095);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(32)]
    public void NextId_rejects_a_shard_id_outside_the_5_bit_range(int shardId)
    {
        var generator = CreateGenerator();

        Assert.Throws<ArgumentOutOfRangeException>(() => generator.NextId(shardId));
    }

    [Fact]
    public void NodeId_configured_outside_the_5_bit_range_is_masked_down()
    {
        // database.md §5: node_id is 5 bits (0-31) - a caller-supplied value
        // outside that range is masked rather than throwing, since NodeId is
        // derived (hashed) rather than hand-picked (see the Infrastructure DI
        // wiring), and a hash's raw output isn't naturally in-range.
        var generator = CreateGenerator(nodeId: 40);

        var decoded = generator.Decode(generator.NextId(shardId: 0));

        Assert.Equal(40 & 0b11111, decoded.NodeId);
    }
}
