namespace TransactionService.Application;

/// <summary>
/// One page of a date-range search. HasMore is computed by the repository
/// fetching one row beyond `limit` and trimming it off, rather than a
/// separate COUNT(*) query (TransactionService.md §5).
/// </summary>
public sealed record TransactionSearchPage(IReadOnlyList<TransactionRecord> Items, bool HasMore);
