namespace TransactionGateway.Application;

/// <summary>One page of a date-range search, as returned by TransactionService (TransactionGateway.md §5).</summary>
public sealed record TransactionSearchPage(IReadOnlyList<TransactionRecord> Items, bool HasMore);
