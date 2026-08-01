using ShardRouting;

namespace ShardRouting.Tests;

public class Murmur3ShardRouterTests
{
    private readonly Murmur3ShardRouter _router = new();

    [Theory]
    [InlineData("TXN-1")]
    [InlineData("TXN-2")]
    [InlineData("a-completely-different-key")]
    public void ResolveShardId_is_deterministic_for_the_same_transaction_no(string transactionNo)
    {
        var first = _router.ResolveShardId(transactionNo);
        var second = _router.ResolveShardId(transactionNo);

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("TXN-1")]
    [InlineData("TXN-2")]
    [InlineData("")]
    public void ResolveShardId_always_returns_a_value_in_range(string suffix)
    {
        var shardId = _router.ResolveShardId($"TXN-{suffix}-stable");

        Assert.InRange(shardId, 0, Murmur3ShardRouter.ShardCount - 1);
    }

    [Fact]
    public void ResolveShardId_throws_for_an_empty_transaction_no()
    {
        Assert.Throws<ArgumentException>(() => _router.ResolveShardId(string.Empty));
    }

    [Fact]
    public void Distinct_transaction_numbers_spread_across_all_3_shards()
    {
        var seen = new HashSet<int>();
        for (var i = 0; i < 100; i++)
        {
            seen.Add(_router.ResolveShardId($"TXN-{i}"));
        }

        Assert.Equal(Murmur3ShardRouter.ShardCount, seen.Count);
    }
}
