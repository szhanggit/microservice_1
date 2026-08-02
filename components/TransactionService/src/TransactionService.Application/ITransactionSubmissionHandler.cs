namespace TransactionService.Application;

public interface ITransactionSubmissionHandler
{
    /// <summary>
    /// Orchestrates validate -> dedup check -> send -> mark-processed
    /// (TransactionService.md §3).
    /// </summary>
    Task<SubmissionResult> HandleAsync(TransactionSubmission submission, CancellationToken cancellationToken);
}
