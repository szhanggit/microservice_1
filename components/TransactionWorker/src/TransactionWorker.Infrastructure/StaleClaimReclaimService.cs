using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TransactionWorker.Application;
using TransactionWorker.Infrastructure.Options;

namespace TransactionWorker.Infrastructure;

/// <summary>
/// Background scan of the gsi_status_lease GSI for stale CLAIMED items
/// (KEDA.md §5). Runs once immediately on startup - in addition to catching
/// up on anything stranded before this pod existed, this is also what makes
/// `minReplicaCount: 0` safe (KEDA.md §6): a freshly scaled-up pod's own
/// startup scan picks up any claim stranded while replicas were at zero.
/// </summary>
public sealed class StaleClaimReclaimService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly StaleClaimReclaimOptions _options;
    private readonly ILogger<StaleClaimReclaimService> _logger;

    public StaleClaimReclaimService(
        IServiceScopeFactory scopeFactory,
        IOptions<StaleClaimReclaimOptions> options,
        ILogger<StaleClaimReclaimService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.ScanInterval);

        do
        {
            await ScanOnceAsync(stoppingToken);
        }
        while (await WaitForNextTickAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    internal async Task ScanOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var claimStore = scope.ServiceProvider.GetRequiredService<IClaimStore>();
        var processor = scope.ServiceProvider.GetRequiredService<IMessageProcessor>();

        IReadOnlyList<StaleClaim> staleClaims;
        try
        {
            staleClaims = await claimStore.FindStaleClaimsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to scan for stale claims");
            return;
        }

        foreach (var claim in staleClaims)
        {
            try
            {
                var won = await processor.TryResumeStaleClaimAsync(claim, cancellationToken);
                if (!won)
                {
                    _logger.LogDebug("Lost reclaim race for {TransactionNo} - another instance already renewed the lease", claim.TransactionNo);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resume stale claim for {TransactionNo}", claim.TransactionNo);
            }
        }
    }
}
