using Amazon.SQS;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ShardRouting;
using TransactionService.Application;
using TransactionService.Infrastructure.Options;

namespace TransactionService.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Wires up TransactionService.md §4: HybridCache (local memory + this
    /// service's own Redis), the AWS SQS client, and the Application-layer
    /// orchestration (including the validator, since it's pure logic with no
    /// infrastructure dependency of its own).
    /// </summary>
    public static IServiceCollection AddTransactionServiceInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<IdempotencyOptions>(configuration.GetSection("Idempotency"));
        services.Configure<RedisOptions>(configuration.GetSection("Redis"));
        services.Configure<SqsOptions>(configuration.GetSection("Sqs"));
        services.Configure<ShardOptions>(configuration.GetSection("Shards"));
        services.Configure<ReportingOptions>(configuration.GetSection("Reporting"));

        var redisOptions = configuration.GetSection("Redis").Get<RedisOptions>() ?? new RedisOptions();
        services.AddStackExchangeRedisCache(o => o.Configuration = redisOptions.ConnectionString);

        var idempotencyOptions = configuration.GetSection("Idempotency").Get<IdempotencyOptions>() ?? new IdempotencyOptions();
        services.AddHybridCache(o =>
        {
            o.DefaultEntryOptions = new HybridCacheEntryOptions
            {
                Expiration = idempotencyOptions.EntryTtl,
                LocalCacheExpiration = idempotencyOptions.EntryTtl,
            };
        });

        // Default AWS credential chain (IRSA on EKS, local profile/SSO for dev)
        // picks up region/credentials - no keys configured here.
        services.AddDefaultAWSOptions(configuration.GetAWSOptions());
        services.AddAWSService<IAmazonSQS>();

        services.AddSingleton<ITransactionValidator, TransactionValidator>();
        services.AddSingleton<IIdempotencyGuard, HybridCacheIdempotencyGuard>();
        services.AddScoped<ISqsMessageForwarder, SqsMessageForwarder>();
        services.AddScoped<ITransactionSubmissionHandler, TransactionSubmissionHandler>();

        // Read-only search path (TransactionService.md §5) - intended to be a
        // separate connection set from TransactionWorker's write-capable ones,
        // using a distinct read-only Postgres role (§2/§6). That role has never
        // actually been created via CREATE ROLE (no migration tooling exists
        // yet for it), so Shards/Reporting are configured with the same master
        // (dbadmin) credentials as TransactionWorker for now - least-privilege
        // separation is still a follow-up, not implemented here.
        var shardOptions = configuration.GetSection("Shards").Get<ShardOptions>() ?? new ShardOptions();
        for (var shardId = 0; shardId < shardOptions.ConnectionStrings.Length; shardId++)
        {
            var connectionString = shardOptions.ConnectionStrings[shardId];
            services.AddKeyedSingleton(shardId, (_, _) => NpgsqlDataSource.Create(connectionString));
        }

        var reportingOptions = configuration.GetSection("Reporting").Get<ReportingOptions>() ?? new ReportingOptions();
        services.AddSingleton(_ => NpgsqlDataSource.Create(reportingOptions.ConnectionString));

        services.AddSingleton<IShardRouter, Murmur3ShardRouter>();
        services.AddSingleton<ITransactionSearchRepository, PostgresTransactionSearchRepository>();
        services.AddScoped<ITransactionSearchHandler, TransactionSearchHandler>();

        return services;
    }
}
