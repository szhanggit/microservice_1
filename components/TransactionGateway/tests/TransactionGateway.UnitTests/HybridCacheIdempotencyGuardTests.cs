using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using TransactionGateway.Infrastructure;

namespace TransactionGateway.UnitTests;

/// <summary>
/// Exercises the real HybridCache (in-memory L1 only, no Redis L2 registered)
/// rather than mocking it - HybridCache is resolved from a real, minimal DI
/// container, so this never touches live Redis (TransactionGateway.md §4).
/// </summary>
public class HybridCacheIdempotencyGuardTests
{
    private static HybridCacheIdempotencyGuard CreateGuard()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        var provider = services.BuildServiceProvider();
        return new HybridCacheIdempotencyGuard(provider.GetRequiredService<HybridCache>());
    }

    [Fact]
    public async Task Fresh_key_has_not_been_processed()
    {
        var guard = CreateGuard();

        var result = await guard.HasBeenProcessedAsync("TXN-1", CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task Marked_key_is_reported_as_processed()
    {
        var guard = CreateGuard();

        await guard.MarkProcessedAsync("TXN-1", CancellationToken.None);
        var result = await guard.HasBeenProcessedAsync("TXN-1", CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task Marking_one_key_does_not_affect_another()
    {
        var guard = CreateGuard();

        await guard.MarkProcessedAsync("TXN-1", CancellationToken.None);
        var result = await guard.HasBeenProcessedAsync("TXN-2", CancellationToken.None);

        Assert.False(result);
    }
}
