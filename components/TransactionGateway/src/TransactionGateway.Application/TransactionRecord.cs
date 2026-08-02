namespace TransactionGateway.Application;

/// <summary>A full transaction row, as returned by TransactionService's search RPCs (TransactionGateway.md §5).</summary>
public sealed record TransactionRecord(
    long Id,
    string TransactionNo,
    string TransactionDatetime,
    string Amount,
    string Type,
    string Status,
    string Currency,
    string SystemDatetime);
