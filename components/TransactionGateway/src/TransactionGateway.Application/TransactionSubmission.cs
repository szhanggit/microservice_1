namespace TransactionGateway.Application;

/// <summary>
/// Transport-agnostic representation of a transaction submission - the HTTP
/// layer maps its DTO into this, and the gRPC forwarder maps this into the
/// wire message. Amount is a string throughout (see TransactionGateway.md §5)
/// to avoid floating-point precision issues.
/// </summary>
public sealed record TransactionSubmission(
    string TransactionNo,
    string TransactionDatetime,
    string Amount,
    string Type,
    string Status,
    string Currency);
