using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Options;
using Moq;
using TransactionWorker.Application;
using TransactionWorker.Infrastructure;
using TransactionWorker.Infrastructure.Options;

namespace TransactionWorker.UnitTests.Infrastructure;

public class DynamoDbClaimStoreTests
{
    private readonly Mock<IAmazonDynamoDB> _dynamoDb = new();

    private DynamoDbClaimStore CreateStore() => new(_dynamoDb.Object, Options.Create(new DynamoDbOptions
    {
        TableName = "transaction-claims",
        LeaseDurationSeconds = 30,
        CompletedTtlSeconds = 300,
    }));

    [Fact]
    public async Task TryClaimAsync_returns_Claimed_when_the_conditional_PutItem_succeeds()
    {
        PutItemRequest? captured = null;
        _dynamoDb.Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutItemRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new PutItemResponse());

        var outcome = await CreateStore().TryClaimAsync("TXN-1", "worker-1", "{}", CancellationToken.None);

        Assert.Equal(ClaimOutcome.Claimed, outcome);
        Assert.NotNull(captured);
        Assert.Equal("transaction-claims", captured!.TableName);
        Assert.Equal("attribute_not_exists(transaction_no)", captured.ConditionExpression);
        Assert.Equal("TXN-1", captured.Item["transaction_no"].S);
        Assert.Equal("CLAIMED", captured.Item["status"].S);
        Assert.Equal("worker-1", captured.Item["worker_id"].S);
    }

    [Fact]
    public async Task TryClaimAsync_returns_AlreadyClaimed_when_the_condition_check_fails()
    {
        _dynamoDb.Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConditionalCheckFailedException("already exists"));

        var outcome = await CreateStore().TryClaimAsync("TXN-1", "worker-1", "{}", CancellationToken.None);

        Assert.Equal(ClaimOutcome.AlreadyClaimed, outcome);
    }

    [Fact]
    public async Task CompleteAsync_sets_status_completed_with_a_ttl()
    {
        UpdateItemRequest? captured = null;
        _dynamoDb.Setup(d => d.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
            .Callback<UpdateItemRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new UpdateItemResponse());

        await CreateStore().CompleteAsync("TXN-1", CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("TXN-1", captured!.Key["transaction_no"].S);
        Assert.Equal("COMPLETED", captured.ExpressionAttributeValues[":completed"].S);
        Assert.True(long.Parse(captured.ExpressionAttributeValues[":ttl"].N) > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task FindStaleClaimsAsync_maps_query_results_into_StaleClaim_records()
    {
        _dynamoDb.Setup(d => d.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResponse
            {
                Items =
                [
                    new Dictionary<string, AttributeValue>
                    {
                        ["transaction_no"] = new AttributeValue { S = "TXN-1" },
                        ["payload"] = new AttributeValue { S = "{\"transaction_no\":\"TXN-1\"}" },
                        ["lease_expiry"] = new AttributeValue { N = "1000" },
                        ["attempt_count"] = new AttributeValue { N = "1" },
                    },
                ],
            });

        var staleClaims = await CreateStore().FindStaleClaimsAsync(CancellationToken.None);

        var claim = Assert.Single(staleClaims);
        Assert.Equal("TXN-1", claim.TransactionNo);
        Assert.Equal(1000, claim.OldLeaseExpiry);
        Assert.Equal(1, claim.AttemptCount);
    }

    [Fact]
    public async Task TryReclaimAsync_returns_true_when_the_condition_on_the_old_lease_holds()
    {
        var claim = new StaleClaim("TXN-1", "{}", OldLeaseExpiry: 1000, AttemptCount: 1);
        UpdateItemRequest? captured = null;
        _dynamoDb.Setup(d => d.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
            .Callback<UpdateItemRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new UpdateItemResponse());

        var won = await CreateStore().TryReclaimAsync(claim, "worker-2", CancellationToken.None);

        Assert.True(won);
        Assert.Equal("1000", captured!.ExpressionAttributeValues[":old_lease"].N);
        Assert.Equal("2", captured.ExpressionAttributeValues[":attempt"].N);
        Assert.Equal("worker-2", captured.ExpressionAttributeValues[":worker"].S);
    }

    [Fact]
    public async Task TryReclaimAsync_returns_false_when_another_instance_already_renewed_the_lease()
    {
        var claim = new StaleClaim("TXN-1", "{}", OldLeaseExpiry: 1000, AttemptCount: 1);
        _dynamoDb.Setup(d => d.UpdateItemAsync(It.IsAny<UpdateItemRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConditionalCheckFailedException("lease already renewed"));

        var won = await CreateStore().TryReclaimAsync(claim, "worker-2", CancellationToken.None);

        Assert.False(won);
    }
}
