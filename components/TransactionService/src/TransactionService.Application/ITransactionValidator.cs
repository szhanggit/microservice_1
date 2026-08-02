namespace TransactionService.Application;

public interface ITransactionValidator
{
    /// <summary>Returns null if valid, otherwise a human-readable rejection reason.</summary>
    string? Validate(TransactionSubmission submission);
}
