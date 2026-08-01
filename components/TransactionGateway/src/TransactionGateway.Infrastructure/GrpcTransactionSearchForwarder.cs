using Contracts.Transactions;
using Polly;
using TransactionGateway.Application;

namespace TransactionGateway.Infrastructure;

/// <summary>
/// Forwards search requests to TransactionService.SearchByTransactionNo /
/// SearchByDateRange over gRPC (TransactionGateway.md §5). Same transport-
/// transience-only retry policy as GrpcTransactionForwarder - an
/// InvalidArgument from TransactionService's own re-validation is not
/// retried and propagates immediately for the handler to map to HTTP 400.
/// </summary>
public sealed class GrpcTransactionSearchForwarder : ITransactionSearchForwarder
{
    private readonly TransactionService.TransactionServiceClient _client;
    private readonly ResiliencePipeline _retryPipeline;

    public GrpcTransactionSearchForwarder(TransactionService.TransactionServiceClient client)
        : this(client, GrpcTransactionForwarder.BuildDefaultRetryPipeline())
    {
    }

    // Internal constructor so tests can inject a fast/no-op pipeline instead
    // of waiting through real retry backoff.
    internal GrpcTransactionSearchForwarder(TransactionService.TransactionServiceClient client, ResiliencePipeline retryPipeline)
    {
        _client = client;
        _retryPipeline = retryPipeline;
    }

    public async Task<TransactionRecord?> SearchByTransactionNoAsync(string transactionNo, CancellationToken cancellationToken)
    {
        var request = new SearchByTransactionNoRequest { TransactionNo = transactionNo };

        var response = await _retryPipeline.ExecuteAsync(
            async ct => await _client.SearchByTransactionNoAsync(request, cancellationToken: ct).ResponseAsync,
            cancellationToken);

        return response.Found ? ToRecord(response.Transaction) : null;
    }

    public async Task<TransactionSearchPage> SearchByDateRangeAsync(DateTimeOffset from, DateTimeOffset to, int limit, int offset, CancellationToken cancellationToken)
    {
        var request = new SearchByDateRangeRequest
        {
            From = from.ToString("O"),
            To = to.ToString("O"),
            Limit = limit,
            Offset = offset,
        };

        var response = await _retryPipeline.ExecuteAsync(
            async ct => await _client.SearchByDateRangeAsync(request, cancellationToken: ct).ResponseAsync,
            cancellationToken);

        return new TransactionSearchPage(response.Transactions.Select(ToRecord).ToList(), response.HasMore);
    }

    private static TransactionRecord ToRecord(Transaction transaction) => new(
        transaction.Id,
        transaction.TransactionNo,
        transaction.TransactionDatetime,
        transaction.Amount,
        transaction.Type,
        transaction.Status,
        transaction.Currency,
        transaction.SystemDatetime);
}
