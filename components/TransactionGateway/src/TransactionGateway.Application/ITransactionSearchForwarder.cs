namespace TransactionGateway.Application;

/// <summary>
/// Forwards search requests to TransactionService over gRPC
/// (TransactionGateway.md §5). Throws Grpc.Core.RpcException on a
/// transport/validation failure - the handler is responsible for mapping
/// that to the right HTTP status.
/// </summary>
public interface ITransactionSearchForwarder
{
    /// <summary>Returns null when TransactionService reports found = false.</summary>
    Task<TransactionRecord?> SearchByTransactionNoAsync(string transactionNo, CancellationToken cancellationToken);

    Task<TransactionSearchPage> SearchByDateRangeAsync(DateTimeOffset from, DateTimeOffset to, int limit, int offset, CancellationToken cancellationToken);
}
