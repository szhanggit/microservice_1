namespace TransactionGateway.Application;

public enum DateRangeSearchOutcome
{
    Success,

    /// <summary>Missing/unparseable from/to, from after to, or a negative offset - maps to HTTP 400.</summary>
    InvalidArgument,

    /// <summary>The gRPC call to TransactionService failed after retries - maps to HTTP 502.</summary>
    ForwardingFailed,
}

public sealed record DateRangeSearchResult(
    DateRangeSearchOutcome Outcome,
    IReadOnlyList<TransactionRecord>? Transactions = null,
    bool HasMore = false,
    string? ErrorMessage = null)
{
    public static DateRangeSearchResult Success(IReadOnlyList<TransactionRecord> transactions, bool hasMore) =>
        new(DateRangeSearchOutcome.Success, transactions, hasMore);

    public static DateRangeSearchResult Invalid(string message) => new(DateRangeSearchOutcome.InvalidArgument, ErrorMessage: message);

    public static DateRangeSearchResult ForwardingFailed() => new(DateRangeSearchOutcome.ForwardingFailed);
}
