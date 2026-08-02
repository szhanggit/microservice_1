using System.Globalization;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace TransactionGateway.Application;

/// <summary>
/// Orchestration for the two search HTTP endpoints (TransactionGateway.md
/// §5): validate query params -> default/clamp pagination -> gRPC call ->
/// response. No dedup cache involved (§2) - these are plain reads.
/// </summary>
public sealed class TransactionSearchHandler : ITransactionSearchHandler
{
    private const int MinLimit = 1;
    private const int MaxLimit = 500;
    private const int DefaultLimit = 50;

    private static readonly DateTimeStyles DateTimeStyle = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

    private readonly ITransactionSearchForwarder _forwarder;
    private readonly ILogger<TransactionSearchHandler> _logger;

    public TransactionSearchHandler(ITransactionSearchForwarder forwarder, ILogger<TransactionSearchHandler> logger)
    {
        _forwarder = forwarder;
        _logger = logger;
    }

    public async Task<TransactionNoSearchResult> SearchByTransactionNoAsync(string transactionNo, CancellationToken cancellationToken)
    {
        using var activity = TransactionGatewayTelemetry.ActivitySource.StartActivity("SearchByTransactionNo");
        activity?.SetTag("transaction_no", transactionNo);
        TransactionGatewayTelemetry.SearchRequestsReceived.Add(1, new KeyValuePair<string, object?>("endpoint", "SearchByTransactionNo"));

        if (string.IsNullOrWhiteSpace(transactionNo))
        {
            return TransactionNoSearchResult.Invalid("transaction_no is required");
        }

        try
        {
            var record = await _forwarder.SearchByTransactionNoAsync(transactionNo, cancellationToken);
            if (record is null)
            {
                activity?.SetTag("found", false);
                TransactionGatewayTelemetry.SearchMisses.Add(1);
                return TransactionNoSearchResult.NotFound();
            }

            activity?.SetTag("found", true);
            return TransactionNoSearchResult.Found(record);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument)
        {
            return TransactionNoSearchResult.Invalid(ex.Status.Detail);
        }
        catch (Exception ex)
        {
            activity?.SetTag("grpc_failure", true);
            TransactionGatewayTelemetry.GrpcFailures.Add(1);
            _logger.LogError(ex, "Failed to search for {TransactionNo}", transactionNo);
            return TransactionNoSearchResult.ForwardingFailed();
        }
    }

    public async Task<DateRangeSearchResult> SearchByDateRangeAsync(string from, string to, int? limit, int? offset, CancellationToken cancellationToken)
    {
        using var activity = TransactionGatewayTelemetry.ActivitySource.StartActivity("SearchByDateRange");
        TransactionGatewayTelemetry.SearchRequestsReceived.Add(1, new KeyValuePair<string, object?>("endpoint", "SearchByDateRange"));

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

        if (offset is < 0)
        {
            return DateRangeSearchResult.Invalid("offset must not be negative");
        }

        // TransactionGateway.md §2: the edge applies the default/clamp so it
        // lives in one place, closest to the client; TransactionService
        // re-validates independently anyway.
        var effectiveLimit = limit is null or <= 0 ? DefaultLimit : Math.Clamp(limit.Value, MinLimit, MaxLimit);
        var effectiveOffset = offset ?? 0;

        activity?.SetTag("from", from);
        activity?.SetTag("to", to);
        activity?.SetTag("limit", effectiveLimit);
        activity?.SetTag("offset", effectiveOffset);

        try
        {
            var page = await _forwarder.SearchByDateRangeAsync(fromDate, toDate, effectiveLimit, effectiveOffset, cancellationToken);
            activity?.SetTag("result_count", page.Items.Count);
            activity?.SetTag("has_more", page.HasMore);
            return DateRangeSearchResult.Success(page.Items, page.HasMore);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument)
        {
            return DateRangeSearchResult.Invalid(ex.Status.Detail);
        }
        catch (Exception ex)
        {
            activity?.SetTag("grpc_failure", true);
            TransactionGatewayTelemetry.GrpcFailures.Add(1);
            _logger.LogError(ex, "Failed to search date range {From} to {To}", from, to);
            return DateRangeSearchResult.ForwardingFailed();
        }
    }
}
