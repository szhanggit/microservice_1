using System.Globalization;
using Microsoft.Extensions.Logging;
using ShardRouting;

namespace TransactionService.Application;

/// <summary>
/// Orchestration for the two search RPCs (TransactionService.md §5). A plain
/// read path - no dedup cache, no SQS, independent of TransactionSubmissionHandler.
/// </summary>
public sealed class TransactionSearchHandler : ITransactionSearchHandler
{
    private const int MinLimit = 1;
    private const int MaxLimit = 500;
    private const int DefaultLimit = 50;

    private static readonly DateTimeStyles DateTimeStyle = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

    private readonly IShardRouter _shardRouter;
    private readonly ITransactionSearchRepository _repository;
    private readonly ILogger<TransactionSearchHandler> _logger;

    public TransactionSearchHandler(IShardRouter shardRouter, ITransactionSearchRepository repository, ILogger<TransactionSearchHandler> logger)
    {
        _shardRouter = shardRouter;
        _repository = repository;
        _logger = logger;
    }

    public async Task<TransactionNoSearchResult> SearchByTransactionNoAsync(string transactionNo, CancellationToken cancellationToken)
    {
        using var activity = TransactionServiceTelemetry.ActivitySource.StartActivity("SearchByTransactionNo");
        activity?.SetTag("transaction_no", transactionNo);
        TransactionServiceTelemetry.SearchRequestsReceived.Add(1, new KeyValuePair<string, object?>("rpc", "SearchByTransactionNo"));

        if (string.IsNullOrWhiteSpace(transactionNo))
        {
            return TransactionNoSearchResult.Invalid("transaction_no is required");
        }

        var shardId = _shardRouter.ResolveShardId(transactionNo);
        activity?.SetTag("shard_id", shardId);
        TransactionServiceTelemetry.SearchByShard.Add(1, new KeyValuePair<string, object?>("shard_id", shardId));

        var record = await _repository.FindByTransactionNoAsync(transactionNo, shardId, cancellationToken);
        if (record is null)
        {
            activity?.SetTag("found", false);
            TransactionServiceTelemetry.SearchMisses.Add(1);
            _logger.LogInformation("No transaction found for {TransactionNo} in shard {ShardId}", transactionNo, shardId);
            return TransactionNoSearchResult.NotFound();
        }

        activity?.SetTag("found", true);
        return TransactionNoSearchResult.Found(record);
    }

    public async Task<DateRangeSearchResult> SearchByDateRangeAsync(string from, string to, int limit, int offset, CancellationToken cancellationToken)
    {
        using var activity = TransactionServiceTelemetry.ActivitySource.StartActivity("SearchByDateRange");
        TransactionServiceTelemetry.SearchRequestsReceived.Add(1, new KeyValuePair<string, object?>("rpc", "SearchByDateRange"));

        if (!DateTimeOffset.TryParse(from, CultureInfo.InvariantCulture, DateTimeStyle, out var fromDate))
        {
            return DateRangeSearchResult.Invalid("from is not a valid ISO-8601 datetime");
        }

        if (!DateTimeOffset.TryParse(to, CultureInfo.InvariantCulture, DateTimeStyle, out var toDate))
        {
            return DateRangeSearchResult.Invalid("to is not a valid ISO-8601 datetime");
        }

        if (fromDate > toDate)
        {
            return DateRangeSearchResult.Invalid("from must not be after to");
        }

        if (offset < 0)
        {
            return DateRangeSearchResult.Invalid("offset must not be negative");
        }

        // Defense in depth - TransactionGateway already defaults/clamps before
        // calling (TransactionGateway.md §5), but this service re-validates
        // independently, same reasoning as field validation on the write path (§2).
        var effectiveLimit = limit <= 0 ? DefaultLimit : Math.Clamp(limit, MinLimit, MaxLimit);

        activity?.SetTag("from", from);
        activity?.SetTag("to", to);
        activity?.SetTag("limit", effectiveLimit);
        activity?.SetTag("offset", offset);

        var page = await _repository.FindByDateRangeAsync(fromDate, toDate, effectiveLimit, offset, cancellationToken);

        activity?.SetTag("result_count", page.Items.Count);
        activity?.SetTag("has_more", page.HasMore);

        return DateRangeSearchResult.Success(page.Items, page.HasMore);
    }
}
