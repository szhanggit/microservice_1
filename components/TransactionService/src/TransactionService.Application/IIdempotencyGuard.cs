namespace TransactionService.Application;

/// <summary>
/// Best-effort dedup on transaction_no, guarding against a duplicate gRPC
/// call from the Gateway (TransactionService.md §2). Callers must treat any
/// exception as fail-open - the DB's UNIQUE constraint is the real backstop.
/// </summary>
public interface IIdempotencyGuard
{
    Task<bool> HasBeenProcessedAsync(string transactionNo, CancellationToken cancellationToken);

    Task MarkProcessedAsync(string transactionNo, CancellationToken cancellationToken);
}
