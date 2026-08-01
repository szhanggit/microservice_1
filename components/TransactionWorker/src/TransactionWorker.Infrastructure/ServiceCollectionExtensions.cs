using Amazon.DynamoDBv2;
using Amazon.SQS;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ShardRouting;
using TransactionWorker.Application;
using TransactionWorker.Domain;
using TransactionWorker.Infrastructure.Options;

namespace TransactionWorker.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Wires up KEDA.md §4: AWS SQS/DynamoDB clients, the 3 keyed per-shard
    /// Postgres connection factories, the claim-check orchestration, and the
    /// two background services.
    /// </summary>
    public static IServiceCollection AddTransactionWorkerInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SqsOptions>(configuration.GetSection("Sqs"));
        services.Configure<DynamoDbOptions>(configuration.GetSection("DynamoDb"));
        services.Configure<StaleClaimReclaimOptions>(configuration.GetSection("StaleClaimReclaim"));

        // Default AWS credential chain (IRSA on EKS, local profile/SSO for dev)
        // picks up region/credentials - no keys configured here.
        services.AddDefaultAWSOptions(configuration.GetAWSOptions());
        services.AddAWSService<IAmazonSQS>();
        services.AddAWSService<IAmazonDynamoDB>();

        services.AddSingleton<IWorkerIdentity, HostnameWorkerIdentity>();

        // database.md §5: node_id is derived (hashed) from this instance's
        // worker identity, so concurrently running instances mint distinct
        // ids without any manual per-instance configuration.
        services.AddOptions<SnowflakeIdOptions>()
            .Configure<IWorkerIdentity>((options, identity) =>
                options.NodeId = (int)(MurmurHash3.Hash32(identity.WorkerId) % 32));

        // 3 logical shards (database.md §5's murmur3 % 3 routing) - shard-0 and
        // shard-2's own RDS instances were dropped to fit the AWS account's
        // free-tier ceiling of 2 concurrent RDS instances, so all 3 keys
        // currently point at the same physical shard-1 instance. Routing logic
        // itself is unchanged; re-split these once more instances are available.
        var shardOptions = configuration.GetSection("Shards").Get<ShardOptions>() ?? new ShardOptions();
        for (var shardId = 0; shardId < shardOptions.ConnectionStrings.Length; shardId++)
        {
            var connectionString = shardOptions.ConnectionStrings[shardId];
            services.AddKeyedSingleton(shardId, (_, _) => NpgsqlDataSource.Create(connectionString));
        }
        services.AddSingleton<ITransactionRepository, PostgresTransactionRepository>();

        services.AddSingleton<IShardRouter, Murmur3ShardRouter>();
        services.AddSingleton<ISnowflakeIdGenerator, SnowflakeIdGenerator>();
        services.AddSingleton<IClaimStore, DynamoDbClaimStore>();
        services.AddScoped<IMessageProcessor, MessageProcessor>();

        services.AddHostedService<SqsListenerService>();
        services.AddHostedService<StaleClaimReclaimService>();

        return services;
    }
}
