namespace TransactionGateway.Application;

public interface ITransactionSearchHandler
{
    Task<TransactionNoSearchResult> SearchByTransactionNoAsync(string transactionNo, CancellationToken cancellationToken);

    /// <summary>limit/offset are nullable to reflect the optional HTTP query params - this handler applies the default/clamp (TransactionGateway.md §2).</summary>
    Task<DateRangeSearchResult> SearchByDateRangeAsync(string from, string to, int? limit, int? offset, CancellationToken cancellationToken);
}
