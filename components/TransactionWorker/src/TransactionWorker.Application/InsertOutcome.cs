namespace TransactionWorker.Application;

public enum InsertOutcome
{
    Inserted,

    /// <summary>
    /// Postgres unique-violation on transaction_no (database.md §6) - treated
    /// as "already processed", not an error (KEDA.md §5's defense-in-depth).
    /// </summary>
    AlreadyExists,
}
