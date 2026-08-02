namespace TransactionWorker.Application;

/// <summary>Fully-resolved row ready to insert into a shard (database.md §6).</summary>
public sealed record TransactionRecord(
    long Id,
    string TransactionNo,
    string TransactionDatetime,
    string Amount,
    string Type,
    string Status,
    string Currency);
