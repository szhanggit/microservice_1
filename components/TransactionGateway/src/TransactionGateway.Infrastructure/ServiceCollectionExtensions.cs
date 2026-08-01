using Contracts.Transactions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TransactionGateway.Application;
using TransactionGateway.Infrastructure.Options;

namespace TransactionGateway.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Wires up everything from TransactionGateway.md §4: HybridCache (local
    /// memory + this service's own Redis), the gRPC client to
    /// TransactionService, and the Application-layer orchestration.
    /// </summary>
    public static IServiceCollection AddTransactionGatewayInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<IdempotencyOptions>(configuration.GetSection("Idempotency"));
        services.Configure<RedisOptions>(configuration.GetSection("Redis"));
        services.Configure<TransactionServiceOptions>(configuration.GetSection("TransactionService"));

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

        var transactionServiceOptions = configuration.GetSection("TransactionService").Get<TransactionServiceOptions>() ?? new TransactionServiceOptions();
        services.AddGrpcClient<TransactionService.TransactionServiceClient>(o =>
        {
            o.Address = new Uri(transactionServiceOptions.GrpcAddress);
        });

        services.AddSingleton<IIdempotencyGuard, HybridCacheIdempotencyGuard>();
        services.AddScoped<ITransactionForwarder, GrpcTransactionForwarder>();
        services.AddScoped<ITransactionSubmissionHandler, TransactionSubmissionHandler>();

        services.AddScoped<ITransactionSearchForwarder, GrpcTransactionSearchForwarder>();
        services.AddScoped<ITransactionSearchHandler, TransactionSearchHandler>();

        return services;
    }
}
