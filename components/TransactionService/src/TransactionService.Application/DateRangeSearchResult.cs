namespace TransactionService.Application;

public enum DateRangeSearchOutcome
{
    Success,

    /// <summary>Unparseable from/to, from after to, or a negative offset - maps to a gRPC INVALID_ARGUMENT.</summary>
    InvalidArgument,
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
}
