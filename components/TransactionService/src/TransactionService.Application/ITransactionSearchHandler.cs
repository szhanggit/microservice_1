namespace TransactionService.Application;

/// <summary>Orchestration for the two search RPCs (TransactionService.md §5).</summary>
public interface ITransactionSearchHandler
{
    Task<TransactionNoSearchResult> SearchByTransactionNoAsync(string transactionNo, CancellationToken cancellationToken);

    Task<DateRangeSearchResult> SearchByDateRangeAsync(string from, string to, int limit, int offset, CancellationToken cancellationToken);
}
