using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TransactionWorker.Application;
using TransactionWorker.Domain;
using TransactionWorker.Infrastructure.Options;

namespace TransactionWorker.Infrastructure;

/// <summary>
/// Long-poll receive loop (KEDA.md §3/§4). Resolves per-message dependencies
/// through a fresh DI scope so scoped services (e.g. IMessageProcessor) don't
/// leak state across messages; messages within a batch are processed
/// concurrently.
/// </summary>
public sealed class SqsListenerService : BackgroundService
{
    private readonly IAmazonSQS _sqs;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SqsOptions _options;
    private readonly ILogger<SqsListenerService> _logger;

    public SqsListenerService(
        IAmazonSQS sqs,
        IServiceScopeFactory scopeFactory,
        IOptions<SqsOptions> options,
        ILogger<SqsListenerService> logger)
    {
        _sqs = sqs;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            ReceiveMessageResponse response;
            try
            {
                response = await _sqs.ReceiveMessageAsync(
                    new ReceiveMessageRequest
                    {
                        QueueUrl = _options.QueueUrl,
                        MaxNumberOfMessages = _options.MaxNumberOfMessages,
                        WaitTimeSeconds = _options.WaitTimeSeconds,
                    },
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to receive messages from SQS, backing off");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            // AWS SDK response.Messages can come back null (not just empty) on
            // a long-poll that finds nothing, rather than an empty list.
            if (response.Messages is null || response.Messages.Count == 0)
            {
                continue;
            }

            await Task.WhenAll(response.Messages.Select(message => ProcessMessageSafeAsync(message, stoppingToken)));
        }
    }

    private async Task ProcessMessageSafeAsync(Message message, CancellationToken cancellationToken)
    {
        try
        {
            await ProcessMessageAsync(message, cancellationToken);
        }
        catch (Exception ex)
        {
            // If this was mid-flight past the claim step, the DynamoDB item is
            // left CLAIMED with its lease running - StaleClaimReclaimService
            // picks it up later. This is the crash-recovery path working as
            // designed, not a bug (KEDA.md §5).
            _logger.LogError(ex, "Unhandled error processing message {MessageId}", message.MessageId);
        }
    }

    internal async Task ProcessMessageAsync(Message message, CancellationToken cancellationToken)
    {
        using var activity = TransactionWorkerTelemetry.ActivitySource.StartActivity("ProcessMessage");
        activity?.SetTag("messaging.message_id", message.MessageId);

        TransactionPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TransactionPayload>(message.Body);
        }
        catch (JsonException ex)
        {
            // A message that can never be claimed keeps being redelivered by
            // SQS itself and eventually lands in the DLQ after maxReceiveCount -
            // a different failure mode from the lease-based recovery below
            // (KEDA.md §5's "what the DLQ is still for").
            _logger.LogWarning(ex, "Malformed message body, leaving for SQS redelivery/DLQ: {MessageId}", message.MessageId);
            return;
        }

        if (payload is null || string.IsNullOrEmpty(payload.TransactionNo))
        {
            _logger.LogWarning("Empty/invalid payload, leaving for SQS redelivery/DLQ: {MessageId}", message.MessageId);
            return;
        }

        activity?.SetTag("transaction_no", payload.TransactionNo);

        using var scope = _scopeFactory.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IMessageProcessor>();

        var claimOutcome = await processor.ClaimNewMessageAsync(payload.TransactionNo, message.Body, cancellationToken);

        // Safe to delete immediately regardless of outcome (KEDA.md §5 step 3) -
        // Claimed means this instance owns it now; AlreadyClaimed means it's a
        // harmless duplicate delivery of work another instance already owns.
        await _sqs.DeleteMessageAsync(_options.QueueUrl, message.ReceiptHandle, cancellationToken);

        if (claimOutcome == ClaimOutcome.AlreadyClaimed)
        {
            return;
        }

        await processor.CompleteProcessingAsync(payload.TransactionNo, message.Body, cancellationToken);
    }
}
