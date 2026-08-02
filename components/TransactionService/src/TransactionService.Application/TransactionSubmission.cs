namespace TransactionService.Application;

/// <summary>
/// Wire-agnostic representation of a submission received over gRPC. Amount
/// stays a string throughout (TransactionService.md §5) to avoid
/// floating-point precision issues.
/// </summary>
public sealed record TransactionSubmission(
    string TransactionNo,
    string TransactionDatetime,
    string Amount,
    string Type,
    string Status,
    string Currency);
