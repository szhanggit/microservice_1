using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Options;
using TransactionService.Application;
using TransactionService.Infrastructure.Options;

namespace TransactionService.Infrastructure;

/// <summary>
/// Sends the raw fields straight through to SQS as JSON (TransactionService.md
/// §5) - no id/shard_id here, TransactionWorker computes those at insert time.
/// </summary>
public sealed class SqsMessageForwarder : ISqsMessageForwarder
{
    private readonly IAmazonSQS _sqs;
    private readonly string _queueUrl;

    public SqsMessageForwarder(IAmazonSQS sqs, IOptions<SqsOptions> options)
    {
        _sqs = sqs;
        _queueUrl = options.Value.QueueUrl;
    }

    public async Task SendAsync(TransactionSubmission submission, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new SqsMessageBody(
            submission.TransactionNo,
            submission.TransactionDatetime,
            submission.Amount,
            submission.Type,
            submission.Status,
            submission.Currency));

        await _sqs.SendMessageAsync(
            new SendMessageRequest
            {
                QueueUrl = _queueUrl,
                MessageBody = body,
            },
            cancellationToken);
    }

    private sealed record SqsMessageBody(
        [property: JsonPropertyName("transaction_no")] string TransactionNo,
        [property: JsonPropertyName("transaction_datetime")] string TransactionDatetime,
        [property: JsonPropertyName("amount")] string Amount,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("currency")] string Currency);
}
