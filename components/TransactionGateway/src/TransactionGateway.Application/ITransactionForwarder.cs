namespace TransactionGateway.Application;

/// <summary>
/// Forwards a submission to TransactionService over gRPC. Returns whether
/// TransactionService accepted it (i.e. the message reached SQS) - throws on
/// a transport-level failure after retries are exhausted.
/// </summary>
public interface ITransactionForwarder
{
    Task<bool> ForwardAsync(TransactionSubmission submission, CancellationToken cancellationToken);
}
