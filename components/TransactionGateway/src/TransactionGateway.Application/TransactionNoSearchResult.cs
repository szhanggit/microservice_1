namespace TransactionGateway.Application;

public enum TransactionNoSearchOutcome
{
    Found,

    /// <summary>Not an error - maps to HTTP 404 (TransactionGateway.md §2).</summary>
    NotFound,

    /// <summary>An empty transaction_no - maps to HTTP 400.</summary>
    InvalidArgument,

    /// <summary>The gRPC call to TransactionService failed after retries - maps to HTTP 502.</summary>
    ForwardingFailed,
}

public sealed record TransactionNoSearchResult(TransactionNoSearchOutcome Outcome, TransactionRecord? Transaction = null, string? ErrorMessage = null)
{
    public static TransactionNoSearchResult Found(TransactionRecord transaction) => new(TransactionNoSearchOutcome.Found, transaction);

    public static TransactionNoSearchResult NotFound() => new(TransactionNoSearchOutcome.NotFound);

    public static TransactionNoSearchResult Invalid(string message) => new(TransactionNoSearchOutcome.InvalidArgument, ErrorMessage: message);

    public static TransactionNoSearchResult ForwardingFailed() => new(TransactionNoSearchOutcome.ForwardingFailed);
}
