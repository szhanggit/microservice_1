namespace TransactionService.Application;

/// <summary>A full transaction row read from a shard or the reporting instance (database.md §6/§7).</summary>
public sealed record TransactionRecord(
    long Id,
    string TransactionNo,
    string TransactionDatetime,
    string Amount,
    string Type,
    string Status,
    string Currency,
    string SystemDatetime);
