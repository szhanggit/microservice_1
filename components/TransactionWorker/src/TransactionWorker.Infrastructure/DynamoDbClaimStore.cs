using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Options;
using TransactionWorker.Application;
using TransactionWorker.Infrastructure.Options;

namespace TransactionWorker.Infrastructure;

/// <inheritdoc cref="IClaimStore" />
public sealed class DynamoDbClaimStore : IClaimStore
{
    private const string StatusClaimed = "CLAIMED";
    private const string StatusCompleted = "COMPLETED";
    private const string StatusAttribute = "status";
    private const string TtlAttribute = "ttl";

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly DynamoDbOptions _options;

    public DynamoDbClaimStore(IAmazonDynamoDB dynamoDb, IOptions<DynamoDbOptions> options)
    {
        _dynamoDb = dynamoDb;
        _options = options.Value;
    }

    public async Task<ClaimOutcome> TryClaimAsync(string transactionNo, string workerId, string payloadJson, CancellationToken cancellationToken)
    {
        var leaseExpiry = DateTimeOffset.UtcNow.AddSeconds(_options.LeaseDurationSeconds).ToUnixTimeSeconds();

        var request = new PutItemRequest
        {
            TableName = _options.TableName,
            Item = new Dictionary<string, AttributeValue>
            {
                ["transaction_no"] = new AttributeValue { S = transactionNo },
                [StatusAttribute] = new AttributeValue { S = StatusClaimed },
                ["worker_id"] = new AttributeValue { S = workerId },
                ["lease_expiry"] = new AttributeValue { N = leaseExpiry.ToString(CultureInfo.InvariantCulture) },
                ["payload"] = new AttributeValue { S = payloadJson },
                ["attempt_count"] = new AttributeValue { N = "0" },
            },
            // KEDA.md §5 step 2: atomically claims the message and, as a side
            // benefit, rejects SQS's own duplicate deliveries independent of
            // any crash scenario.
            ConditionExpression = "attribute_not_exists(transaction_no)",
        };

        try
        {
            await ResiliencePipelines.DynamoDb.ExecuteAsync(async ct => await _dynamoDb.PutItemAsync(request, ct), cancellationToken);
            return ClaimOutcome.Claimed;
        }
        catch (ConditionalCheckFailedException)
        {
            return ClaimOutcome.AlreadyClaimed;
        }
    }

    public async Task CompleteAsync(string transactionNo, CancellationToken cancellationToken)
    {
        var ttl = DateTimeOffset.UtcNow.AddSeconds(_options.CompletedTtlSeconds).ToUnixTimeSeconds();

        var request = new UpdateItemRequest
        {
            TableName = _options.TableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["transaction_no"] = new AttributeValue { S = transactionNo },
            },
            UpdateExpression = "SET #status = :completed, #ttl = :ttl",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                ["#status"] = StatusAttribute,
                ["#ttl"] = TtlAttribute,
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":completed"] = new AttributeValue { S = StatusCompleted },
                [":ttl"] = new AttributeValue { N = ttl.ToString(CultureInfo.InvariantCulture) },
            },
        };

        await ResiliencePipelines.DynamoDb.ExecuteAsync(async ct => await _dynamoDb.UpdateItemAsync(request, ct), cancellationToken);
    }

    public async Task<IReadOnlyList<StaleClaim>> FindStaleClaimsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var request = new QueryRequest
        {
            TableName = _options.TableName,
            IndexName = "gsi_status_lease",
            KeyConditionExpression = "#status = :claimed AND lease_expiry < :now",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                ["#status"] = StatusAttribute,
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":claimed"] = new AttributeValue { S = StatusClaimed },
                [":now"] = new AttributeValue { N = now.ToString(CultureInfo.InvariantCulture) },
            },
        };

        var response = await ResiliencePipelines.DynamoDb.ExecuteAsync(async ct => await _dynamoDb.QueryAsync(request, ct), cancellationToken);

        return response.Items
            .Select(item => new StaleClaim(
                item["transaction_no"].S,
                item["payload"].S,
                long.Parse(item["lease_expiry"].N, CultureInfo.InvariantCulture),
                int.Parse(item["attempt_count"].N, CultureInfo.InvariantCulture)))
            .ToList();
    }

    public async Task<bool> TryReclaimAsync(StaleClaim claim, string workerId, CancellationToken cancellationToken)
    {
        var newLeaseExpiry = DateTimeOffset.UtcNow.AddSeconds(_options.LeaseDurationSeconds).ToUnixTimeSeconds();

        var request = new UpdateItemRequest
        {
            TableName = _options.TableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["transaction_no"] = new AttributeValue { S = claim.TransactionNo },
            },
            UpdateExpression = "SET lease_expiry = :new_lease, worker_id = :worker, attempt_count = :attempt",
            // Only one instance wins if several race to reclaim the same
            // stale item (KEDA.md §5's crash-recovery section).
            ConditionExpression = "lease_expiry = :old_lease",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":new_lease"] = new AttributeValue { N = newLeaseExpiry.ToString(CultureInfo.InvariantCulture) },
                [":old_lease"] = new AttributeValue { N = claim.OldLeaseExpiry.ToString(CultureInfo.InvariantCulture) },
                [":worker"] = new AttributeValue { S = workerId },
                [":attempt"] = new AttributeValue { N = (claim.AttemptCount + 1).ToString(CultureInfo.InvariantCulture) },
            },
        };

        try
        {
            await ResiliencePipelines.DynamoDb.ExecuteAsync(async ct => await _dynamoDb.UpdateItemAsync(request, ct), cancellationToken);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }
}
