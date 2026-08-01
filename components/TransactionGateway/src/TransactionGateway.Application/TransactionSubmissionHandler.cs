using Microsoft.Extensions.Logging;

namespace TransactionGateway.Application;

/// <summary>
/// Orchestration for POST /api/v1/transactions (TransactionGateway.md §2/§4):
/// dedup check -> forward -> mark-processed, with the cache treated as
/// fail-open at every step.
/// </summary>
public sealed class TransactionSubmissionHandler : ITransactionSubmissionHandler
{
    private readonly IIdempotencyGuard _idempotencyGuard;
    private readonly ITransactionForwarder _forwarder;
    private readonly ILogger<TransactionSubmissionHandler> _logger;

    public TransactionSubmissionHandler(
        IIdempotencyGuard idempotencyGuard,
        ITransactionForwarder forwarder,
        ILogger<TransactionSubmissionHandler> logger)
    {
        _idempotencyGuard = idempotencyGuard;
        _forwarder = forwarder;
        _logger = logger;
    }

    public async Task<bool> HandleAsync(TransactionSubmission submission, CancellationToken cancellationToken)
    {
        using var activity = TransactionGatewayTelemetry.ActivitySource.StartActivity("SubmitTransaction");
        activity?.SetTag("transaction_no", submission.TransactionNo);
        TransactionGatewayTelemetry.RequestsReceived.Add(1);

        bool alreadyProcessed;
        try
        {
            alreadyProcessed = await _idempotencyGuard.HasBeenProcessedAsync(submission.TransactionNo, cancellationToken);
        }
        catch (Exception ex)
        {
            // Fail open: the idempotency cache is a latency/cost optimization,
            // not the source of truth (TransactionGateway.md §2) - a Redis
            // outage should degrade to "maybe a duplicate reaches SQS", not
            // "requests fail".
            TransactionGatewayTelemetry.CacheFailOpens.Add(1);
            activity?.SetTag("cache_fail_open", true);
            _logger.LogWarning(ex, "Idempotency cache unavailable for {TransactionNo}, failing open", submission.TransactionNo);
            alreadyProcessed = false;
        }

        if (alreadyProcessed)
        {
            activity?.SetTag("dedup_hit", true);
            TransactionGatewayTelemetry.DedupHits.Add(1);
            _logger.LogInformation("Duplicate submission for {TransactionNo}, returning cached acceptance", submission.TransactionNo);
            return true;
        }

        bool accepted;
        try
        {
            accepted = await _forwarder.ForwardAsync(submission, cancellationToken);
        }
        catch (Exception ex)
        {
            activity?.SetTag("grpc_failure", true);
            TransactionGatewayTelemetry.GrpcFailures.Add(1);
            _logger.LogError(ex, "Failed to forward {TransactionNo} to TransactionService", submission.TransactionNo);
            return false;
        }

        if (accepted)
        {
            try
            {
                await _idempotencyGuard.MarkProcessedAsync(submission.TransactionNo, cancellationToken);
            }
            catch (Exception ex)
            {
                // Same fail-open reasoning as above - a failed cache write just
                // means a future retry isn't deduped locally; TransactionService's
                // own dedup cache and the downstream unique-key/claim checks still
                // guard correctness.
                _logger.LogWarning(ex, "Failed to record idempotency marker for {TransactionNo} after a successful forward", submission.TransactionNo);
            }
        }

        return accepted;
    }
}
