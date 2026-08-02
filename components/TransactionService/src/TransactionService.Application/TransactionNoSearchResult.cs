namespace TransactionService.Application;

public enum TransactionNoSearchOutcome
{
    Found,

    /// <summary>Not a gRPC error - a search miss is a normal outcome (TransactionService.md §5).</summary>
    NotFound,

    /// <summary>An empty transaction_no - maps to a gRPC INVALID_ARGUMENT.</summary>
    InvalidArgument,
}

public sealed record TransactionNoSearchResult(TransactionNoSearchOutcome Outcome, TransactionRecord? Transaction = null, string? ErrorMessage = null)
{
    public static TransactionNoSearchResult Found(TransactionRecord transaction) => new(TransactionNoSearchOutcome.Found, transaction);

    public static TransactionNoSearchResult NotFound() => new(TransactionNoSearchOutcome.NotFound);

    public static TransactionNoSearchResult Invalid(string message) => new(TransactionNoSearchOutcome.InvalidArgument, ErrorMessage: message);
}
