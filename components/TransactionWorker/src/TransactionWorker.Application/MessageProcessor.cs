using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ShardRouting;
using TransactionWorker.Domain;

namespace TransactionWorker.Application;

/// <inheritdoc cref="IMessageProcessor" />
public sealed class MessageProcessor : IMessageProcessor
{
    private readonly IClaimStore _claimStore;
    private readonly ITransactionRepository _repository;
    private readonly IShardRouter _shardRouter;
    private readonly ISnowflakeIdGenerator _idGenerator;
    private readonly IWorkerIdentity _workerIdentity;
    private readonly ILogger<MessageProcessor> _logger;

    public MessageProcessor(
        IClaimStore claimStore,
        ITransactionRepository repository,
        IShardRouter shardRouter,
        ISnowflakeIdGenerator idGenerator,
        IWorkerIdentity workerIdentity,
        ILogger<MessageProcessor> logger)
    {
        _claimStore = claimStore;
        _repository = repository;
        _shardRouter = shardRouter;
        _idGenerator = idGenerator;
        _workerIdentity = workerIdentity;
        _logger = logger;
    }

    public async Task<ClaimOutcome> ClaimNewMessageAsync(string transactionNo, string payloadJson, CancellationToken cancellationToken)
    {
        using var activity = TransactionWorkerTelemetry.ActivitySource.StartActivity("ClaimNewMessage");
        activity?.SetTag("transaction_no", transactionNo);

        var outcome = await _claimStore.TryClaimAsync(transactionNo, _workerIdentity.WorkerId, payloadJson, cancellationToken);

        if (outcome == ClaimOutcome.AlreadyClaimed)
        {
            activity?.SetTag("claim_conflict", true);
            TransactionWorkerTelemetry.ClaimConflicts.Add(1);
            _logger.LogInformation("Duplicate delivery for {TransactionNo}, already claimed - discarding", transactionNo);
        }

        return outcome;
    }

    public async Task CompleteProcessingAsync(string transactionNo, string payloadJson, CancellationToken cancellationToken)
    {
        using var activity = TransactionWorkerTelemetry.ActivitySource.StartActivity("CompleteProcessing");
        activity?.SetTag("transaction_no", transactionNo);

        await InsertAndCompleteAsync(transactionNo, payloadJson, activity, cancellationToken);
    }

    public async Task<bool> TryResumeStaleClaimAsync(StaleClaim claim, CancellationToken cancellationToken)
    {
        using var activity = TransactionWorkerTelemetry.ActivitySource.StartActivity("ResumeStaleClaim");
        activity?.SetTag("transaction_no", claim.TransactionNo);

        var won = await _claimStore.TryReclaimAsync(claim, _workerIdentity.WorkerId, cancellationToken);
        if (!won)
        {
            activity?.SetTag("reclaim_lost_race", true);
            return false;
        }

        TransactionWorkerTelemetry.Reclaims.Add(1);
        _logger.LogInformation("Reclaimed stale claim for {TransactionNo} (attempt {AttemptCount})", claim.TransactionNo, claim.AttemptCount + 1);

        await InsertAndCompleteAsync(claim.TransactionNo, claim.Payload, activity, cancellationToken);
        return true;
    }

    private async Task InsertAndCompleteAsync(string transactionNo, string payloadJson, Activity? activity, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<TransactionPayload>(payloadJson)
                      ?? throw new InvalidOperationException($"Could not deserialize payload for {transactionNo}.");

        var shardId = _shardRouter.ResolveShardId(transactionNo);
        var id = _idGenerator.NextId(shardId);

        var record = new TransactionRecord(id, transactionNo, payload.TransactionDatetime, payload.Amount, payload.Type, payload.Status, payload.Currency);

        var outcome = await _repository.InsertAsync(record, shardId, cancellationToken);

        activity?.SetTag("shard_id", shardId);
        activity?.SetTag("insert_outcome", outcome.ToString());
        TransactionWorkerTelemetry.ShardInserts.Add(1, new KeyValuePair<string, object?>("shard_id", shardId));

        if (outcome == InsertOutcome.AlreadyExists)
        {
            _logger.LogInformation(
                "{TransactionNo} already exists in shard {ShardId} (unique-violation treated as already-processed)",
                transactionNo,
                shardId);
        }

        TransactionWorkerTelemetry.MessagesProcessed.Add(1);

        await _claimStore.CompleteAsync(transactionNo, cancellationToken);
    }
}
