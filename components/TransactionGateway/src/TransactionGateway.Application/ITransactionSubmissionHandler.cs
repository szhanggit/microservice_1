namespace TransactionGateway.Application;

public interface ITransactionSubmissionHandler
{
    /// <summary>
    /// Orchestrates dedup check -> forward -> mark-processed. Returns true for
    /// both a fresh acceptance and a deduped retry (both map to HTTP 202 -
    /// TransactionGateway.md §2); false means the caller should surface a
    /// failure response.
    /// </summary>
    Task<bool> HandleAsync(TransactionSubmission submission, CancellationToken cancellationToken);
}
