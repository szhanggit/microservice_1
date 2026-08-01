using Microsoft.Extensions.Caching.Hybrid;
using TransactionService.Application;

namespace TransactionService.Infrastructure;

/// <summary>
/// HybridCache (local memory L1 + Redis L2) backing for the idempotency
/// check (TransactionService.md §2). Deliberately lets exceptions propagate
/// (no internal try/catch) - TransactionSubmissionHandler owns the fail-open
/// decision, which keeps that behavior independently unit-testable at the
/// handler level.
/// </summary>
public sealed class HybridCacheIdempotencyGuard : IIdempotencyGuard
{
    private const string KeyPrefix = "txn-service:idempotency:";

    private readonly HybridCache _cache;

    public HybridCacheIdempotencyGuard(HybridCache cache)
    {
        _cache = cache;
    }

    public async Task<bool> HasBeenProcessedAsync(string transactionNo, CancellationToken cancellationToken)
    {
        try
        {
            // See TransactionGateway's HybridCacheIdempotencyGuard for why this
            // throw-as-miss-signal trick is used: HybridCache only exposes
            // GetOrCreateAsync, and a factory that throws caches nothing, so a
            // miss never poisons a future check with a stale negative.
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
