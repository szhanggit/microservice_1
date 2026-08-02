using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Npgsql;
using Polly;
using Polly.Retry;

namespace TransactionWorker.Infrastructure;

/// <summary>
/// Shared Polly retry policies (KEDA.md §4) for transient AWS/Postgres errors.
/// Expected control-flow outcomes - DynamoDB conditional-check failures,
/// Postgres unique-violations - are excluded from retry so callers can still
/// catch and handle them as the normal "already claimed" / "already exists"
/// branches they are, rather than having Polly retry and mask them.
/// </summary>
internal static class ResiliencePipelines
{
    public static readonly ResiliencePipeline DynamoDb = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder()
                .Handle<AmazonServiceException>(e => e is not ConditionalCheckFailedException),
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromMilliseconds(200),
        })
        .Build();

    public static readonly ResiliencePipeline Postgres = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder()
                .Handle<NpgsqlException>(e => e is not PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }),
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromMilliseconds(200),
        })
        .Build();
}
