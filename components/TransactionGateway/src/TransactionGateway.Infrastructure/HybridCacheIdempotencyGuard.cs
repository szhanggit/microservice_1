using Microsoft.Extensions.Caching.Hybrid;
using TransactionGateway.Application;

namespace TransactionGateway.Infrastructure;

/// <summary>
/// HybridCache (local memory L1 + Redis L2) backing for the idempotency
/// check (TransactionGateway.md §2). Deliberately lets exceptions propagate
/// (no internal try/catch) - TransactionSubmissionHandler is the layer
/// responsible for the fail-open decision, which keeps that behavior
/// independently unit-testable at the handler level.
/// </summary>
public sealed class HybridCacheIdempotencyGuard : IIdempotencyGuard
{
    private const string KeyPrefix = "txn-gateway:idempotency:";

    private readonly HybridCache _cache;

    public HybridCacheIdempotencyGuard(HybridCache cache)
    {
        _cache = cache;
    }

    public async Task<bool> HasBeenProcessedAsync(string transactionNo, CancellationToken cancellationToken)
    {
        try
        {
            // HybridCache only exposes GetOrCreateAsync (get-or-create), not a
            // plain "does this key exist" lookup. A factory that throws is the
            // standard trick to get TryGet-like semantics out of it: on a hit,
            // the cached value is returned and the factory never runs; on a
            // miss, the factory runs, throws, and - because it faulted -
            // HybridCache caches nothing, so this never "poisons" a future
            // check with a stale negative.
            await _cache.GetOrCreateAsync<bool>(
                CacheKey(transactionNo),
                static _ => throw new EntryNotFoundException(),
                cancellationToken: cancellationToken);
            return true;
        }
        catch (EntryNotFoundException)
        {
            return false;
        }
    }

    public async Task MarkProcessedAsync(string transactionNo, CancellationToken cancellationToken)
    {
        await _cache.SetAsync(CacheKey(transactionNo), true, cancellationToken: cancellationToken);
    }

    private static string CacheKey(string transactionNo) => $"{KeyPrefix}{transactionNo}";

    private sealed class EntryNotFoundException : Exception;
}
