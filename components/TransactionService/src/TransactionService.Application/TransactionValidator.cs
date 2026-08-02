using System.Globalization;

namespace TransactionService.Application;

/// <summary>
/// Re-validates independently of TransactionGateway (TransactionService.md
/// §2) - a ClusterIP Service restricts routing, not authorization, so this
/// doesn't trust the caller. Pure logic, no I/O.
/// </summary>
public sealed class TransactionValidator : ITransactionValidator
{
    // Small in-process allow-lists for the demo (TransactionService.md §5) -
    // kept in code, not a Postgres ENUM type, so adding a value never needs a
    // DDL change across the 3 shard instances (database.md §6).
    private static readonly HashSet<string> AllowedCurrencies = new(StringComparer.OrdinalIgnoreCase)
    {
        "USD", "CAD", "EUR", "GBP", "JPY", "AUD", "CHF", "CNY", "INR", "MXN",
    };

    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "PAYMENT", "REFUND", "TRANSFER", "ADJUSTMENT",
    };

    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "PENDING", "COMPLETED", "FAILED", "REVERSED",
    };

    public string? Validate(TransactionSubmission submission)
    {
        if (string.IsNullOrWhiteSpace(submission.TransactionNo))
        {
            return "transaction_no is required";
        }

        if (string.IsNullOrWhiteSpace(submission.TransactionDatetime))
        {
            return "transaction_datetime is required";
        }

        if (!AllowedCurrencies.Contains(submission.Currency))
        {
            return $"currency '{submission.Currency}' is not a recognized ISO 4217 code";
        }

        if (!AllowedTypes.Contains(submission.Type))
        {
            return $"type '{submission.Type}' is not an allowed transaction type";
        }

        if (!AllowedStatuses.Contains(submission.Status))
        {
            return $"status '{submission.Status}' is not an allowed transaction status";
        }

        // Received as a string over gRPC specifically to avoid floating-point
        // precision issues (TransactionService.md §5) - parsed here only to
        // validate shape, never converted back to a number for storage/transit.
        const NumberStyles style = NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign;
        if (!decimal.TryParse(submission.Amount, style, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
        {
            return "amount must be a positive decimal";
        }

        return null;
    }
}
