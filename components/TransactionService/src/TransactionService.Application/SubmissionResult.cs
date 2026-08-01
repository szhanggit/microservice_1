namespace TransactionService.Application;

public enum SubmissionOutcome
{
    /// <summary>Fresh send succeeded, or this was a deduped retry - both map to accepted=true.</summary>
    Accepted,

    /// <summary>Field validation failed before anything else ran - maps to a gRPC INVALID_ARGUMENT.</summary>
    ValidationFailed,

    /// <summary>SendMessage to SQS failed - maps to a gRPC error distinct from a validation failure.</summary>
    SendFailed,
}

public sealed record SubmissionResult(SubmissionOutcome Outcome, string? ErrorMessage = null)
{
    public static SubmissionResult Accepted() => new(SubmissionOutcome.Accepted);

    public static SubmissionResult ValidationFailed(string message) => new(SubmissionOutcome.ValidationFailed, message);

    public static SubmissionResult SendFailed(string message) => new(SubmissionOutcome.SendFailed, message);
}
