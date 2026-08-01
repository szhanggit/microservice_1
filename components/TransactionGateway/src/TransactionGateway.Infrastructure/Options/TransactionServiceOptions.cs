namespace TransactionGateway.Infrastructure.Options;

public sealed class TransactionServiceOptions
{
    /// <summary>
    /// In-cluster address of TransactionService's gRPC ClusterIP Service
    /// (e.g. http://transaction-service:5000), or localhost for docker-compose
    /// local dev (TransactionGateway.md §7).
    /// </summary>
    public string GrpcAddress { get; set; } = "http://localhost:5001";
}
