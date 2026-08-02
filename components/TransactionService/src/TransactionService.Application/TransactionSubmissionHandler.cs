using Microsoft.Extensions.Logging;

namespace TransactionService.Application;

/// <summary>
/// Orchestration for the gRPC SubmitTransaction call (TransactionService.md
/// §3): validate -> dedup check -> send -> mark-processed. The idempotency
/// cache entry is written only after SendMessage succeeds (§2) - a failed
/// send never gets silently swallowed by a marker for a message that never
/// reached SQS.
/// </summary>
public sealed class TransactionSubmissionHandler : ITransactionSubmissionHandler
{
    private readonly ITransactionValidator _validator;
    private readonly IIdempotencyGuard _idempotencyGuard;
    private readonly ISqsMessageForwarder _forwarder;
    private readonly ILogger<TransactionSubmissionHandler> _logger;

    public TransactionSubmissionHandler(
        ITransactionValidator validator,
        IIdempotencyGuard idempotencyGuard,
        ISqsMessageForwarder forwarder,
        ILogger<TransactionSubmissionHandler> logger)
    {
        _validator = validator;
        _idempotencyGuard = idempotencyGuard;
        _forwarder = forwarder;
        _logger = logger;
    }

    public async Task<SubmissionResult> HandleAsync(TransactionSubmission submission, CancellationToken cancellationToken)
    {
        using var activity = TransactionServiceTelemetry.ActivitySource.StartActivity("SubmitTransaction");
        activity?.SetTag("transaction_no", submission.TransactionNo);
        TransactionServiceTelemetry.RequestsReceived.Add(1);

        var validationError = _validator.Validate(submission);
        if (validationError is not null)
        {
            activity?.SetTag("validation_failed", true);
            TransactionServiceTelemetry.ValidationFailures.Add(1);
            _logger.LogWarning("Validation failed for {TransactionNo}: {Error}", submission.TransactionNo, validationError);
            return SubmissionResult.ValidationFailed(validationError);
        }

        bool alreadyProcessed;
        try
        {
            alreadyProcessed = await _idempotencyGuard.HasBeenProcessedAsync(submission.TransactionNo, cancellationToken);
        }
        catch (Exception ex)
        {
            // Fail open - same reasoning as TransactionGateway: this cache is
            // a latency/cost optimization, not the source of truth. The DB's
            // UNIQUE constraint is the real backstop (TransactionService.md §3).
            TransactionServiceTelemetry.CacheFailOpens.Add(1);
            activity?.SetTag("cache_fail_open", true);
            _logger.LogWarning(ex, "Idempotency cache unavailable for {TransactionNo}, failing open", submission.TransactionNo);
            alreadyProcessed = false;
        }

        if (alreadyProcessed)
        {
            activity?.SetTag("dedup_hit", true);
            TransactionServiceTelemetry.DedupHits.Add(1);
            _logger.LogInformation("Duplicate submission for {TransactionNo}, returning cached acceptance", submission.TransactionNo);
            return SubmissionResult.Accepted();
        }

        try
        {
            await _forwarder.SendAsync(submission, cancellationToken);
        }
        catch (Exception ex)
        {
            activity?.SetTag("send_failed", true);
            TransactionServiceTelemetry.SendFailures.Add(1);
            _logger.LogError(ex, "Failed to send {TransactionNo} to SQS", submission.TransactionNo);
            return SubmissionResult.SendFailed(ex.Message);
        }

        try
        {
            await _idempotencyGuard.MarkProcessedAsync(submission.TransactionNo, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record idempotency marker for {TransactionNo} after a successful send", submission.TransactionNo);
        }

        return SubmissionResult.Accepted();
    }
}
