namespace TransactionService.Application;

/// <summary>Sends a submission to Amazon SQS TransactionQueue (SQS.md).</summary>
public interface ISqsMessageForwarder
{
    Task SendAsync(TransactionSubmission submission, CancellationToken cancellationToken);
}
